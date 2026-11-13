using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Vibershot.Core;
using Vibershot.Editing;

namespace Vibershot.UI;

/// <summary>
/// The image editor: a layered vector document over a captured picture, rendered on WPF
/// visuals and undone through one <see cref="History{T}"/>.
/// </summary>
public partial class EditorWindow : Window
{
    private enum Tool { Select, Marquee, Crop, Arrow, Shape, Text, Callout, Stamp, Blur, Image }
    private enum ShapeKind { Rect, Ellipse, Line }
    private enum Handle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate, LineTail, LineHead }
    private enum DragKind { None, Pan, Move, Resize, Rotate, LineEnd, Marquee, PixelSelect, PixelMove, CropDraw, CropResize, CropMove, Create, Target, BlurBrush, BlurBox }

    private const string ProjectFilter = "Vibershot project|*.vibeshot";
    private const string ImageExtensions = "*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.tif;*.tiff";
    private const string ImageFilter = "Images|" + ImageExtensions + "|All files|*.*";
    private const string OpenFilter = "All supported|*.vibeshot;" + ImageExtensions + "|Vibershot project|*.vibeshot|Images|" + ImageExtensions + "|All files|*.*";
    private const double HandleRadius = 8, RotateHandleDistance = 26;

    /// <summary>One store for every editor window so items copied between windows keep their pixels.</summary>
    private static readonly AssetStore SharedAssets = new();

    private readonly AppConfig _config;
    private readonly AssetStore _assets = SharedAssets;
    private readonly History<ImageDocument> _history;
    private readonly HashSet<Guid> _selection = new();
    private Guid? _primary;
    private Guid? _activeLayer;
    private Guid? _liftedId;
    private string? _layersSignature;
    private string? _projectPath;
    /// <summary>The flat image file this document was opened from, when it has no project yet; names the window and seeds the export path.</summary>
    private string? _sourceImagePath;
    /// <summary>True once a flat export was written from this window; drives the flatten reminder on close.</summary>
    private bool _exportedThisSession;
    private Tool _tool = Tool.Select;
    private ShapeKind _shape = ShapeKind.Rect;
    private Rect? _pixelSelection, _cropDraft, _marquee;

    // Callout, stamp and blur tool options; each follows the last choice made in the tool panel.
    private CalloutKind _calloutKind = CalloutKind.Label;
    private string _stampId = StampLibrary.All[0].Id;
    private ObscureMode _obscureMode = ObscureMode.Blur;
    private bool _blurBrush = true;
    private double _blurStrength = 14, _brushSize = 40;
    private readonly List<Point> _brushPoints = new();
    private Rect? _blurBox;
    private Point? _pointer;
    private int _dragTarget;
    private bool _spaceDown, _syncingPanel;

    // The drag gesture in progress. Every push during a gesture shares _dragKey so it undoes as one step.
    private DragKind _drag;
    private Point _dragStartScreen, _dragStartDoc;
    private Vector _dragStartOffset;
    private ImageDocument _dragBase;
    private Handle _dragHandle;
    private Item? _dragOriginal;
    private Rect _dragRect;
    private string _dragKey = "";
    private bool _dragMoved;
    private Guid _createId;

    // In-place text editing.
    private TextBox? _textEditor;
    private Guid? _editingText;
    private string? _textEditKey;

    // Style for the next new item; follows the last change made in the panel.
    private Color? _stroke;
    private Color? _fill;
    private double _strokeWidth;

    private ImageDocument Doc => _history.Current;
    private string DocumentName => _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : _sourceImagePath != null ? Path.GetFileName(_sourceImagePath) : "Untitled";
    /// <summary>File-name stem offered by the export and save dialogs.</summary>
    private string ExportStem => _projectPath != null || _sourceImagePath != null ? Path.GetFileNameWithoutExtension(_projectPath ?? _sourceImagePath!) : $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}";

    public EditorWindow(AppConfig config, BitmapSource? image) : this(config, image != null ? DocumentOps.FromImage(image, SharedAssets, CanvasColor(config)) : DocumentOps.Blank(new Size(1280, 720), CanvasColor(config)), null) { }

    /// <summary>The configured canvas colour for new documents (Settings → Editor).</summary>
    private static Color CanvasColor(AppConfig config)
    {
        uint argb = config.Editor.CanvasBackgroundArgb();
        return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private EditorWindow(AppConfig config, ImageDocument document, string? projectPath)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _config = config;
        _projectPath = projectPath;
        _stroke = TryParseColor(config.Editor.DefaultStrokeColor) ?? Color.FromRgb(0xF0, 0x61, 0x2A);
        _strokeWidth = Math.Clamp(config.Editor.DefaultStrokeWidth, 0, 1000);
        _history = new History<ImageDocument>(document);
        _history.Changed += OnHistoryChanged;
        _dragBase = document;

        Surface.OverlayPainter = PaintOverlay;
        Surface.ViewChanged += () => { ZoomLabel.Text = $"{Surface.Zoom / PixelZoom * 100:0}%"; PositionTextEditor(); };
        FontCombo.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
        StrokeStrip.ValueChanged += c => { _stroke = c; ApplyToSelection(i => i with { Stroke = c, StrokeWidth = c != null && i.StrokeWidth <= 0 ? Math.Max(1, _strokeWidth) : i.StrokeWidth }); };
        FillStrip.ValueChanged += c => { if (SelectedItems().Any(i => i is not StampItem)) _fill = c; ApplyToSelection(i => i is RectItem or EllipseItem or StampItem ? i with { Fill = c } : i); };
        TextColorStrip.ValueChanged += c => { if (c is { } color) ApplyText(t => t with { Color = color }); };
        TextBackgroundStrip.ValueChanged += c => ApplyText(t => t with { Background = c });
        CanvasStrip.ValueChanged += c =>
        {
            Push(DocumentOps.SetBackground(Doc, c ?? Colors.Transparent));
            Status(c == null ? "Canvas is transparent; exports keep the alpha channel." : "Canvas colour changed.");
        };
        foreach (var slider in new[] { StrokeWidthSlider, CornerSlider, HeadSlider, RoundSlider, OpacitySlider, PaddingSlider, LayerOpacitySlider })
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => _history.BreakCoalescing()));
        // The number boxes take values past the sliders' range; each typed value is its own undo step.
        foreach (var box in new[] { StrokeWidthBox, CornerBox, HeadBox, RoundBox, PaddingBox, BlurStrengthBox, BrushSizeBox }) box.ReturnFocusTo = Surface;
        StrokeWidthBox.Committed += v => { SetStrokeWidth(v); _history.BreakCoalescing(); };
        CornerBox.Committed += v => { SetCorner(v); _history.BreakCoalescing(); };
        HeadBox.Committed += v => { SetHead(v); _history.BreakCoalescing(); };
        RoundBox.Committed += v => { SetRoundness(v / 100); _history.BreakCoalescing(); };
        PaddingBox.Committed += v => { SetPadding(v); _history.BreakCoalescing(); };
        BlurStrengthBox.Committed += v => { _blurStrength = v; SyncObscurePanel(); };
        BrushSizeBox.Committed += v => { _brushSize = v; SyncObscurePanel(); };
        StampSearch.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Escape) { Surface.Focus(); e.Handled = true; } };
        Surface.MouseLeave += (_, _) => { if (_pointer != null) { _pointer = null; Surface.InvalidateOverlay(); } };
        string actualKeys = Gesture(config.Editor.ActualSizeShortcut), fitKeys = Gesture(config.Editor.FitShortcut);
        ActualSizeMenu.InputGestureText = actualKeys;
        FitMenu.InputGestureText = fitKeys;
        ActualSizeButton.ToolTip = actualKeys.Length > 0 ? $"Show pixels 1:1 ({actualKeys})" : "Show pixels 1:1";
        FitButton.ToolTip = fitKeys.Length > 0 ? $"Fit the picture in the window ({fitKeys})" : "Fit the picture in the window";
        BuildStampGrid();
        SyncObscurePanel();
        int initialLayer = DocumentOps.ActiveLayerIndex(document);
        _activeLayer = initialLayer >= 0 && initialLayer < document.Layers.Count ? document.Layers[initialLayer].Id : null;

        PreviewKeyDown += Window_PreviewKeyDown;
        PreviewKeyUp += Window_PreviewKeyUp;
        DragOver += Window_DragOver;
        Drop += Window_Drop;

        AboutMenu.Header = $"Vibershot {AppVersion.Current}";
        Surface.SetDocument(document, _assets);
        SetShapeButtons();
        SetTool(Tool.Select);
        UpdateChrome();
        UpdatePanel();
        UpdateLayersPanel();
        Loaded += (_, _) => { Surface.Fit(Doc.ContentRect, maxZoom: PixelZoom); Surface.Focus(); };
        Status($"{Doc.Size.Width:0} × {Doc.Size.Height:0} px • Pick a tool on the left, or paste an image.");
    }

    /// <summary>Opens a saved <c>.vibeshot</c> project in a new editor window.</summary>
    public static EditorWindow OpenProject(AppConfig config, string path)
    {
        var (document, assets) = ProjectFile.Load(path);
        foreach (var id in assets.Ids) SharedAssets.Add(id, assets.Get(id)!);
        return new EditorWindow(config, document, path);
    }

    /// <summary>Opens a flat image file (PNG, JPEG, WebP, GIF, BMP, TIFF) as a new document that remembers where it came from.</summary>
    public static EditorWindow OpenImage(AppConfig config, string path)
    {
        var image = Vibershot.Gallery.ThumbnailCache.LoadImage(path, 0) ?? throw new InvalidOperationException("The file is not a readable image.");
        var window = new EditorWindow(config, image) { _sourceImagePath = path };
        window.UpdateChrome();
        return window;
    }

    private static bool IsProjectFile(string path) => string.Equals(Path.GetExtension(path), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase);

    private static Color? TryParseColor(string text)
    {
        try { return DocumentJson.ParseColor(text); }
        catch (Exception) { return null; }
    }

    // ----- Document state -----

    private void OnHistoryChanged()
    {
        Surface.SetDocument(Doc, _assets);
        // Items on hidden or locked layers cannot be hit, so they leave the selection too.
        var selectable = Doc.Layers.Where(PixelOps.Editable).SelectMany(l => l.Items).Select(i => i.Id).ToHashSet();
        _selection.RemoveWhere(id => !selectable.Contains(id));
        if (_primary is Guid p && !selectable.Contains(p)) _primary = _selection.FirstOrDefault();
        if (_activeLayer is Guid active && Doc.Layers.All(l => l.Id != active)) _activeLayer = null;
        if (_activeLayer == null)
        {
            int fallback = DocumentOps.ActiveLayerIndex(Doc);
            if (fallback >= 0 && fallback < Doc.Layers.Count) _activeLayer = Doc.Layers[fallback].Id;
        }
        UpdateChrome();
        UpdatePanel();
        UpdateLayersPanel();
        Surface.InvalidateOverlay();
    }

    private void Push(ImageDocument doc, string? key = null) => _history.Push(doc, key);
    /// <summary>
    /// The canvas zoom at which one picture pixel covers exactly one pixel of the display. On a
    /// scaled display (125%, 150%) that is less than 1, because WPF lays out in scaled units; the
    /// zoom readout is relative to this, so "100%" always means pixel for pixel.
    /// </summary>
    private double PixelZoom => 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;

    private Item? Find(Guid id) => DocumentOps.FindItem(Doc, id);
    private List<Item> SelectedItems() => Doc.AllItems.Where(i => _selection.Contains(i.Id)).ToList();
    private IEnumerable<Guid> SelectedUnlocked() => _selection.Where(id => !Surface.IsLocked(id));

    private Layer? ActiveLayer => _activeLayer is Guid id ? Doc.Layers.FirstOrDefault(l => l.Id == id) : null;

    /// <summary>Where new items go: the active layer when it is visible and unlocked, else the top-most usable one.</summary>
    private int ActiveLayerIndex()
    {
        if (ActiveLayer is Layer layer && PixelOps.Editable(layer)) return DocumentOps.LayerIndexOf(Doc, layer);
        return DocumentOps.ActiveLayerIndex(Doc);
    }

    private ImageDocument AddToActiveLayer(ImageDocument doc, IEnumerable<Item> items)
    {
        int index = ActiveLayerIndex();
        if (ActiveLayer is Layer layer && !PixelOps.Editable(layer)) Status($"\"{layer.Name}\" is {(layer.Visible ? "locked" : "hidden")}; the item went on \"{doc.Layers[Math.Clamp(index, 0, doc.Layers.Count - 1)].Name}\" instead.");
        return DocumentOps.AddItems(doc, items, index);
    }

    private void Select(Guid id, bool add = false)
    {
        if (!add) _selection.Clear();
        _selection.Add(id);
        _primary = id;
        SelectionChanged();
    }

    private void ToggleSelect(Guid id)
    {
        if (!_selection.Remove(id)) { _selection.Add(id); _primary = id; }
        else if (_primary == id) _primary = _selection.FirstOrDefault();
        SelectionChanged();
    }

    private void ClearSelection()
    {
        if (_selection.Count == 0 && _primary == null) return;
        _selection.Clear();
        _primary = null;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        // Picking an item makes its layer the active one, so the next item lands beside it.
        if (_primary is Guid primary && DocumentOps.LayerIndexOf(Doc, primary) is int index && index >= 0) _activeLayer = Doc.Layers[index].Id;
        UpdateLayersPanel();
        UpdatePanel();
        UpdateCommandState();
        Surface.InvalidateOverlay();
    }

    private void UpdateChrome()
    {
        bool dirty = _history.IsDirty;
        Title = $"Vibershot Editor — {DocumentName}{(dirty ? "*" : "")}";
        DocNameLabel.Text = DocumentName + (dirty ? " •" : "");
        var content = Doc.ContentRect;
        DocInfoLabel.Text = Doc.CropRect != null
            ? $"{Doc.Size.Width:0} × {Doc.Size.Height:0} px · cropped to {content.Width:0} × {content.Height:0}"
            : $"{Doc.Size.Width:0} × {Doc.Size.Height:0} px · {Doc.AllItems.Count()} items · {Doc.Layers.Count} layer{(Doc.Layers.Count == 1 ? "" : "s")}";
        DocSizeLabel.Text = Doc.CropRect != null ? $"{content.Width:0} × {content.Height:0} of {Doc.Size.Width:0} × {Doc.Size.Height:0}" : $"{Doc.Size.Width:0} × {Doc.Size.Height:0}";
        CanvasStrip.Value = Doc.Background.A == 0 ? null : Doc.Background;
        UndoButton.IsEnabled = UndoMenu.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = RedoMenu.IsEnabled = _history.CanRedo;
        ResetCropButton.IsEnabled = ResetCropMenu.IsEnabled = Doc.CropRect != null;
        ApplyCropMenu.IsEnabled = _tool == Tool.Crop && _cropDraft != null;
        ApplyCropButton.Visibility = ApplyCropMenu.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CropRow.Visibility = _tool == Tool.Crop ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommandState();
    }

    /// <summary>Enables the rail buttons and menu items that need a selection (or a marquee of pixels).</summary>
    private void UpdateCommandState()
    {
        bool any = _selection.Count > 0;
        bool pixels = _pixelSelection != null;
        CutButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = any || pixels;
        DuplicateButton.IsEnabled = ForwardButton.IsEnabled = BackwardButton.IsEnabled = any;
        ObscureSelectionButton.IsEnabled = pixels;
    }

    private void Status(string message)
    {
        StatusLabel.Text = message;
        string key = message.StartsWith("Saved", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Exported", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Copied", StringComparison.OrdinalIgnoreCase) ? "Ok"
            : message.Contains("failed", StringComparison.OrdinalIgnoreCase) || message.Contains("Could not", StringComparison.OrdinalIgnoreCase) ? "Danger" : "Ink3";
        StatusDot.Fill = (Brush)FindResource(key);
    }

    // ----- Tools -----

    private void SetTool(Tool tool)
    {
        CommitTextEdit();
        if (tool != Tool.Crop) _cropDraft = null;
        else if (_cropDraft == null) _cropDraft = Doc.CropRect;
        if (tool != Tool.Marquee) _pixelSelection = null;
        if (tool != Tool.Blur) { _brushPoints.Clear(); _blurBox = null; }
        _tool = tool;
        ToggleButton? activeRow = null;
        foreach (var button in ToolRail.Children.OfType<ToggleButton>())
        {
            button.IsChecked = (string)button.Tag == tool.ToString();
            if (button.IsChecked == true) activeRow = button;
        }
        // The accordion sits directly under the active tool's row.
        if (activeRow != null)
        {
            ToolRail.Children.Remove(ToolPanel);
            ToolRail.Children.Insert(ToolRail.Children.IndexOf(activeRow) + 1, ToolPanel);
        }
        ShapeKindRow.Visibility = tool == Tool.Shape ? Visibility.Visible : Visibility.Collapsed;
        StampRow.Visibility = tool == Tool.Stamp ? Visibility.Visible : Visibility.Collapsed;
        ObscureRow.Visibility = tool is Tool.Blur or Tool.Marquee ? Visibility.Visible : Visibility.Collapsed;
        BlurShapeRow.Visibility = tool == Tool.Blur ? Visibility.Visible : Visibility.Collapsed;
        ObscureSelectionButton.Visibility = tool == Tool.Marquee ? Visibility.Visible : Visibility.Collapsed;
        SyncObscurePanel();
        ToolHint.Text = tool switch
        {
            Tool.Select => "Click an item to select it, drag to move it. Shift-click adds to the selection. Double-click text to edit it.",
            Tool.Marquee => "Drag to select pixels. Drag inside the box or press V to lift them; Ctrl+C copies, Ctrl+X cuts, Delete clears.",
            Tool.Crop => "Drag the area to keep.",
            Tool.Arrow => "Drag from the tail to the head. Hold Shift to snap to 45°.",
            Tool.Shape => "Drag on the picture to draw. Hold Shift for a square or circle.",
            Tool.Text => "Click or drag on the picture to place a text box, then type.",
            Tool.Callout => _calloutKind == CalloutKind.Label
                ? "Drag from the thing you are labelling to where the label should sit, then type. A click places a label without an arrow."
                : "Drag from who is speaking to where the bubble should sit, then type.",
            Tool.Stamp => "Pick a stamp, then click to place it or drag to size it. Hold Shift to stretch it.",
            Tool.Blur => "Paint or box over anything that should not be readable. Each pass adds a patch you can move, delete or undo.",
            _ => "Click where the image should go, then pick a file.",
        };
        SetCursorForTool();
        UpdateChrome();
        UpdatePanel();
        Surface.InvalidateOverlay();
        Status(tool switch
        {
            Tool.Select => "Click to select, drag to move. Shift-click adds. Double-click text to edit.",
            Tool.Marquee => "Drag to select pixels. Drag inside the box or press V to move them; Ctrl+C copies, Ctrl+X cuts, Delete clears.",
            Tool.Crop => "Drag the area to keep. Enter crops, Esc cancels; Reset crop restores everything.",
            Tool.Arrow => "Drag from the tail to the head. Shift snaps to 45°.",
            Tool.Shape => $"Drag to draw a {_shape.ToString().ToLowerInvariant()}. Press S again to switch shape.",
            Tool.Text => "Click or drag to place a text box, then type.",
            Tool.Callout => "Drag from what the callout points at to where it should sit. Press O again to switch style.",
            Tool.Stamp => "Click to place the stamp, or drag to size it.",
            Tool.Blur => _blurBrush ? "Paint over what should be hidden." : "Drag a box over what should be hidden.",
            _ => "Click where the image should go.",
        });
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tool = Enum.Parse<Tool>((string)((ToggleButton)sender).Tag);
        // Clicking the active Shape row again steps through rectangle, ellipse and line, like pressing S.
        if (tool == Tool.Shape && _tool == Tool.Shape) SetShape((ShapeKind)(((int)_shape + 1) % 3));
        else if (tool == Tool.Callout && _tool == Tool.Callout) NextCalloutKind();
        else SetTool(tool);
        Surface.Focus();
    }

    private void NextCalloutKind()
    {
        _calloutKind = (CalloutKind)(((int)_calloutKind + 1) % 4);
        SetTool(Tool.Callout);
    }

    private void CropTool_Click(object sender, RoutedEventArgs e) { SetTool(Tool.Crop); Surface.Focus(); }
    private void ImageTool_Click(object sender, RoutedEventArgs e) { SetTool(Tool.Image); Surface.Focus(); }

    private void ShapeKind_Click(object sender, RoutedEventArgs e)
    {
        SetShape(Enum.Parse<ShapeKind>((string)((ToggleButton)sender).Tag));
        Surface.Focus();
    }

    private void SetShape(ShapeKind kind)
    {
        _shape = kind;
        SetShapeButtons();
        SetTool(Tool.Shape);
    }

    private void SetShapeButtons()
    {
        ShapeToolIcon.Data = Geometry.Parse(_shape switch
        {
            ShapeKind.Ellipse => "M10,3 A7,7 0 1 1 9.99,3 Z",
            ShapeKind.Line => "M3,17 L17,3",
            _ => "M2,4 H18 V16 H2 Z",
        });
        foreach (var button in ShapeKinds.Children.OfType<ToggleButton>()) button.IsChecked = (string)button.Tag == _shape.ToString();
    }

    // ----- Keyboard -----

    private static bool IsTypingTarget() => Keyboard.FocusedElement is TextBox;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsTypingTarget())
        {
            if (key == Key.Escape && _textEditor != null && _textEditor.IsKeyboardFocusWithin) { CommitTextEdit(); Surface.Focus(); e.Handled = true; }
            return;
        }
        // The two view shortcuts are configurable (Settings → Editor), so they are matched before the fixed keys.
        if (Matches(_config.Editor.ActualSizeShortcut, key)) { Surface.Center(Doc.ContentRect, PixelZoom); e.Handled = true; return; }
        if (Matches(_config.Editor.FitShortcut, key)) { Surface.Fit(Doc.ContentRect); e.Handled = true; return; }
        // Alt belongs to the menu bar's access keys (Alt+F opens File), not to the single-letter tool shortcuts.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (key == Key.Space)
        {
            if (!_spaceDown) { _spaceDown = true; Surface.Cursor = Cursors.ScrollAll; }
            e.Handled = true;
            return;
        }
        if (ctrl)
        {
            switch (key)
            {
                case Key.Z: if (shift) Redo(); else Undo(); break;
                case Key.Y: Redo(); break;
                case Key.A: SelectAll(); break;
                case Key.C: if (shift) CopyImage(); else Copy(cut: false); break;
                case Key.X: Copy(cut: true); break;
                case Key.V: Paste(); break;
                case Key.D: Duplicate(); break;
                case Key.S: SaveProject(saveAs: shift); break;
                case Key.O: OpenDialog(); break;
                case Key.E: Export(); break;
                case Key.N: if (shift) (Application.Current as App)?.OpenEditorFromClipboard(); else NewCanvas(this, _config); break;
                case Key.G: (Application.Current as App)?.ShowGallery(); break;
                case Key.W: Close(); break;
                case Key.D1 or Key.NumPad1: Surface.Center(Doc.ContentRect, PixelZoom); break;
                case Key.OemPlus or Key.Add: Surface.ZoomAt(new Point(Surface.ActualWidth / 2, Surface.ActualHeight / 2), Surface.Zoom * 1.25); break;
                case Key.OemMinus or Key.Subtract: Surface.ZoomAt(new Point(Surface.ActualWidth / 2, Surface.ActualHeight / 2), Surface.Zoom / 1.25); break;
                case Key.OemCloseBrackets: Reorder(shift ? ZOrderMove.BringToFront : ZOrderMove.BringForward); break;
                case Key.OemOpenBrackets: Reorder(shift ? ZOrderMove.SendToBack : ZOrderMove.SendBackward); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        double step = shift ? 10 : 1;
        switch (key)
        {
            case Key.V: if (_pixelSelection != null) LiftSelection(); else SetTool(Tool.Select); break;
            case Key.M: SetTool(Tool.Marquee); break;
            case Key.C: SetTool(Tool.Crop); break;
            case Key.A: SetTool(Tool.Arrow); break;
            case Key.S: if (_tool == Tool.Shape) SetShape((ShapeKind)(((int)_shape + 1) % 3)); else SetTool(Tool.Shape); break;
            case Key.T: SetTool(Tool.Text); break;
            case Key.I: SetTool(Tool.Image); break;
            case Key.O: if (_tool == Tool.Callout) NextCalloutKind(); else SetTool(Tool.Callout); break;
            case Key.K: SetTool(Tool.Stamp); break;
            case Key.B: SetTool(Tool.Blur); break;
            case Key.Delete or Key.Back: DeleteSelection(); break;
            case Key.Escape: Cancel(); break;
            case Key.Enter: if (_tool == Tool.Crop && _cropDraft != null) ApplyCrop(); else return; break;
            case Key.Left: Nudge(new Vector(-step, 0)); break;
            case Key.Right: Nudge(new Vector(step, 0)); break;
            case Key.Up: Nudge(new Vector(0, -step)); break;
            case Key.Down: Nudge(new Vector(0, step)); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { _spaceDown = false; if (_drag != DragKind.Pan) SetCursorForTool(); }
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) _history.BreakCoalescing();
    }

    private void SetCursorForTool() => Surface.Cursor = _tool switch { Tool.Text => Cursors.IBeam, Tool.Select or Tool.Image => Cursors.Arrow, _ => Cursors.Cross };

    /// <summary>The shortcut as the user reads it ("Ctrl+0"), or "" when it is not set or unreadable.</summary>
    private static string Gesture(string? shortcut) => HotkeyGesture.TryParse(shortcut, out var gesture) ? gesture.ToString() : "";

    /// <summary>True when the pressed key, with the modifiers held right now, is the configured shortcut. A digit matches its number-pad twin.</summary>
    private static bool Matches(string? shortcut, Key key)
    {
        if (!HotkeyGesture.TryParse(shortcut, out var gesture) || gesture.IsEmpty) return false;
        if (HotkeyManager.CurrentModifiers() != gesture.Modifiers || HotkeyManager.ToKey(gesture.Key) is not Key wanted) return false;
        return key == wanted || (wanted is >= Key.D0 and <= Key.D9 && key == Key.NumPad0 + (wanted - Key.D0));
    }

    private void Cancel()
    {
        if (_tool == Tool.Crop && _cropDraft != null) { _cropDraft = null; UpdateChrome(); Surface.InvalidateOverlay(); return; }
        if (_pixelSelection != null) { _pixelSelection = null; Surface.InvalidateOverlay(); return; }
        if (_selection.Count > 0) { ClearSelection(); return; }
        if (_tool != Tool.Select) SetTool(Tool.Select);
    }

    private void Nudge(Vector delta)
    {
        var ids = SelectedUnlocked().ToList();
        if (ids.Count == 0) return;
        Push(DocumentOps.MoveItems(Doc, ids, delta), "nudge");
    }

    private void Undo() { CommitTextEdit(); _history.Undo(); }
    private void Redo() { CommitTextEdit(); _history.Redo(); }

    private void SelectAll()
    {
        _selection.Clear();
        foreach (var item in Doc.AllItems) if (!Surface.IsLocked(item.Id)) _selection.Add(item.Id);
        _primary = _selection.LastOrDefault();
        SelectionChanged();
    }

    private void DeleteSelection()
    {
        CommitTextEdit();
        if (_pixelSelection is Rect pixels) { ErasePixels(pixels, "Cleared"); return; }
        var ids = SelectedUnlocked().ToList();
        if (ids.Count == 0) return;
        Push(DocumentOps.RemoveItems(Doc, ids));
        ClearSelection();
    }

    private void Reorder(ZOrderMove move)
    {
        if (_primary is not Guid id) return;
        var doc = Doc;
        foreach (var item in SelectedItems()) doc = DocumentOps.Reorder(doc, item.Id, move);
        if (_selection.Count == 1) doc = DocumentOps.Reorder(Doc, id, move);
        Push(doc);
    }

    // ----- Mouse -----

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Surface.ZoomAt(e.GetPosition(Surface), Surface.Zoom * (e.Delta > 0 ? 1.25 : 0.8));
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Surface.Pan(new Vector(e.Delta, 0));
        else Surface.Pan(new Vector(0, e.Delta));
        e.Handled = true;
    }

    private void StartDrag(DragKind kind, Point screen, Point doc)
    {
        _drag = kind;
        _dragStartScreen = screen;
        _dragStartDoc = doc;
        _dragStartOffset = Surface.Offset;
        _dragBase = Doc;
        _dragKey = "drag:" + Guid.NewGuid().ToString("N");
        _dragMoved = false;
        Surface.CaptureMouse();
    }

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Surface.Focus();
        CommitTextEdit();
        var screen = e.GetPosition(Surface);
        var doc = Surface.ToDoc(screen);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _spaceDown))
        {
            StartDrag(DragKind.Pan, screen, doc);
            Surface.Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right)
        {
            if (_tool == Tool.Select && Surface.HitItem(screen) is Guid under && !_selection.Contains(under)) Select(under);
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2 && _tool == Tool.Select && Surface.HitItem(screen) is Guid hitText && Find(hitText) is TextItem)
        {
            BeginTextEdit(hitText);
            e.Handled = true;
            return;
        }

        switch (_tool)
        {
            case Tool.Select:
            {
                if (HitTarget(screen) is int tip && _primary is Guid owner && Find(owner) is CalloutItem pointing)
                {
                    StartDrag(DragKind.Target, screen, doc);
                    _dragOriginal = pointing;
                    _dragTarget = tip;
                    break;
                }
                var handle = HitHandle(screen);
                if (handle != Handle.None && _primary is Guid primary && Find(primary) is Item target)
                {
                    StartDrag(handle switch { Handle.Rotate => DragKind.Rotate, Handle.LineTail or Handle.LineHead => DragKind.LineEnd, _ => DragKind.Resize }, screen, doc);
                    _dragHandle = handle;
                    _dragOriginal = target;
                    break;
                }
                var hit = Surface.HitItem(screen);
                if (hit is Guid id)
                {
                    if (shift) ToggleSelect(id);
                    else if (!_selection.Contains(id)) Select(id);
                    if (_selection.Contains(id)) StartDrag(DragKind.Move, screen, doc);
                }
                else
                {
                    if (!shift) ClearSelection();
                    StartDrag(DragKind.Marquee, screen, doc);
                }
                break;
            }
            case Tool.Marquee:
                if (_pixelSelection is Rect pixels && pixels.Contains(doc))
                {
                    StartDrag(DragKind.PixelMove, screen, doc);
                    _liftedId = null;
                    break;
                }
                _pixelSelection = null;
                StartDrag(DragKind.PixelSelect, screen, doc);
                break;
            case Tool.Crop:
            {
                if (_cropDraft is Rect crop)
                {
                    var handle = HitRectHandle(screen, Surface.ToScreen(crop));
                    if (handle != Handle.None) { StartDrag(DragKind.CropResize, screen, doc); _dragHandle = handle; _dragRect = crop; break; }
                    if (crop.Contains(doc)) { StartDrag(DragKind.CropMove, screen, doc); _dragRect = crop; break; }
                }
                StartDrag(DragKind.CropDraw, screen, doc);
                break;
            }
            case Tool.Arrow or Tool.Shape or Tool.Text or Tool.Callout or Tool.Stamp:
                StartDrag(DragKind.Create, screen, doc);
                _createId = Guid.NewGuid();
                break;
            case Tool.Blur:
                _brushPoints.Clear();
                _blurBox = null;
                if (_blurBrush) { _brushPoints.Add(doc); StartDrag(DragKind.BlurBrush, screen, doc); Surface.InvalidateOverlay(); }
                else StartDrag(DragKind.BlurBox, screen, doc);
                break;
            case Tool.Image:
                PlaceImageFromFile(doc);
                break;
        }
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        var screen = e.GetPosition(Surface);
        if (_tool == Tool.Blur && _blurBrush) { _pointer = screen; if (_drag == DragKind.None) Surface.InvalidateOverlay(); }
        if (_drag == DragKind.None) { UpdateHoverCursor(screen); return; }
        var doc = Surface.ToDoc(screen);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!_dragMoved && _drag != DragKind.Pan && (screen - _dragStartScreen).Length < 3) return;
        _dragMoved = true;
        var delta = doc - _dragStartDoc;
        var canvas = new Rect(Doc.Size);

        switch (_drag)
        {
            case DragKind.Pan:
                Surface.SetView(Surface.Zoom, _dragStartOffset + (screen - _dragStartScreen));
                break;
            case DragKind.Move:
            {
                if (shift) { if (Math.Abs(delta.X) > Math.Abs(delta.Y)) delta.Y = 0; else delta.X = 0; }
                delta = new Vector(Math.Round(delta.X), Math.Round(delta.Y));
                // Alt leaves a callout's pointers aimed where they were while its box moves.
                Push(DocumentOps.MoveItems(_dragBase, SelectedUnlocked(), delta, pinTargets: Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)), _dragKey);
                break;
            }
            case DragKind.Target:
            {
                var callout = (CalloutItem)_dragOriginal!;
                if (_dragTarget < 0 || _dragTarget >= callout.Targets.Count) break;
                var tip = new Point(Math.Round(doc.X), Math.Round(doc.Y));
                Push(DocumentOps.ReplaceItem(_dragBase, callout with { Targets = callout.Targets.SetItem(_dragTarget, tip) }), _dragKey);
                break;
            }
            case DragKind.BlurBrush:
                if (_brushPoints.Count == 0 || (_brushPoints[^1] - doc).Length >= 1) _brushPoints.Add(doc);
                Surface.InvalidateOverlay();
                break;
            case DragKind.BlurBox:
                _blurBox = Snap(Rect.Intersect(new Rect(_dragStartDoc, doc), canvas));
                Surface.InvalidateOverlay();
                break;
            case DragKind.Resize:
                Push(DocumentOps.ReplaceItem(_dragBase, ResizeItem(_dragOriginal!, _dragHandle, _dragStartDoc, doc, shift)), _dragKey);
                break;
            case DragKind.Rotate:
            {
                var c = _dragOriginal!.Center;
                double degrees = _dragOriginal.Rotation + (Math.Atan2(doc.Y - c.Y, doc.X - c.X) - Math.Atan2(_dragStartDoc.Y - c.Y, _dragStartDoc.X - c.X)) * 180 / Math.PI;
                if (shift) degrees = Math.Round(degrees / 15) * 15;
                degrees = Math.Round(((degrees + 180) % 360 + 360) % 360 - 180, 1);
                Push(DocumentOps.SetRotation(_dragBase, _dragOriginal.Id, degrees), _dragKey);
                break;
            }
            case DragKind.LineEnd:
            {
                var line = (LineItem)_dragOriginal!;
                var point = new Point(Math.Round(doc.X), Math.Round(doc.Y));
                var moved = _dragHandle == Handle.LineTail
                    ? line.WithPoints(shift ? SnapAngle(line.Head, point) : point, line.Head)
                    : line.WithPoints(line.Tail, shift ? SnapAngle(line.Tail, point) : point);
                Push(DocumentOps.ReplaceItem(_dragBase, moved), _dragKey);
                break;
            }
            case DragKind.Marquee:
                _marquee = new Rect(_dragStartDoc, doc);
                Surface.InvalidateOverlay();
                break;
            case DragKind.PixelSelect:
                _pixelSelection = Snap(Rect.Intersect(new Rect(_dragStartDoc, doc), canvas));
                Surface.InvalidateOverlay();
                break;
            case DragKind.PixelMove:
            {
                // First movement lifts the pixels into a floating image item; the rest of the drag moves it.
                if (_liftedId == null)
                {
                    if (_pixelSelection is not Rect region) { EndDrag(doc); break; }
                    var result = PixelOps.Lift(_dragBase, _assets, region);
                    if (result.Lifted is not ImageItem lifted)
                    {
                        EndDrag(doc);
                        Status("No picture pixels under the selection to move. Rotated images cannot be cut up.");
                        break;
                    }
                    _dragBase = result.Document;
                    _liftedId = lifted.Id;
                    _pixelSelection = null;
                    Push(_dragBase, _dragKey);
                    Select(lifted.Id);
                }
                if (shift) { if (Math.Abs(delta.X) > Math.Abs(delta.Y)) delta.Y = 0; else delta.X = 0; }
                delta = new Vector(Math.Round(delta.X), Math.Round(delta.Y));
                Push(DocumentOps.MoveItems(_dragBase, [_liftedId.Value], delta), _dragKey);
                break;
            }
            case DragKind.CropDraw:
                _cropDraft = Snap(Rect.Intersect(new Rect(_dragStartDoc, shift ? SquareEnd(_dragStartDoc, doc) : doc), canvas));
                UpdateChrome();
                Surface.InvalidateOverlay();
                break;
            case DragKind.CropResize:
                _cropDraft = Snap(Rect.Intersect(ResizeRect(_dragRect, _dragHandle, delta, shift), canvas));
                Surface.InvalidateOverlay();
                break;
            case DragKind.CropMove:
            {
                var moved = Rect.Offset(_dragRect, Math.Round(delta.X), Math.Round(delta.Y));
                moved.X = Math.Clamp(moved.X, 0, Math.Max(0, canvas.Width - moved.Width));
                moved.Y = Math.Clamp(moved.Y, 0, Math.Max(0, canvas.Height - moved.Height));
                _cropDraft = moved;
                Surface.InvalidateOverlay();
                break;
            }
            case DragKind.Create:
                Push(DocumentOps.AddItem(_dragBase, BuildItem(_dragStartDoc, doc, shift), ActiveLayerIndex()), _dragKey);
                break;
        }
    }

    private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragKind.None) return;
        if (e.ChangedButton == MouseButton.Right) return;
        EndDrag(Surface.ToDoc(e.GetPosition(Surface)));
        e.Handled = true;
    }

    private void Surface_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_drag != DragKind.None) EndDrag(Surface.ToDoc(e.GetPosition(Surface)));
    }

    private void EndDrag(Point doc)
    {
        var kind = _drag;
        _drag = DragKind.None;
        if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (kind)
        {
            case DragKind.Pan:
                SetCursorForTool();
                break;
            case DragKind.Move or DragKind.Resize or DragKind.Rotate or DragKind.LineEnd or DragKind.Target:
                _history.BreakCoalescing();
                break;
            case DragKind.BlurBrush:
                ApplyBrushStroke();
                break;
            case DragKind.BlurBox:
                if (_blurBox is { Width: >= 2, Height: >= 2 } blurred) ApplyObscure(blurred);
                _blurBox = null;
                Surface.InvalidateOverlay();
                break;
            case DragKind.Marquee:
            {
                if (_marquee is Rect box && _dragMoved)
                {
                    if (!shift) _selection.Clear();
                    foreach (var item in Doc.AllItems)
                        if (!Surface.IsLocked(item.Id) && box.IntersectsWith(DocumentOps.VisualBounds(item))) _selection.Add(item.Id);
                    _primary = _selection.LastOrDefault();
                }
                _marquee = null;
                SelectionChanged();
                break;
            }
            case DragKind.PixelSelect:
                if (_pixelSelection is { Width: < 1 } or { Height: < 1 }) _pixelSelection = null;
                if (_pixelSelection is Rect px) Status($"{px.Width:0} × {px.Height:0} px selected • drag inside or press V to move, Ctrl+C copies, Ctrl+X cuts, Delete clears, or blur it from the tool panel.");
                UpdateCommandState();
                Surface.InvalidateOverlay();
                break;
            case DragKind.PixelMove:
                if (_liftedId is Guid liftedId && Find(liftedId) is Item floating)
                {
                    _history.BreakCoalescing();
                    SetTool(Tool.Select);
                    Select(liftedId);
                    Status($"Moved {floating.Bounds.Width:0} × {floating.Bounds.Height:0} px as a floating image. Drag it further, resize it, or press Delete to drop it.");
                }
                _liftedId = null;
                break;
            case DragKind.CropDraw:
                if (_cropDraft is { Width: < 1 } or { Height: < 1 }) _cropDraft = null;
                UpdateChrome();
                Surface.InvalidateOverlay();
                break;
            case DragKind.CropResize or DragKind.CropMove:
                Surface.InvalidateOverlay();
                break;
            case DragKind.Create:
                FinishCreate(doc);
                break;
        }
    }

    private void FinishCreate(Point doc)
    {
        if (!_dragMoved)
        {
            if (_tool == Tool.Stamp)
            {
                // A click places the stamp at a size that suits the picture.
                double side = Math.Clamp(Math.Round(Math.Min(Doc.Size.Width, Doc.Size.Height) / 9), 32, 320);
                var stamp = new StampItem { Id = _createId, StampId = _stampId, Bounds = new Rect(Math.Round(doc.X - side / 2), Math.Round(doc.Y - side / 2), side, side) };
                Push(AddToActiveLayer(_dragBase, [stamp]), _dragKey);
                Select(stamp.Id);
                _history.BreakCoalescing();
                return;
            }
            if (_tool == Tool.Callout)
            {
                // A click drops a label where it was clicked; a bubble sits up and to the right with its tail on the click.
                var at = new Point(Math.Round(doc.X), Math.Round(doc.Y));
                var callout = _calloutKind == CalloutKind.Label ? NewCallout(at, null) : NewCallout(at + new Vector(150, -110), at);
                Push(AddToActiveLayer(_dragBase, [callout]), _dragKey);
                Select(callout.Id);
                BeginTextEdit(callout.Id, _dragKey);
                return;
            }
            if (_tool != Tool.Text) return;
            var text = NewText(new Rect(Math.Round(doc.X), Math.Round(doc.Y), 240, 10));
            text = text with { Bounds = new Rect(text.Bounds.X, text.Bounds.Y, 240, DocumentRenderer.MeasureText(text).Height) };
            Push(AddToActiveLayer(_dragBase, [text]), _dragKey);
            Select(text.Id);
            BeginTextEdit(text.Id, _dragKey);
            return;
        }
        if (Find(_createId) is not Item item) return;
        if (item is not TextItem && item.Bounds.Width < 3 && item.Bounds.Height < 3)
        {
            _history.Rollback(_dragKey);
            return;
        }
        Select(item.Id);
        if (item is TextItem) BeginTextEdit(item.Id, _dragKey);
        else _history.BreakCoalescing();
    }

    private void UpdateHoverCursor(Point screen)
    {
        if (_spaceDown) { Surface.Cursor = Cursors.ScrollAll; return; }
        if (_tool == Tool.Select)
        {
            if (HitTarget(screen) != null) { Surface.Cursor = Cursors.Hand; return; }
            Surface.Cursor = HitHandle(screen) switch
            {
                Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
                Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
                Handle.Top or Handle.Bottom => Cursors.SizeNS,
                Handle.Left or Handle.Right => Cursors.SizeWE,
                Handle.Rotate or Handle.LineTail or Handle.LineHead => Cursors.Hand,
                _ => Surface.HitItem(screen) != null ? Cursors.SizeAll : Cursors.Arrow,
            };
        }
        else if (_tool == Tool.Marquee)
        {
            Surface.Cursor = _pixelSelection is Rect pixels && pixels.Contains(Surface.ToDoc(screen)) ? Cursors.SizeAll : Cursors.Cross;
        }
        else if (_tool == Tool.Crop && _cropDraft is Rect crop)
        {
            var handle = HitRectHandle(screen, Surface.ToScreen(crop));
            Surface.Cursor = handle switch
            {
                Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
                Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
                Handle.Top or Handle.Bottom => Cursors.SizeNS,
                Handle.Left or Handle.Right => Cursors.SizeWE,
                _ => crop.Contains(Surface.ToDoc(screen)) ? Cursors.SizeAll : Cursors.Cross,
            };
        }
    }

    // ----- Geometry helpers -----

    private static Rect Snap(Rect r) => r.IsEmpty ? r : new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));

    private static Point SquareEnd(Point start, Point end)
    {
        double size = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        return new Point(start.X + Math.Sign(end.X - start.X) * size, start.Y + Math.Sign(end.Y - start.Y) * size);
    }

    private static Point SnapAngle(Point start, Point end)
    {
        var v = end - start;
        if (v.Length < 0.01) return end;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        var snapped = start + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
        return new Point(Math.Round(snapped.X), Math.Round(snapped.Y));
    }

    private static Rect ResizeRect(Rect r, Handle h, Vector d, bool keepAspect)
    {
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        bool l = h is Handle.TopLeft or Handle.Left or Handle.BottomLeft, rt = h is Handle.TopRight or Handle.Right or Handle.BottomRight;
        bool t = h is Handle.TopLeft or Handle.Top or Handle.TopRight, b = h is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;
        if (l) left += d.X;
        if (rt) right += d.X;
        if (t) top += d.Y;
        if (b) bottom += d.Y;
        if (keepAspect && r.Width > 0 && r.Height > 0 && (l || rt) && (t || b))
        {
            double height = Math.Abs(right - left) / (r.Width / r.Height) * (bottom >= top ? 1 : -1);
            if (t) top = bottom - height; else bottom = top + height;
        }
        return new Rect(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top));
    }

    /// <summary>Resizes in the item's own rotated frame so a tilted box grows along its edges.</summary>
    private static Item ResizeItem(Item item, Handle handle, Point startDoc, Point currentDoc, bool keepAspect)
    {
        var c = item.Center;
        var inverse = new RotateTransform(-item.Rotation, c.X, c.Y);
        var local = ResizeRect(item.Bounds, handle, inverse.Transform(currentDoc) - inverse.Transform(startDoc), keepAspect);
        Rect bounds;
        if (item.Rotation == 0) bounds = Snap(local);
        else
        {
            var centre = new RotateTransform(item.Rotation, c.X, c.Y).Transform(new Point(local.X + local.Width / 2, local.Y + local.Height / 2));
            bounds = new Rect(centre.X - local.Width / 2, centre.Y - local.Height / 2, local.Width, local.Height);
        }
        var resized = item with { Bounds = bounds };
        if (resized is TextItem text)
        {
            double height = Math.Max(bounds.Height, DocumentRenderer.MeasureText(text).Height);
            resized = text with { Bounds = new Rect(bounds.X, bounds.Y, bounds.Width, height) };
        }
        return resized;
    }

    private IEnumerable<(Handle Handle, Point Screen)> HandlePositions(Item item)
    {
        if (item is LineItem line)
        {
            yield return (Handle.LineTail, Surface.ToScreen(line.Tail));
            yield return (Handle.LineHead, Surface.ToScreen(line.Head));
            yield break;
        }
        var b = item.Bounds;
        var rotate = new RotateTransform(item.Rotation, item.Center.X, item.Center.Y);
        Point P(double x, double y) => Surface.ToScreen(rotate.Transform(new Point(x, y)));
        double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
        yield return (Handle.TopLeft, P(b.Left, b.Top));
        yield return (Handle.Top, P(cx, b.Top));
        yield return (Handle.TopRight, P(b.Right, b.Top));
        yield return (Handle.Right, P(b.Right, cy));
        yield return (Handle.BottomRight, P(b.Right, b.Bottom));
        yield return (Handle.Bottom, P(cx, b.Bottom));
        yield return (Handle.BottomLeft, P(b.Left, b.Bottom));
        yield return (Handle.Left, P(b.Left, cy));
        yield return (Handle.Rotate, P(cx, b.Top - RotateHandleDistance / Surface.Zoom));
    }

    private Handle HitHandle(Point screen)
    {
        if (_selection.Count != 1 || _primary is not Guid id || Find(id) is not Item item || Surface.IsLocked(id) || _editingText == id) return Handle.None;
        foreach (var (handle, point) in HandlePositions(item))
            if ((screen - point).Length <= HandleRadius) return handle;
        return Handle.None;
    }

    /// <summary>The index of the selected callout's pointer tip under a screen point, or null.</summary>
    private int? HitTarget(Point screen)
    {
        if (_selection.Count != 1 || _primary is not Guid id || Find(id) is not CalloutItem callout || Surface.IsLocked(id) || _editingText == id) return null;
        for (int i = callout.Targets.Count - 1; i >= 0; i--)
            if ((screen - Surface.ToScreen(callout.Targets[i])).Length <= HandleRadius + 2) return i;
        return null;
    }

    private static Handle HitRectHandle(Point screen, Rect r)
    {
        var handles = new (Handle, Point)[]
        {
            (Handle.TopLeft, r.TopLeft), (Handle.Top, new Point(r.X + r.Width / 2, r.Top)), (Handle.TopRight, r.TopRight), (Handle.Right, new Point(r.Right, r.Y + r.Height / 2)),
            (Handle.BottomRight, r.BottomRight), (Handle.Bottom, new Point(r.X + r.Width / 2, r.Bottom)), (Handle.BottomLeft, r.BottomLeft), (Handle.Left, new Point(r.Left, r.Y + r.Height / 2)),
        };
        foreach (var (handle, point) in handles)
            if ((screen - point).Length <= HandleRadius) return handle;
        return Handle.None;
    }

    // ----- Item creation -----

    private Item BuildItem(Point start, Point end, bool shift)
    {
        start = new Point(Math.Round(start.X), Math.Round(start.Y));
        end = new Point(Math.Round(end.X), Math.Round(end.Y));
        var stroke = _stroke ?? Color.FromRgb(0xF0, 0x61, 0x2A);
        double width = Math.Max(1, _strokeWidth);
        switch (_tool)
        {
            case Tool.Arrow:
                return new ArrowItem { Id = _createId, Stroke = stroke, StrokeWidth = width }.WithPoints(start, shift ? SnapAngle(start, end) : end);
            case Tool.Shape when _shape == ShapeKind.Line:
                return new LineItem { Id = _createId, Stroke = stroke, StrokeWidth = width }.WithPoints(start, shift ? SnapAngle(start, end) : end);
            case Tool.Shape:
            {
                var bounds = new Rect(start, shift ? SquareEnd(start, end) : end);
                return _shape == ShapeKind.Rect
                    ? new RectItem { Id = _createId, Bounds = bounds, Stroke = stroke, StrokeWidth = width, Fill = _fill }
                    : new EllipseItem { Id = _createId, Bounds = bounds, Stroke = stroke, StrokeWidth = width, Fill = _fill };
            }
            case Tool.Callout:
                // Dragged from the thing being pointed at to where the callout should sit.
                return NewCallout(end, start);
            case Tool.Stamp:
            {
                // Stamps keep their shape unless Shift stretches them.
                var bounds = new Rect(start, shift ? end : SquareEnd(start, end));
                return new StampItem { Id = _createId, StampId = _stampId, Bounds = bounds };
            }
            default:
            {
                var bounds = new Rect(start, end);
                var text = NewText(bounds);
                return text with { Bounds = new Rect(bounds.X, bounds.Y, Math.Max(bounds.Width, 40), Math.Max(bounds.Height, DocumentRenderer.MeasureText(text).Height)) };
            }
        }
    }

    private TextItem NewText(Rect bounds) => new()
    {
        Id = _createId, Bounds = bounds, Text = "",
        FontFamily = string.IsNullOrWhiteSpace(_config.Editor.DefaultFontFamily) ? "Segoe UI" : _config.Editor.DefaultFontFamily,
        FontSize = Math.Clamp(_config.Editor.DefaultFontSize, 6, 2000),
        Color = _stroke ?? Color.FromRgb(0x15, 0x18, 0x1C),
    };

    /// <summary>A callout in the current style, centred on <paramref name="centre"/> and pointing at <paramref name="target"/> when one is given.</summary>
    private CalloutItem NewCallout(Point centre, Point? target)
    {
        bool round = _calloutKind is CalloutKind.Oval or CalloutKind.Thought;
        var item = new CalloutItem
        {
            Id = _createId, Kind = _calloutKind, Text = "", Alignment = TextAlign.Center, Padding = 10,
            FontFamily = string.IsNullOrWhiteSpace(_config.Editor.DefaultFontFamily) ? "Segoe UI" : _config.Editor.DefaultFontFamily,
            FontSize = Math.Clamp(_config.Editor.DefaultFontSize, 6, 2000),
            Color = Color.FromRgb(0x15, 0x18, 0x1C), Background = Colors.White,
            Stroke = _stroke ?? Color.FromRgb(0xF0, 0x61, 0x2A), StrokeWidth = Math.Max(1, _strokeWidth),
            CornerRadius = _calloutKind == CalloutKind.Speech ? 18 : 8,
            HeadSize = Math.Max(10, _strokeWidth * 3),
            Bounds = new Rect(0, 0, round ? 240 : 200, 10),
        };
        double width = item.Bounds.Width, height = Math.Max(DocumentRenderer.MeasureText(item).Height, round ? 120 : 56);
        return item with
        {
            Bounds = new Rect(Math.Round(centre.X - width / 2), Math.Round(centre.Y - height / 2), width, Math.Ceiling(height)),
            Targets = target is Point tip ? [new Point(Math.Round(tip.X), Math.Round(tip.Y))] : [],
        };
    }

    private void PlaceImage(BitmapSource bitmap, Point? at = null, string? key = null)
    {
        var frozen = AssetStore.Freeze(bitmap);
        var id = _assets.Add(frozen);
        double w = frozen.PixelWidth, h = frozen.PixelHeight;
        var content = Doc.ContentRect;
        double scale = Math.Min(1, Math.Min(content.Width / w, content.Height / h));
        if (scale < 1) { w = Math.Max(1, Math.Round(w * scale)); h = Math.Max(1, Math.Round(h * scale)); }
        var centre = at ?? Surface.ViewCenter;
        var item = new ImageItem { AssetId = id, Bounds = new Rect(Math.Round(centre.X - w / 2), Math.Round(centre.Y - h / 2), w, h) };
        Push(AddToActiveLayer(Doc, [item]), key);
        if (_tool != Tool.Select) SetTool(Tool.Select);
        Select(item.Id);
        Status($"Placed a {frozen.PixelWidth} × {frozen.PixelHeight} px image{(scale < 1 ? " scaled to fit" : "")}.");
    }

    private void PlaceImageFromFile(Point at)
    {
        var dialog = new OpenFileDialog { Filter = ImageFilter, Title = "Place image" };
        if (dialog.ShowDialog(this) != true) return;
        var bitmap = LoadImageFile(dialog.FileName);
        if (bitmap == null) { Status("Could not open " + Path.GetFileName(dialog.FileName)); return; }
        PlaceImage(bitmap, at);
    }

    /// <summary>Decodes any supported image file at full size (WPF codecs, plus Skia for WebP); null when unreadable.</summary>
    private static BitmapSource? LoadImageFile(string path) => Vibershot.Gallery.ThumbnailCache.LoadImage(path, 0);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var point = Surface.ToDoc(e.GetPosition(Surface));
        var key = "drop:" + Guid.NewGuid().ToString("N");
        int placed = 0;
        foreach (var file in files)
        {
            if (IsProjectFile(file)) { OpenFileWindow(file); continue; }
            var bitmap = LoadImageFile(file);
            if (bitmap == null) { Status("Could not open " + Path.GetFileName(file)); continue; }
            PlaceImage(bitmap, point + new Vector(20, 20) * placed, key);
            placed++;
        }
        _history.BreakCoalescing();
        e.Handled = true;
    }

    // ----- Text editing -----

    private void BeginTextEdit(Guid id, string? coalesceKey = null)
    {
        if (Find(id) is not TextItem item || Surface.IsLocked(id)) return;
        CommitTextEdit();
        _editingText = id;
        _textEditKey = coalesceKey;
        var box = new TextBox
        {
            Text = item.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Style = null, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = new SolidColorBrush(item.Color), CaretBrush = (Brush)FindResource("Accent"),
            SelectionBrush = (Brush)FindResource("AccentSoft"), SelectionOpacity = 0.6,
            FontFamily = new FontFamily(item.FontFamily), FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextAlignment = item.Alignment switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
            VerticalContentAlignment = item is CalloutItem ? VerticalAlignment.Center : VerticalAlignment.Top,
        };
        if (item.Underline) box.TextDecorations = TextDecorations.Underline;
        box.LostKeyboardFocus += (_, _) => CommitTextEdit();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { CommitTextEdit(); Surface.Focus(); e.Handled = true; }
        };
        box.TextChanged += (_, _) => GrowTextEditor();
        EditorLayer.Children.Add(box);
        _textEditor = box;
        Surface.SetHidden(id);
        PositionTextEditor();
        box.Focus();
        box.SelectAll();
        Surface.InvalidateOverlay();
    }

    private void PositionTextEditor()
    {
        if (_textEditor == null || _editingText is not Guid id || Find(id) is not TextItem item) return;
        // The editor covers the item's text area plus its padding: the whole box for plain text, the inside of the curve for a round callout.
        var area = DocumentRenderer.TextArea(item);
        area.Inflate(item.Padding, item.Padding);
        var r = Surface.ToScreen(area);
        Canvas.SetLeft(_textEditor, r.X);
        Canvas.SetTop(_textEditor, r.Y);
        _textEditor.Width = Math.Max(10, r.Width);
        _textEditor.FontSize = Math.Max(1, item.FontSize * Surface.Zoom);
        _textEditor.Padding = new Thickness(item.Padding * Surface.Zoom);
        GrowTextEditor();
    }

    private void GrowTextEditor()
    {
        if (_textEditor == null || _editingText is not Guid id || Find(id) is not TextItem item) return;
        if (item is CalloutItem)
        {
            // A callout keeps its shape while typing; it grows, if it must, when the edit is committed.
            _textEditor.MinHeight = (DocumentRenderer.TextArea(item).Height + item.Padding * 2) * Surface.Zoom;
            return;
        }
        var measured = DocumentRenderer.MeasureText(item with { Text = _textEditor.Text });
        _textEditor.MinHeight = Math.Max(item.Bounds.Height, measured.Height) * Surface.Zoom;
    }

    private void CommitTextEdit()
    {
        if (_textEditor == null || _editingText is not Guid id) return;
        var box = _textEditor;
        var key = _textEditKey;
        _textEditor = null;
        _editingText = null;
        _textEditKey = null;
        EditorLayer.Children.Remove(box);
        Surface.SetHidden(null);
        if (Find(id) is not TextItem item) return;
        var text = box.Text;
        // An emptied text box is removed; a callout that already existed may stay as an empty bubble.
        if (string.IsNullOrWhiteSpace(text) && !(item is CalloutItem && key == null && item.Text.Length == 0))
        {
            if (key != null) _history.Rollback(key);
            else Push(DocumentOps.RemoveItems(Doc, [id]));
            _selection.Remove(id);
            if (_primary == id) _primary = _selection.FirstOrDefault();
            SelectionChanged();
            return;
        }
        if (text != item.Text)
        {
            var updated = item with { Text = text };
            var size = DocumentRenderer.MeasureText(updated);
            double height = item is CalloutItem ? Math.Max(item.Bounds.Height, size.Height) : size.Height;
            updated = updated with { Bounds = new Rect(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, height) };
            Push(DocumentOps.ReplaceItem(Doc, updated), key);
        }
        _history.BreakCoalescing();
        Surface.InvalidateOverlay();
    }

    // ----- Clipboard -----

    /// <summary>Marquee: turns the selected pixels into a floating image item that the Select tool can move.</summary>
    private void LiftSelection()
    {
        if (_pixelSelection is not Rect region) return;
        var result = PixelOps.Lift(Doc, _assets, region);
        if (result.Lifted is not ImageItem lifted)
        {
            Status("No picture pixels under the selection to move. Rotated images cannot be cut up.");
            return;
        }
        Push(result.Document);
        _pixelSelection = null;
        SetTool(Tool.Select);
        Select(lifted.Id);
        Status($"{region.Width:0} × {region.Height:0} px lifted into a floating image. Drag it, resize it, or press Delete to drop it.");
    }

    /// <summary>Marquee: clears the selected pixels from every image under them.</summary>
    private void ErasePixels(Rect region, string verb)
    {
        var erased = PixelOps.Erase(Doc, _assets, region);
        if (ReferenceEquals(erased, Doc))
        {
            Status("No picture pixels under the selection. Rotated images cannot be cut up.");
            return;
        }
        Push(erased);
        _pixelSelection = null;
        Surface.InvalidateOverlay();
        Status($"{verb} {region.Width:0} × {region.Height:0} px. Undo brings them back.");
    }

    private void Copy(bool cut)
    {
        CommitTextEdit();
        try
        {
            if (_pixelSelection is Rect pixels)
            {
                Clipboard.SetImage(DocumentRenderer.Flatten(Doc, _assets, pixels));
                if (cut) ErasePixels(pixels, "Cut");
                else Status($"Copied {pixels.Width:0} × {pixels.Height:0} px to the clipboard.");
                return;
            }
            var items = SelectedItems();
            if (items.Count == 0) return;
            Clipboard.SetDataObject(ItemClipboard.CreateDataObject(items, DocumentRenderer.FlattenItems(items, _assets)), true);
            if (cut)
            {
                Push(DocumentOps.RemoveItems(Doc, SelectedUnlocked().ToList()));
                ClearSelection();
            }
            Status($"{(cut ? "Cut" : "Copied")} {items.Count} item{(items.Count == 1 ? "" : "s")}.");
        }
        catch (Exception ex) { Status("Could not copy: " + ex.Message); }
    }

    private void Paste()
    {
        CommitTextEdit();
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            var items = ItemClipboard.TryRead(data);
            if (items != null)
            {
                var clones = DocumentOps.Clone(items.Where(i => i is not ImageItem image || _assets.Contains(image.AssetId)), new Vector(10, 10));
                if (clones.Count > 0)
                {
                    Push(AddToActiveLayer(Doc, clones));
                    _selection.Clear();
                    foreach (var clone in clones) _selection.Add(clone.Id);
                    _primary = clones[^1].Id;
                    if (_tool != Tool.Select) SetTool(Tool.Select);
                    SelectionChanged();
                    Status($"Pasted {clones.Count} item{(clones.Count == 1 ? "" : "s")}.");
                    return;
                }
            }
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                int placed = 0;
                foreach (var file in files)
                {
                    var bitmap = LoadImageFile(file);
                    if (bitmap != null) { PlaceImage(bitmap, Surface.ViewCenter + new Vector(20, 20) * placed, "paste-files"); placed++; }
                }
                _history.BreakCoalescing();
                if (placed > 0) return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image) { PlaceImage(image); return; }
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (text.Length == 0) return;
                _createId = Guid.NewGuid();
                var centre = Surface.ViewCenter;
                var item = NewText(new Rect(0, 0, 320, 10)) with { Text = text };
                var size = DocumentRenderer.MeasureText(item);
                item = item with { Bounds = new Rect(Math.Round(centre.X - size.Width / 2), Math.Round(centre.Y - size.Height / 2), Math.Min(320, size.Width), size.Height) };
                item = item with { Bounds = new Rect(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, DocumentRenderer.MeasureText(item).Height) };
                Push(AddToActiveLayer(Doc, [item]));
                if (_tool != Tool.Select) SetTool(Tool.Select);
                Select(item.Id);
                Status("Pasted text as a text box.");
                return;
            }
            Status("Nothing on the clipboard to paste.");
        }
        catch (Exception ex) { Status("Could not paste: " + ex.Message); }
    }

    private void Duplicate()
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        var clones = DocumentOps.Clone(items, new Vector(10, 10));
        Push(AddToActiveLayer(Doc, clones));
        _selection.Clear();
        foreach (var clone in clones) _selection.Add(clone.Id);
        _primary = clones[^1].Id;
        SelectionChanged();
    }

    // ----- Crop -----

    private void ApplyCrop()
    {
        if (_cropDraft is not Rect crop) return;
        Push(DocumentOps.SetCrop(Doc, crop));
        _cropDraft = null;
        SetTool(Tool.Select);
        Surface.Fit(Doc.ContentRect, maxZoom: Math.Max(1, Surface.Zoom));
        Status(Doc.CropRect is Rect applied ? $"Cropped to {applied.Width:0} × {applied.Height:0} px. Reset crop brings the rest back." : "Crop covers the whole picture; nothing changed.");
    }

    private void ApplyCrop_Click(object sender, RoutedEventArgs e) => ApplyCrop();

    private void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        if (Doc.CropRect == null) return;
        Push(DocumentOps.SetCrop(Doc, null));
        _cropDraft = null;
        if (_tool == Tool.Crop) SetTool(Tool.Select);
        Surface.Fit(Doc.ContentRect, maxZoom: Math.Max(1, Surface.Zoom));
    }

    // ----- Overlay -----

    private void PaintOverlay(DrawingContext dc)
    {
        var accent = (Brush)FindResource("Accent");
        var paper = (Brush)FindResource("Paper");
        var outline = new Pen(accent, 1);
        var dashed = new Pen((Brush)FindResource("Ink2"), 1) { DashStyle = DashStyles.Dash };
        var accentColor = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(0xBD, 0x93, 0xF9);
        var tint = new SolidColorBrush(Color.FromArgb(0x30, accentColor.R, accentColor.G, accentColor.B));

        if (_tool == Tool.Crop && _cropDraft is Rect crop)
        {
            var canvas = Surface.ToScreen(new Rect(Doc.Size));
            var screen = Surface.ToScreen(crop);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xB0, 0x21, 0x22, 0x2C)), null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(canvas), new RectangleGeometry(screen)));
            dc.DrawRectangle(null, new Pen(accent, 1.5), screen);
            foreach (var (_, point) in RectHandles(screen)) DrawHandle(dc, point, paper, accent);
            DrawLabel(dc, $"{crop.Width:0} × {crop.Height:0}", new Point(screen.Right, screen.Bottom + 8));
        }
        if (_pixelSelection is Rect pixels)
        {
            var screen = Surface.ToScreen(pixels);
            dc.DrawRectangle(tint, dashed, screen);
            DrawLabel(dc, $"{pixels.Width:0} × {pixels.Height:0}", new Point(screen.Right, screen.Bottom + 8));
        }
        if (_marquee is Rect marquee) dc.DrawRectangle(tint, new Pen(accent, 1) { DashStyle = DashStyles.Dash }, Surface.ToScreen(marquee));

        if (_tool == Tool.Blur)
        {
            var wash = new SolidColorBrush(Color.FromArgb(0x66, accentColor.R, accentColor.G, accentColor.B));
            if (_blurBox is Rect blurBox) dc.DrawRectangle(tint, new Pen(accent, 1) { DashStyle = DashStyles.Dash }, Surface.ToScreen(blurBox));
            if (_brushPoints.Count > 0)
            {
                double size = Math.Max(1, _brushSize * Surface.Zoom);
                if (_brushPoints.Count == 1) dc.DrawEllipse(wash, null, Surface.ToScreen(_brushPoints[0]), size / 2, size / 2);
                else
                {
                    var figure = new PathFigure { StartPoint = Surface.ToScreen(_brushPoints[0]), IsFilled = false };
                    figure.Segments.Add(new PolyLineSegment(_brushPoints.Skip(1).Select(Surface.ToScreen), true));
                    dc.DrawGeometry(null, new Pen(wash, size) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, new PathGeometry([figure]));
                }
            }
            if (_blurBrush && _pointer is Point pointer)
            {
                // The brush outline: dark under light so it reads on any picture.
                double radius = Math.Max(2, _brushSize * Surface.Zoom / 2);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)), 3), pointer, radius, radius);
                dc.DrawEllipse(null, new Pen(Brushes.White, 1), pointer, radius, radius);
            }
        }

        // Like Photoshop's Move tool: the selection persists across tool switches, but its outline and handles only show while Select is active.
        if (_tool != Tool.Select) return;

        var selected = SelectedItems();
        foreach (var item in selected)
        {
            if (item.Id == _editingText || item is LineItem) continue;
            var rotate = new RotateTransform(item.Rotation, item.Center.X, item.Center.Y);
            var b = item.Bounds;
            var corners = new[] { b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft }.Select(p => Surface.ToScreen(rotate.Transform(p))).ToArray();
            for (int i = 0; i < 4; i++) dc.DrawLine(outline, corners[i], corners[(i + 1) % 4]);
        }
        if (selected.Count == 1 && _primary is Guid id && Find(id) is Item primary && !Surface.IsLocked(id) && id != _editingText)
        {
            var handles = HandlePositions(primary).ToList();
            var top = handles.FirstOrDefault(h => h.Handle == Handle.Top);
            foreach (var (handle, point) in handles)
            {
                if (handle == Handle.Rotate)
                {
                    dc.DrawLine(outline, top.Screen, point);
                    dc.DrawEllipse(paper, new Pen(accent, 1.5), point, 5, 5);
                }
                else DrawHandle(dc, point, paper, accent);
            }
            // A callout's pointer tips: round accent handles that drag independently of the box.
            if (primary is CalloutItem pointing)
                foreach (var tip in pointing.Targets) dc.DrawEllipse(accent, new Pen(paper, 1.5), Surface.ToScreen(tip), 5.5, 5.5);
        }
        else if (selected.Count > 1)
        {
            var union = DocumentOps.UnionBounds(selected);
            if (!union.IsEmpty) dc.DrawRectangle(null, new Pen(accent, 1) { DashStyle = DashStyles.Dash }, Surface.ToScreen(union));
        }
    }

    private static IEnumerable<(Handle, Point)> RectHandles(Rect r) =>
    [
        (Handle.TopLeft, r.TopLeft), (Handle.Top, new Point(r.X + r.Width / 2, r.Top)), (Handle.TopRight, r.TopRight), (Handle.Right, new Point(r.Right, r.Y + r.Height / 2)),
        (Handle.BottomRight, r.BottomRight), (Handle.Bottom, new Point(r.X + r.Width / 2, r.Bottom)), (Handle.BottomLeft, r.BottomLeft), (Handle.Left, new Point(r.Left, r.Y + r.Height / 2)),
    ];

    private static void DrawHandle(DrawingContext dc, Point p, Brush fill, Brush stroke) =>
        dc.DrawRectangle(fill, new Pen(stroke, 1.5), new Rect(Math.Round(p.X) - 4, Math.Round(p.Y) - 4, 8, 8));

    private void DrawLabel(DrawingContext dc, string text, Point bottomRight)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("MonoFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, (Brush)FindResource("Ink2"), 1.0);
        var box = new Rect(bottomRight.X - formatted.Width - 12, bottomRight.Y, formatted.Width + 12, formatted.Height + 6);
        dc.DrawRoundedRectangle((Brush)FindResource("Paper"), new Pen((Brush)FindResource("PaperEdge"), 1), box, 5, 5);
        dc.DrawText(formatted, new Point(box.X + 6, box.Y + 3));
    }

    // ----- Properties panel -----

    private void ApplyToSelection(Func<Item, Item> update, string? key = null)
    {
        if (_syncingPanel) return;
        var ids = SelectedUnlocked().ToList();
        if (ids.Count == 0) return;
        Push(DocumentOps.UpdateItems(Doc, ids, update), key);
    }

    private void ApplyText(Func<TextItem, TextItem> update, string? key = null) =>
        ApplyToSelection(item => item is TextItem text ? Refit(update(text)) : item, key);

    /// <summary>Keeps a text box tall enough for its text after a font change.</summary>
    private static TextItem Refit(TextItem text)
    {
        var size = DocumentRenderer.MeasureText(text);
        return size.Height > text.Bounds.Height ? text with { Bounds = new Rect(text.Bounds.X, text.Bounds.Y, text.Bounds.Width, size.Height) } : text;
    }

    private void UpdatePanel()
    {
        _syncingPanel = true;
        try
        {
            var items = SelectedItems();
            var primary = (_primary is Guid id ? items.FirstOrDefault(i => i.Id == id) : null) ?? items.LastOrDefault();
            // With nothing selected the Select tool explains itself; the other tools' hints already say what to do.
            NoSelectionPanel.Visibility = primary == null && _tool == Tool.Select ? Visibility.Visible : Visibility.Collapsed;
            ItemPanel.Visibility = primary == null ? Visibility.Collapsed : Visibility.Visible;
            // The callout style row belongs to the tool, and to a selected callout it can restyle.
            var selectedCallout = primary as CalloutItem;
            CalloutKindRow.Visibility = _tool == Tool.Callout || (selectedCallout != null && _tool == Tool.Select) ? Visibility.Visible : Visibility.Collapsed;
            var shownKind = _tool == Tool.Callout ? _calloutKind : selectedCallout?.Kind ?? _calloutKind;
            foreach (var button in CalloutKinds.Children.OfType<ToggleButton>()) button.IsChecked = (string)button.Tag == shownKind.ToString();
            if (primary == null) return;
            ItemKindLabel.Text = (primary switch
            {
                ImageItem => "Image", RectItem => "Rectangle", EllipseItem => "Ellipse", ArrowItem => "Arrow", LineItem => "Line", CalloutItem => "Callout", TextItem => "Text",
                StampItem stamp => StampLibrary.Find(stamp.StampId)?.Name ?? "Stamp", _ => "Item",
            } + (items.Count > 1 ? $" + {items.Count - 1} more" : "")).ToUpperInvariant();
            StrokeStrip.Value = primary.Stroke;
            // Sliders show what fits their range; the boxes beside them always show the real value.
            StrokeWidthRow.Visibility = primary is StampItem ? Visibility.Collapsed : Visibility.Visible;
            StrokeWidthSlider.Value = Math.Clamp(primary.StrokeWidth, StrokeWidthSlider.Minimum, StrokeWidthSlider.Maximum);
            StrokeWidthBox.Show(primary.StrokeWidth);
            FillRow.Visibility = primary is RectItem or EllipseItem or StampItem ? Visibility.Visible : Visibility.Collapsed;
            FillStrip.Value = primary.Fill;
            double? corner = primary switch { RectItem r => r.CornerRadius, CalloutItem { Kind: CalloutKind.Label or CalloutKind.Speech } c => c.CornerRadius, _ => null };
            CornerRow.Visibility = corner != null ? Visibility.Visible : Visibility.Collapsed;
            if (corner is double radius) { CornerSlider.Value = Math.Clamp(radius, CornerSlider.Minimum, CornerSlider.Maximum); CornerBox.Show(radius); }
            (double Size, double Roundness)? head = primary switch { ArrowItem a => (a.HeadSize, a.HeadRoundness), CalloutItem { Kind: CalloutKind.Label } c => (c.HeadSize, c.HeadRoundness), _ => null };
            HeadRow.Visibility = head != null ? Visibility.Visible : Visibility.Collapsed;
            if (head is var (headSize, roundness))
            {
                HeadSlider.Value = Math.Clamp(headSize, HeadSlider.Minimum, HeadSlider.Maximum);
                HeadBox.Show(headSize);
                RoundSlider.Value = Math.Clamp(Math.Round(roundness * 100), 0, 100);
                RoundBox.Show(Math.Round(roundness * 100));
            }
            CalloutArrowRow.Visibility = selectedCallout != null ? Visibility.Visible : Visibility.Collapsed;
            if (selectedCallout != null)
            {
                string noun = selectedCallout.Kind == CalloutKind.Label ? "arrow" : "tail";
                AddPointerButton.Content = "Add " + noun;
                RemovePointerButton.Content = "Remove " + noun;
                RemovePointerButton.IsEnabled = selectedCallout.Targets.Count > 0;
            }
            OpacitySlider.Value = Math.Clamp(Math.Round(primary.Opacity * 100), 5, 100);
            OpacityValue.Text = $"{primary.Opacity * 100:0}%";
            ShadowCheck.IsChecked = primary.Shadow;
            TextSection.Visibility = primary is TextItem ? Visibility.Visible : Visibility.Collapsed;
            if (primary is TextItem text)
            {
                FontCombo.SelectedItem = text.FontFamily;
                FontSizeBox.Text = $"{text.FontSize:0.#}";
                BoldToggle.IsChecked = text.Bold;
                ItalicToggle.IsChecked = text.Italic;
                UnderlineToggle.IsChecked = text.Underline;
                AlignLeftToggle.IsChecked = text.Alignment == TextAlign.Left;
                AlignCenterToggle.IsChecked = text.Alignment == TextAlign.Center;
                AlignRightToggle.IsChecked = text.Alignment == TextAlign.Right;
                TextColorStrip.Value = text.Color;
                TextBackgroundStrip.Value = text.Background;
                PaddingSlider.Value = Math.Clamp(text.Padding, PaddingSlider.Minimum, PaddingSlider.Maximum);
                PaddingBox.Show(text.Padding);
            }
        }
        finally { _syncingPanel = false; }
    }

    private void StrokeWidth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_syncingPanel) SetStrokeWidth(Math.Round(e.NewValue)); }
    private void Corner_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_syncingPanel) SetCorner(Math.Round(e.NewValue)); }
    private void Head_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_syncingPanel) SetHead(Math.Round(e.NewValue)); }
    private void Round_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_syncingPanel) SetRoundness(Math.Round(e.NewValue) / 100); }
    private void Padding_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_syncingPanel) SetPadding(Math.Round(e.NewValue)); }

    // Each setter serves both its slider and its number box, so a typed 120 px behaves like a dragged 12 px.

    private void SetStrokeWidth(double width)
    {
        StrokeWidthBox.Show(width);
        _strokeWidth = width;
        ApplyToSelection(i => i with { StrokeWidth = width, Stroke = i.Stroke ?? (width > 0 ? _stroke : null) }, "panel:width");
    }

    private void SetCorner(double radius)
    {
        CornerBox.Show(radius);
        ApplyToSelection(i => i switch { RectItem rect => rect with { CornerRadius = radius }, CalloutItem callout => callout with { CornerRadius = radius }, _ => i }, "panel:corner");
    }

    private void SetHead(double size)
    {
        HeadBox.Show(size);
        ApplyToSelection(i => i switch { ArrowItem arrow => arrow with { HeadSize = size }, CalloutItem callout => callout with { HeadSize = size }, _ => i }, "panel:head");
    }

    private void SetRoundness(double fraction)
    {
        RoundBox.Show(Math.Round(fraction * 100));
        ApplyToSelection(i => i switch { ArrowItem arrow => arrow with { HeadRoundness = fraction }, CalloutItem callout => callout with { HeadRoundness = fraction }, _ => i }, "panel:round");
    }

    private void SetPadding(double padding)
    {
        PaddingBox.Show(padding);
        ApplyText(t => t with { Padding = padding }, "panel:padding");
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPanel) return;
        double opacity = Math.Round(e.NewValue) / 100;
        OpacityValue.Text = $"{opacity * 100:0}%";
        ApplyToSelection(i => i with { Opacity = opacity }, "panel:opacity");
    }

    private void Shadow_Click(object sender, RoutedEventArgs e) => ApplyToSelection(i => i with { Shadow = ShadowCheck.IsChecked == true });

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPanel || FontCombo.SelectedItem is not string family) return;
        ApplyText(t => t with { FontFamily = family });
    }

    private void FontSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitFontSize();
        Surface.Focus();
        e.Handled = true;
    }

    private void FontSize_LostFocus(object sender, RoutedEventArgs e) => CommitFontSize();

    private void CommitFontSize()
    {
        if (_syncingPanel) return;
        if (!double.TryParse(FontSizeBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var size)) { UpdatePanel(); return; }
        size = Math.Clamp(size, 6, 2000);
        ApplyText(t => t.FontSize == size ? t : t with { FontSize = size });
    }

    private void TextStyle_Click(object sender, RoutedEventArgs e) =>
        ApplyText(t => t with { Bold = BoldToggle.IsChecked == true, Italic = ItalicToggle.IsChecked == true, Underline = UnderlineToggle.IsChecked == true });

    private void Align_Click(object sender, RoutedEventArgs e)
    {
        var alignment = Enum.Parse<TextAlign>((string)((ToggleButton)sender).Tag);
        ApplyText(t => t with { Alignment = alignment });
        UpdatePanel();
    }

    // ----- Top bar, context menu and files -----

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Surface.ZoomAt(new Point(Surface.ActualWidth / 2, Surface.ActualHeight / 2), Surface.Zoom * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Surface.ZoomAt(new Point(Surface.ActualWidth / 2, Surface.ActualHeight / 2), Surface.Zoom / 1.25);
    private void Fit_Click(object sender, RoutedEventArgs e) => Surface.Fit(Doc.ContentRect);
    private void ActualSize_Click(object sender, RoutedEventArgs e) => Surface.Center(Doc.ContentRect, PixelZoom);
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Cut_Click(object sender, RoutedEventArgs e) => Copy(cut: true);
    private void Copy_Click(object sender, RoutedEventArgs e) => Copy(cut: false);
    private void Paste_Click(object sender, RoutedEventArgs e) => Paste();
    private void Duplicate_Click(object sender, RoutedEventArgs e) => Duplicate();
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();
    private void BringToFront_Click(object sender, RoutedEventArgs e) => Reorder(ZOrderMove.BringToFront);
    private void BringForward_Click(object sender, RoutedEventArgs e) => Reorder(ZOrderMove.BringForward);
    private void SendBackward_Click(object sender, RoutedEventArgs e) => Reorder(ZOrderMove.SendBackward);
    private void SendToBack_Click(object sender, RoutedEventArgs e) => Reorder(ZOrderMove.SendToBack);

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool any = _selection.Count > 0;
        bool pixels = _pixelSelection != null;
        foreach (var item in new[] { DuplicateMenu, FrontMenu, ForwardMenu, BackwardMenu, BackMenu }) item.IsEnabled = any;
        CutMenu.IsEnabled = CopyMenu.IsEnabled = DeleteMenu.IsEnabled = any || pixels;
    }

    private void EditMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool any = _selection.Count > 0;
        bool pixels = _pixelSelection != null;
        DuplicateMenu2.IsEnabled = any;
        CutMenu2.IsEnabled = CopyMenu2.IsEnabled = DeleteMenu2.IsEnabled = any || pixels;
    }

    private void ArrangeMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool any = _selection.Count > 0;
        foreach (var item in new[] { FrontMenu2, ForwardMenu2, BackwardMenu2, BackMenu2 }) item.IsEnabled = any;
    }

    private void ImageMenu_Opened(object sender, RoutedEventArgs e) => UpdateChrome();

    private void LayerMenu_Opened(object sender, RoutedEventArgs e)
    {
        int index = ActiveLayer is Layer active ? DocumentOps.LayerIndexOf(Doc, active) : -1;
        DeleteLayerMenu.IsEnabled = index >= 0 && Doc.Layers.Count > 1;
        LayerUpMenu.IsEnabled = index >= 0 && index < Doc.Layers.Count - 1;
        LayerDownMenu.IsEnabled = MergeDownMenu.IsEnabled = index > 0;
    }

    private void MergeDown_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveLayer is not Layer active) return;
        int index = DocumentOps.LayerIndexOf(Doc, active);
        if (index <= 0) return;
        _activeLayer = Doc.Layers[index - 1].Id;
        Push(DocumentOps.MergeLayerDown(Doc, index));
        Status($"Merged \"{active.Name}\" into \"{Doc.Layers[index - 1].Name}\".");
    }

    private void NewFromClipboard_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.OpenEditorFromClipboard();
    private void OpenGallery_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowGallery();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        string actual = Gesture(_config.Editor.ActualSizeShortcut), fit = Gesture(_config.Editor.FitShortcut);
        ShortcutsDialog.Show(this, $"{(actual.Length > 0 ? actual : "not set")} / {(fit.Length > 0 ? fit : "not set")}");
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/CommonDocsOrg/vibershot") { UseShellExecute = true }); }
        catch (Exception ex) { Status("Could not open the browser: " + ex.Message); }
    }

    // ----- Canvas: new, canvas size, image size -----

    private void NewCanvas_Click(object sender, RoutedEventArgs e) => NewCanvas(this, _config);

    /// <summary>Asks for a size and background, then opens a blank document in a new editor window.</summary>
    public static void NewCanvas(Window? owner, AppConfig config)
    {
        if (CanvasSetupWindow.AskNew(owner, new Size(1920, 1080), CanvasColor(config)) is not { } choice) return;
        new EditorWindow(config, DocumentOps.Blank(choice.Size, choice.Background), null).Show();
    }

    private void CanvasSize_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        if (CanvasSetupWindow.AskCanvasSize(this, Doc) is not { } add) return;
        var before = Doc.Size;
        _cropDraft = null;
        _pixelSelection = null;
        Push(DocumentOps.ResizeCanvas(Doc, add.Left, add.Top, add.Right, add.Bottom));
        Surface.Fit(Doc.ContentRect, maxZoom: Math.Max(1, Surface.Zoom));
        Status($"Canvas resized from {before.Width:0} × {before.Height:0} to {Doc.Size.Width:0} × {Doc.Size.Height:0} px. Nothing was deleted; undo brings the old canvas back.");
    }

    private void ImageSize_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEdit();
        if (CanvasSetupWindow.AskImageSize(this, Doc) is not { } size) return;
        var before = Doc.Size;
        _cropDraft = null;
        _pixelSelection = null;
        Push(DocumentOps.Scale(Doc, size));
        Surface.Fit(Doc.ContentRect, maxZoom: Math.Max(1, Surface.Zoom));
        Status($"Picture scaled from {before.Width:0} × {before.Height:0} to {Doc.Size.Width:0} × {Doc.Size.Height:0} px.");
    }

    // ----- Callouts -----

    private void CalloutKind_Click(object sender, RoutedEventArgs e)
    {
        var kind = Enum.Parse<CalloutKind>((string)((ToggleButton)sender).Tag);
        _calloutKind = kind;
        if (_tool == Tool.Callout) SetTool(Tool.Callout);
        else ApplyToSelection(i => i is CalloutItem callout ? Refit(callout with { Kind = kind, CornerRadius = kind == CalloutKind.Speech && callout.Kind != CalloutKind.Speech ? Math.Max(callout.CornerRadius, 18) : callout.CornerRadius }) : i);
        UpdatePanel();
        Surface.Focus();
    }

    /// <summary>Adds a pointer to the selected callout, fanned out around the box so each new tip is easy to grab.</summary>
    private void AddPointer_Click(object sender, RoutedEventArgs e)
    {
        if (_primary is not Guid id || Find(id) is not CalloutItem callout) return;
        double[] angles = [135, 45, 225, 315, 90, 270, 180, 0];
        double angle = angles[callout.Targets.Count % angles.Length] * Math.PI / 180;
        double reach = Math.Max(callout.Bounds.Width, callout.Bounds.Height) / 2 + 80;
        var tip = callout.Center + new Vector(Math.Cos(angle), Math.Sin(angle)) * reach;
        Push(DocumentOps.ReplaceItem(Doc, callout with { Targets = callout.Targets.Add(new Point(Math.Round(tip.X), Math.Round(tip.Y))) }));
        if (_tool != Tool.Select) SetTool(Tool.Select);
        Status("Pointer added. Drag its round tip onto what it should point at.");
        Surface.Focus();
    }

    private void RemovePointer_Click(object sender, RoutedEventArgs e)
    {
        if (_primary is not Guid id || Find(id) is not CalloutItem { Targets.Count: > 0 } callout) return;
        Push(DocumentOps.ReplaceItem(Doc, callout with { Targets = callout.Targets.RemoveAt(callout.Targets.Count - 1) }));
        Surface.Focus();
    }

    // ----- Stamps -----

    private void StampSearch_Changed(object sender, TextChangedEventArgs e) => BuildStampGrid();

    /// <summary>Fills the stamp picker with the stamps matching the search box; the chosen stamp carries the accent edge.</summary>
    private void BuildStampGrid()
    {
        if (StampGrid == null) return;
        StampSearchHint.Visibility = StampSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StampGrid.Children.Clear();
        foreach (var stamp in StampLibrary.Search(StampSearch.Text))
        {
            bool chosen = stamp.Id == _stampId;
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, StampLibrary.Grid, StampLibrary.Grid));
                StampLibrary.Draw(dc, stamp.Id, new Rect(0, 0, StampLibrary.Grid, StampLibrary.Grid));
            }
            drawing.Freeze();
            var tile = new Button
            {
                Width = 48, Height = 48, Padding = new Thickness(9), Margin = new Thickness(0, 0, 4, 4), ToolTip = stamp.Name,
                Background = (Brush)FindResource(chosen ? "AccentSoft" : "Paper"), BorderBrush = (Brush)FindResource(chosen ? "Accent" : "AccentEdge"), BorderThickness = new Thickness(chosen ? 2 : 1),
                Content = new Image { Source = new DrawingImage(drawing), Stretch = Stretch.Uniform },
            };
            AutomationProperties.SetName(tile, stamp.Name + " stamp");
            string id = stamp.Id;
            tile.Click += (_, _) =>
            {
                _stampId = id;
                BuildStampGrid();
                Status($"{StampLibrary.Find(id)?.Name}: click the picture to place it, or drag to size it.");
                Surface.Focus();
            };
            StampGrid.Children.Add(tile);
        }
        StampEmpty.Visibility = StampGrid.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Blur and pixelate -----

    private void ObscureMode_Click(object sender, RoutedEventArgs e)
    {
        _obscureMode = Enum.Parse<ObscureMode>((string)((ToggleButton)sender).Tag);
        SyncObscurePanel();
        Surface.Focus();
    }

    private void BlurShape_Click(object sender, RoutedEventArgs e)
    {
        _blurBrush = (string)((ToggleButton)sender).Tag == "Brush";
        SyncObscurePanel();
        Surface.InvalidateOverlay();
        Surface.Focus();
    }

    private void BlurStrength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // A slider coerces its value to its minimum while the XAML loads; that is not the user choosing a value.
        if (_syncingPanel || !IsInitialized) return;
        _blurStrength = Math.Round(e.NewValue);
        BlurStrengthBox.Show(_blurStrength);
    }

    private void BrushSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPanel || !IsInitialized) return;
        _brushSize = Math.Round(e.NewValue);
        BrushSizeBox.Show(_brushSize);
    }

    /// <summary>Shows the blur options as they are: mode, brush or box, strength and brush size.</summary>
    private void SyncObscurePanel()
    {
        bool was = _syncingPanel;
        _syncingPanel = true;
        try
        {
            foreach (var button in ObscureModes.Children.OfType<ToggleButton>()) button.IsChecked = (string)button.Tag == _obscureMode.ToString();
            foreach (var button in BlurShapes.Children.OfType<ToggleButton>()) button.IsChecked = (string)button.Tag == (_blurBrush ? "Brush" : "Box");
            BlurStrengthSlider.Value = Math.Clamp(_blurStrength, BlurStrengthSlider.Minimum, BlurStrengthSlider.Maximum);
            BlurStrengthBox.Show(_blurStrength);
            BrushSizeSlider.Value = Math.Clamp(_brushSize, BrushSizeSlider.Minimum, BrushSizeSlider.Maximum);
            BrushSizeBox.Show(_brushSize);
            BrushSizeRow.Visibility = _tool == Tool.Blur && _blurBrush ? Visibility.Visible : Visibility.Collapsed;
            ObscureSelectionButton.Content = _obscureMode == ObscureMode.Blur ? "Blur selection" : "Pixelate selection";
        }
        finally { _syncingPanel = was; }
    }

    private void ObscureSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_pixelSelection is not Rect region) return;
        if (ApplyObscure(region)) { _pixelSelection = null; UpdateCommandState(); Surface.InvalidateOverlay(); }
        Surface.Focus();
    }

    /// <summary>Turns the stroke just painted into a blurred patch shaped like the stroke.</summary>
    private void ApplyBrushStroke()
    {
        var points = _brushPoints.ToList();
        _brushPoints.Clear();
        Surface.InvalidateOverlay();
        if (points.Count == 0) return;
        double size = Math.Max(2, _brushSize);
        // A soft edge suits a blur; pixel blocks keep a hard one.
        double feather = _obscureMode == ObscureMode.Blur ? Math.Min(8, Math.Round(size * 0.12)) : 0;
        var bounds = new Rect(points[0], new Size(0, 0));
        foreach (var point in points) bounds.Union(point);
        bounds.Inflate(size / 2 + feather + 1, size / 2 + feather + 1);
        if (points.Count == 1) { ApplyObscure(bounds, new EllipseGeometry(points[0], size / 2, size / 2), null, feather); return; }
        var figure = new PathFigure { StartPoint = points[0], IsFilled = false };
        figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
        var pen = new Pen(Brushes.White, size) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        ApplyObscure(bounds, new PathGeometry([figure]), pen, feather);
    }

    /// <summary>
    /// Covers a region with a blurred or pixelated copy of what is under it. The patch is a new image
    /// item on the active layer, made from that layer and everything below, so it can be moved,
    /// deleted or undone, and the hidden pixels are really gone from an export.
    /// </summary>
    private bool ApplyObscure(Rect region, Geometry? mask = null, Pen? maskPen = null, double feather = 0)
    {
        CommitTextEdit();
        int layer = ActiveLayerIndex();
        try
        {
            var patch = PixelOps.ObscurePatch(Doc, _assets, region, _obscureMode, _blurStrength, layer, mask, maskPen, feather);
            if (patch == null) { Status("Nothing to hide there: that area is outside the picture."); return false; }
            Push(DocumentOps.AddItem(Doc, patch, layer));
            _history.BreakCoalescing();
            Status($"{(_obscureMode == ObscureMode.Blur ? "Blurred" : "Pixelated")} {patch.Bounds.Width:0} × {patch.Bounds.Height:0} px on \"{Doc.Layers[Math.Clamp(layer, 0, Doc.Layers.Count - 1)].Name}\". The patch is its own image: select it to move or delete it.");
            return true;
        }
        catch (Exception ex)
        {
            Status("Could not hide that area: " + ex.Message);
            return false;
        }
    }

    // ----- Layers panel -----

    private void MoveSelectionToLayer(int index)
    {
        var ids = SelectedUnlocked().ToList();
        if (ids.Count == 0 || index < 0 || index >= Doc.Layers.Count) return;
        _activeLayer = Doc.Layers[index].Id;
        Push(DocumentOps.MoveItemsToLayer(Doc, ids, index));
        Status($"Moved {ids.Count} item{(ids.Count == 1 ? "" : "s")} to \"{Doc.Layers[index].Name}\".");
    }

    private void UpdateLayersPanel()
    {
        // Drags push a snapshot per mouse move; only rebuild the rows when something they show has changed.
        var signature = string.Join("|", Doc.Layers.Select(l => $"{l.Id:N}:{l.Name}:{l.Visible}:{l.Locked}:{l.Items.Count}")) + $"#{_activeLayer:N}#{_selection.Count > 0}";
        if (signature != _layersSignature)
        {
            _layersSignature = signature;
            LayersList.Children.Clear();
            for (int i = Doc.Layers.Count - 1; i >= 0; i--) LayersList.Children.Add(BuildLayerRow(Doc.Layers[i], i));
        }
        var active = ActiveLayer;
        int index = active != null ? DocumentOps.LayerIndexOf(Doc, active) : -1;
        DeleteLayerButton.IsEnabled = index >= 0 && Doc.Layers.Count > 1;
        LayerUpButton.IsEnabled = index >= 0 && index < Doc.Layers.Count - 1;
        LayerDownButton.IsEnabled = index > 0;
        _syncingPanel = true;
        try
        {
            LayerOpacitySlider.IsEnabled = active != null;
            LayerOpacitySlider.Value = active != null ? Math.Round(Math.Clamp(active.Opacity, 0, 1) * 100) : 100;
            LayerOpacityValue.Text = $"{LayerOpacitySlider.Value:0}%";
        }
        finally { _syncingPanel = false; }
    }

    private UIElement BuildLayerRow(Layer layer, int index)
    {
        bool active = layer.Id == _activeLayer;
        var row = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(4, 2, 8, 2), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand,
            Background = active ? (Brush)FindResource("AccentSoft") : Brushes.Transparent,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var eye = LayerGlyphButton(layer.Visible ? "" : "", layer.Visible ? "Hide layer" : "Show layer", layer.Visible ? 1 : 0.45);
        eye.Click += (_, e) => { e.Handled = true; Push(DocumentOps.SetLayer(Doc, index, layer with { Visible = !layer.Visible })); };
        var padlock = LayerGlyphButton(layer.Locked ? "" : "", layer.Locked ? "Unlock layer" : "Lock layer", layer.Locked ? 1 : 0.3);
        padlock.Click += (_, e) => { e.Handled = true; Push(DocumentOps.SetLayer(Doc, index, layer with { Locked = !layer.Locked })); };
        Grid.SetColumn(padlock, 1);

        var name = new TextBlock
        {
            Text = layer.Name, FontSize = 12.5, FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource(layer.Visible ? "Ink" : "Ink3"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 6, 0),
        };
        Grid.SetColumn(name, 2);
        var count = new TextBlock
        {
            Text = layer.Items.Count.ToString(), FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 10.5, Foreground = (Brush)FindResource("Ink3"), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{layer.Items.Count} item{(layer.Items.Count == 1 ? "" : "s")}",
        };
        Grid.SetColumn(count, 3);
        grid.Children.Add(eye);
        grid.Children.Add(padlock);
        grid.Children.Add(name);
        grid.Children.Add(count);
        row.Child = grid;

        row.MouseEnter += (_, _) => { if (layer.Id != _activeLayer) row.Background = (Brush)FindResource("Hover"); };
        row.MouseLeave += (_, _) => { if (layer.Id != _activeLayer) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonDown += (_, e) =>
        {
            CommitTextEdit();
            if (e.ClickCount == 2) { BeginLayerRename(layer, index, grid, name); e.Handled = true; return; }
            SetActiveLayer(layer);
        };
        row.MouseRightButtonDown += (_, _) => SetActiveLayer(layer);

        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename", InputGestureText = "Double-click" };
        rename.Click += (_, _) => BeginLayerRename(layer, index, grid, name);
        var moveHere = new MenuItem { Header = "Move selection here", IsEnabled = _selection.Count > 0 && PixelOps.Editable(layer) };
        moveHere.Click += (_, _) => MoveSelectionToLayer(index);
        var merge = new MenuItem { Header = "Merge down", IsEnabled = index > 0 };
        merge.Click += (_, _) => { _activeLayer = Doc.Layers[index - 1].Id; Push(DocumentOps.MergeLayerDown(Doc, index)); };
        var delete = new MenuItem { Header = "Delete layer", IsEnabled = Doc.Layers.Count > 1 };
        delete.Click += (_, _) => DeleteLayer(index);
        menu.Items.Add(rename);
        menu.Items.Add(moveHere);
        menu.Items.Add(merge);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        row.ContextMenu = menu;
        return row;
    }

    private Button LayerGlyphButton(string glyph, string tip, double opacity)
    {
        var button = new Button { Style = (Style)FindResource("IconButton"), Width = 24, Height = 24, FontSize = 12, Content = glyph, ToolTip = tip, Opacity = opacity };
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private void SetActiveLayer(Layer layer)
    {
        if (_activeLayer == layer.Id) return;
        _activeLayer = layer.Id;
        UpdateLayersPanel();
        Surface.Focus();
    }

    private void BeginLayerRename(Layer layer, int index, Grid grid, TextBlock name)
    {
        if (grid.Children.OfType<TextBox>().Any()) return;
        var box = new TextBox { Text = layer.Name, FontSize = 12.5, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(box, 2);
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            var text = box.Text.Trim();
            if (grid.Children.Contains(box)) { grid.Children.Remove(box); name.Visibility = Visibility.Visible; }
            if (commit && text.Length > 0 && text != layer.Name && index < Doc.Layers.Count && Doc.Layers[index].Id == layer.Id)
                Push(DocumentOps.SetLayer(Doc, index, Doc.Layers[index] with { Name = text }));
            else UpdateLayersPanel();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); Surface.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); Surface.Focus(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        name.Visibility = Visibility.Collapsed;
        grid.Children.Add(box);
        box.Focus();
        box.SelectAll();
    }

    private void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        var active = ActiveLayer;
        int above = (active != null ? DocumentOps.LayerIndexOf(Doc, active) : Doc.Layers.Count - 1) + 1;
        var names = Doc.Layers.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = Doc.Layers.Count + 1;
        while (names.Contains($"Layer {n}")) n++;
        var layer = new Layer { Name = $"Layer {n}" };
        _activeLayer = layer.Id;
        Push(DocumentOps.InsertLayer(Doc, above, layer));
        Status($"Added \"{layer.Name}\". New items now go there.");
        Surface.Focus();
    }

    private void DeleteLayer_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveLayer is Layer active) DeleteLayer(DocumentOps.LayerIndexOf(Doc, active));
    }

    private void DeleteLayer(int index)
    {
        if (Doc.Layers.Count <= 1 || index < 0 || index >= Doc.Layers.Count) return;
        var removed = Doc.Layers[index];
        _activeLayer = Doc.Layers[index > 0 ? index - 1 : 1].Id;
        Push(DocumentOps.RemoveLayer(Doc, index));
        Status(removed.Items.Count > 0 ? $"Deleted \"{removed.Name}\" and its {removed.Items.Count} item{(removed.Items.Count == 1 ? "" : "s")}. Undo brings it back." : $"Deleted \"{removed.Name}\".");
        Surface.Focus();
    }

    private void LayerUp_Click(object sender, RoutedEventArgs e) => ShiftActiveLayer(1);
    private void LayerDown_Click(object sender, RoutedEventArgs e) => ShiftActiveLayer(-1);

    private void ShiftActiveLayer(int delta)
    {
        if (ActiveLayer is not Layer active) return;
        Push(DocumentOps.MoveLayer(Doc, DocumentOps.LayerIndexOf(Doc, active), delta));
        Surface.Focus();
    }

    private void LayerOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPanel || ActiveLayer is not Layer active) return;
        double opacity = Math.Round(e.NewValue) / 100;
        LayerOpacityValue.Text = $"{opacity * 100:0}%";
        if (Math.Abs(active.Opacity - opacity) < 0.001) return;
        Push(DocumentOps.SetLayer(Doc, DocumentOps.LayerIndexOf(Doc, active), active with { Opacity = opacity }), "panel:layer-opacity");
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e) => CopyImage();

    /// <summary>Flattens the document onto the clipboard.</summary>
    private void CopyImage()
    {
        CommitTextEdit();
        try
        {
            var flat = DocumentRenderer.Flatten(Doc, _assets);
            ClipboardService.SetImage(flat);
            Status($"Copied {flat.PixelWidth} × {flat.PixelHeight} px to the clipboard. Ready to paste.");
        }
        catch (Exception ex) { Status("Could not copy: " + ex.Message); }
    }

    private void Export_Click(object sender, RoutedEventArgs e) => Export();

    /// <summary>Flattens the document and runs the export dialog; a flat file is not editable, so the notice bar follows when no saved project holds the edits.</summary>
    private void Export()
    {
        CommitTextEdit();
        BitmapSource flat;
        try { flat = DocumentRenderer.Flatten(Doc, _assets); }
        catch (Exception ex) { Status("Export failed: " + ex.Message); return; }
        var folder = _sourceImagePath != null ? Path.GetDirectoryName(_sourceImagePath) : null;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Directory.Exists(_config.OutputFolder) ? _config.OutputFolder : null;
        var initial = ImageEncoding.IsFlat(_config.Format) ? _config.Format : ImageFormat.Png;
        var dialog = new ExportImageWindow(flat, initial, _config.WebpQuality, ExportStem, folder) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ExportedPath == null) return;
        _exportedThisSession = true;
        Status("Exported: " + dialog.ExportedPath);
        if (_projectPath == null || _history.IsDirty) ShowFlattenNotice(ExportImageWindow.ShortName(dialog.Format));
        else FlattenNotice.Visibility = Visibility.Collapsed;
    }

    private void ShowFlattenNotice(string formatName)
    {
        FlattenNoticeText.Text = $"Exported as {formatName}. The export is flattened: text, shapes and layers in it can no longer be edited. Save a Vibershot project to keep them editable.";
        FlattenNotice.Visibility = Visibility.Visible;
    }

    private void DismissNotice_Click(object sender, RoutedEventArgs e) { FlattenNotice.Visibility = Visibility.Collapsed; Surface.Focus(); }

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);
    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: true);

    private bool SaveProject(bool saveAs)
    {
        CommitTextEdit();
        var path = _projectPath;
        if (saveAs || path == null)
        {
            var projects = _config.Gallery.ProjectsFolder;
            try { Directory.CreateDirectory(projects); } catch (Exception) { }
            var dialog = new SaveFileDialog
            {
                Filter = ProjectFilter, DefaultExt = ProjectFile.Extension, AddExtension = true,
                FileName = _projectPath != null || _sourceImagePath != null ? ExportStem : $"capture_{DateTime.Now:yyyyMMdd_HHmmss}",
                InitialDirectory = _projectPath != null ? Path.GetDirectoryName(_projectPath) : projects,
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            ProjectFile.Save(path, Doc, _assets);
            _projectPath = path;
            _history.MarkSaved();
            FlattenNotice.Visibility = Visibility.Collapsed;
            UpdateChrome();
            Status("Saved: " + path);
            return true;
        }
        catch (Exception ex)
        {
            Status("Save failed: " + ex.Message);
            return false;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenDialog();

    /// <summary>Open…: a project opens as a project window; any other image becomes a new document.</summary>
    private void OpenDialog()
    {
        var projects = _config.Gallery.ProjectsFolder;
        var dialog = new OpenFileDialog { Filter = OpenFilter, InitialDirectory = Directory.Exists(projects) ? projects : "" };
        if (dialog.ShowDialog(this) != true) return;
        OpenFileWindow(dialog.FileName);
    }

    private void OpenFileWindow(string path)
    {
        try { (IsProjectFile(path) ? OpenProject(_config, path) : OpenImage(_config, path)).Show(); }
        catch (Exception ex) { Status($"Could not open {Path.GetFileName(path)}: {ex.Message}"); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitTextEdit();
        // A flat export cannot be reopened for editing, so an exported document without a project is worth a prompt even when it has no edits.
        bool flattenedOnly = _exportedThisSession && (_projectPath == null || _history.IsDirty);
        if (_history.IsDirty || flattenedOnly)
        {
            var message = flattenedOnly ? $"Save changes to {DocumentName} as a Vibershot project? Your export is flattened; without a project the text, shapes and layers cannot be edited again." : null;
            switch (SaveChangesDialog.Show(this, DocumentName, message))
            {
                case SaveChangesDialog.Choice.Save:
                    if (!SaveProject(saveAs: false)) e.Cancel = true;
                    break;
                case SaveChangesDialog.Choice.Cancel:
                    e.Cancel = true;
                    break;
            }
        }
        base.OnClosing(e);
    }
}
