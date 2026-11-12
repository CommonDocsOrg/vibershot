using System.Collections.Generic;
using System.Linq;
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
    private enum Tool { Select, Marquee, Crop, Arrow, Shape, Text, Image }
    private enum ShapeKind { Rect, Ellipse, Line }
    private enum Handle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate, LineTail, LineHead }
    private enum DragKind { None, Pan, Move, Resize, Rotate, LineEnd, Marquee, PixelSelect, CropDraw, CropResize, CropMove, Create }

    private const string ProjectFilter = "Vibershot project|*.vibeshot";
    private const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";
    private const double HandleRadius = 8, RotateHandleDistance = 26;

    /// <summary>One store for every editor window so items copied between windows keep their pixels.</summary>
    private static readonly AssetStore SharedAssets = new();
    private static readonly string ProjectsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibershot", "Projects");

    private readonly AppConfig _config;
    private readonly AssetStore _assets = SharedAssets;
    private readonly History<ImageDocument> _history;
    private readonly HashSet<Guid> _selection = new();
    private Guid? _primary;
    private string? _projectPath;
    private Tool _tool = Tool.Select;
    private ShapeKind _shape = ShapeKind.Rect;
    private Rect? _pixelSelection, _cropDraft, _marquee;
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
    private string DocumentName => _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : "Untitled";

    public EditorWindow(AppConfig config, BitmapSource? image) : this(config, image != null ? DocumentOps.FromImage(image, SharedAssets) : DocumentOps.Blank(new Size(1280, 720), Colors.White), null) { }

    private EditorWindow(AppConfig config, ImageDocument document, string? projectPath)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _config = config;
        _projectPath = projectPath;
        _stroke = TryParseColor(config.Editor.DefaultStrokeColor) ?? Color.FromRgb(0xF0, 0x61, 0x2A);
        _strokeWidth = Math.Clamp(config.Editor.DefaultStrokeWidth, 0, 24);
        _history = new History<ImageDocument>(document);
        _history.Changed += OnHistoryChanged;
        _dragBase = document;

        Surface.OverlayPainter = PaintOverlay;
        Surface.ViewChanged += () => { ZoomLabel.Text = $"{Surface.Zoom * 100:0}%"; PositionTextEditor(); };
        FontCombo.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
        StrokeStrip.ValueChanged += c => { _stroke = c; ApplyToSelection(i => i with { Stroke = c, StrokeWidth = c != null && i.StrokeWidth <= 0 ? Math.Max(1, _strokeWidth) : i.StrokeWidth }); };
        FillStrip.ValueChanged += c => { _fill = c; ApplyToSelection(i => i is RectItem or EllipseItem ? i with { Fill = c } : i); };
        TextColorStrip.ValueChanged += c => { if (c is { } color) ApplyText(t => t with { Color = color }); };
        TextBackgroundStrip.ValueChanged += c => ApplyText(t => t with { Background = c });
        foreach (var slider in new[] { StrokeWidthSlider, CornerSlider, HeadSlider, OpacitySlider, PaddingSlider })
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => _history.BreakCoalescing()));

        PreviewKeyDown += Window_PreviewKeyDown;
        PreviewKeyUp += Window_PreviewKeyUp;
        DragOver += Window_DragOver;
        Drop += Window_Drop;

        Surface.SetDocument(document, _assets);
        SetTool(Tool.Select);
        UpdateChrome();
        UpdatePanel();
        Loaded += (_, _) => { Surface.Fit(Doc.ContentRect, maxZoom: 1); Surface.Focus(); };
        Status($"{Doc.Size.Width:0} × {Doc.Size.Height:0} px • Pick a tool on the left, or paste an image.");
    }

    /// <summary>Opens a saved <c>.vibeshot</c> project in a new editor window.</summary>
    public static EditorWindow OpenProject(AppConfig config, string path)
    {
        var (document, assets) = ProjectFile.Load(path);
        foreach (var id in assets.Ids) SharedAssets.Add(id, assets.Get(id)!);
        return new EditorWindow(config, document, path);
    }

    private static Color? TryParseColor(string text)
    {
        try { return DocumentJson.ParseColor(text); }
        catch (Exception) { return null; }
    }

    // ----- Document state -----

    private void OnHistoryChanged()
    {
        Surface.SetDocument(Doc, _assets);
        var existing = Doc.AllItems.Select(i => i.Id).ToHashSet();
        _selection.RemoveWhere(id => !existing.Contains(id));
        if (_primary is Guid p && !existing.Contains(p)) _primary = _selection.FirstOrDefault();
        UpdateChrome();
        UpdatePanel();
        Surface.InvalidateOverlay();
    }

    private void Push(ImageDocument doc, string? key = null) => _history.Push(doc, key);
    private Item? Find(Guid id) => DocumentOps.FindItem(Doc, id);
    private List<Item> SelectedItems() => Doc.AllItems.Where(i => _selection.Contains(i.Id)).ToList();
    private IEnumerable<Guid> SelectedUnlocked() => _selection.Where(id => !Surface.IsLocked(id));

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
        UpdatePanel();
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
            : $"{Doc.Size.Width:0} × {Doc.Size.Height:0} px · {Doc.AllItems.Count()} items";
        DocSizeLabel.Text = Doc.CropRect != null ? $"{content.Width:0} × {content.Height:0} of {Doc.Size.Width:0} × {Doc.Size.Height:0}" : $"{Doc.Size.Width:0} × {Doc.Size.Height:0}";
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        ResetCropButton.IsEnabled = Doc.CropRect != null;
        ApplyCropButton.Visibility = _tool == Tool.Crop && _cropDraft != null ? Visibility.Visible : Visibility.Collapsed;
        CropHint.Visibility = _tool == Tool.Crop ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Status(string message)
    {
        StatusLabel.Text = message;
        string key = message.StartsWith("Saved", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Copied", StringComparison.OrdinalIgnoreCase) ? "Ok"
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
        _tool = tool;
        foreach (var button in ToolRail.Children.OfType<ToggleButton>()) button.IsChecked = (string)button.Tag == tool.ToString();
        Surface.Cursor = tool switch
        {
            Tool.Text => Cursors.IBeam,
            Tool.Select or Tool.Image => Cursors.Arrow,
            _ => Cursors.Cross,
        };
        UpdateChrome();
        Surface.InvalidateOverlay();
        Status(tool switch
        {
            Tool.Select => "Click to select, drag to move. Shift-click adds. Double-click text to edit.",
            Tool.Marquee => "Drag over the picture, then Ctrl+C copies those pixels.",
            Tool.Crop => "Drag the area to keep. Enter crops, Esc cancels; Reset crop restores everything.",
            Tool.Arrow => "Drag from the tail to the head. Shift snaps to 45°.",
            Tool.Shape => $"Drag to draw a {_shape.ToString().ToLowerInvariant()}. Press S again to switch shape.",
            Tool.Text => "Click or drag to place a text box, then type.",
            _ => "Click where the image should go.",
        });
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tool = Enum.Parse<Tool>((string)((ToggleButton)sender).Tag);
        if (tool == Tool.Shape && _tool == Tool.Shape) ShapeFlyout.IsOpen = true;
        SetTool(tool);
        Surface.Focus();
    }

    private void ShapeTool_RightClick(object sender, MouseButtonEventArgs e) { ShapeFlyout.IsOpen = true; e.Handled = true; }

    private void ShapeKind_Click(object sender, RoutedEventArgs e)
    {
        SetShape(Enum.Parse<ShapeKind>((string)((ToggleButton)sender).Tag));
        ShapeFlyout.IsOpen = false;
        Surface.Focus();
    }

    private void SetShape(ShapeKind kind)
    {
        _shape = kind;
        ShapeToolIcon.Data = Geometry.Parse(kind switch
        {
            ShapeKind.Ellipse => "M10,3 A7,7 0 1 1 9.99,3 Z",
            ShapeKind.Line => "M3,17 L17,3",
            _ => "M2,4 H18 V16 H2 Z",
        });
        foreach (var button in ((StackPanel)((Border)ShapeFlyout.Child).Child).Children.OfType<ToggleButton>()) button.IsChecked = (string)button.Tag == kind.ToString();
        SetTool(Tool.Shape);
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
                case Key.C: Copy(cut: false); break;
                case Key.X: Copy(cut: true); break;
                case Key.V: Paste(); break;
                case Key.D: Duplicate(); break;
                case Key.S: SaveProject(saveAs: shift); break;
                case Key.O: OpenProjectDialog(); break;
                case Key.E: Export(); break;
                case Key.D0 or Key.NumPad0: Surface.Fit(Doc.ContentRect); break;
                case Key.D1 or Key.NumPad1: Surface.Center(Doc.ContentRect, 1); break;
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
            case Key.V: SetTool(Tool.Select); break;
            case Key.M: SetTool(Tool.Marquee); break;
            case Key.C: SetTool(Tool.Crop); break;
            case Key.A: SetTool(Tool.Arrow); break;
            case Key.S: if (_tool == Tool.Shape) SetShape((ShapeKind)(((int)_shape + 1) % 3)); else SetTool(Tool.Shape); break;
            case Key.T: SetTool(Tool.Text); break;
            case Key.I: SetTool(Tool.Image); break;
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
            case Tool.Arrow or Tool.Shape or Tool.Text:
                StartDrag(DragKind.Create, screen, doc);
                _createId = Guid.NewGuid();
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
                Push(DocumentOps.MoveItems(_dragBase, SelectedUnlocked(), delta), _dragKey);
                break;
            }
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
                Push(DocumentOps.AddItem(_dragBase, BuildItem(_dragStartDoc, doc, shift)), _dragKey);
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
            case DragKind.Move or DragKind.Resize or DragKind.Rotate or DragKind.LineEnd:
                _history.BreakCoalescing();
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
                if (_pixelSelection is Rect px) Status($"{px.Width:0} × {px.Height:0} px selected • Ctrl+C copies them as an image.");
                Surface.InvalidateOverlay();
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
            if (_tool != Tool.Text) return;
            var text = NewText(new Rect(Math.Round(doc.X), Math.Round(doc.Y), 240, 10));
            text = text with { Bounds = new Rect(text.Bounds.X, text.Bounds.Y, 240, DocumentRenderer.MeasureText(text).Height) };
            Push(DocumentOps.AddItem(_dragBase, text), _dragKey);
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
        FontSize = Math.Clamp(_config.Editor.DefaultFontSize, 6, 400),
        Color = _stroke ?? Color.FromRgb(0x15, 0x18, 0x1C),
    };

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
        Push(DocumentOps.AddItem(Doc, item), key);
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

    private static BitmapSource? LoadImageFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception) { return null; }
    }

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
            if (string.Equals(Path.GetExtension(file), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) { OpenProjectWindow(file); continue; }
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
            Background = Brushes.Transparent, Foreground = new SolidColorBrush(item.Color), CaretBrush = (Brush)FindResource("Tangerine"),
            SelectionBrush = (Brush)FindResource("TangerineSoft"), SelectionOpacity = 0.6,
            FontFamily = new FontFamily(item.FontFamily), FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextAlignment = item.Alignment switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
            VerticalContentAlignment = VerticalAlignment.Top,
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
        var r = Surface.ToScreen(item.Bounds);
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
        if (string.IsNullOrWhiteSpace(text))
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
            updated = updated with { Bounds = new Rect(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, size.Height) };
            Push(DocumentOps.ReplaceItem(Doc, updated), key);
        }
        _history.BreakCoalescing();
        Surface.InvalidateOverlay();
    }

    // ----- Clipboard -----

    private void Copy(bool cut)
    {
        CommitTextEdit();
        try
        {
            if (_tool == Tool.Marquee && _pixelSelection is Rect pixels)
            {
                Clipboard.SetImage(DocumentRenderer.Flatten(Doc, _assets, pixels));
                Status($"Copied {pixels.Width:0} × {pixels.Height:0} px to the clipboard.");
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
                    Push(DocumentOps.AddItems(Doc, clones));
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
                Push(DocumentOps.AddItem(Doc, item));
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
        Push(DocumentOps.AddItems(Doc, clones));
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
        var tangerine = (Brush)FindResource("Tangerine");
        var paper = (Brush)FindResource("Paper");
        var outline = new Pen(tangerine, 1);
        var dashed = new Pen((Brush)FindResource("Ink2"), 1) { DashStyle = DashStyles.Dash };
        var tint = new SolidColorBrush(Color.FromArgb(0x30, 0xF0, 0x61, 0x2A));

        if (_tool == Tool.Crop && _cropDraft is Rect crop)
        {
            var canvas = Surface.ToScreen(new Rect(Doc.Size));
            var screen = Surface.ToScreen(crop);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xB0, 0xFB, 0xFB, 0xFC)), null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(canvas), new RectangleGeometry(screen)));
            dc.DrawRectangle(null, new Pen(tangerine, 1.5), screen);
            foreach (var (_, point) in RectHandles(screen)) DrawHandle(dc, point, paper, tangerine);
            DrawLabel(dc, $"{crop.Width:0} × {crop.Height:0}", new Point(screen.Right, screen.Bottom + 8));
        }
        if (_pixelSelection is Rect pixels)
        {
            var screen = Surface.ToScreen(pixels);
            dc.DrawRectangle(tint, dashed, screen);
            DrawLabel(dc, $"{pixels.Width:0} × {pixels.Height:0}", new Point(screen.Right, screen.Bottom + 8));
        }
        if (_marquee is Rect marquee) dc.DrawRectangle(tint, new Pen(tangerine, 1) { DashStyle = DashStyles.Dash }, Surface.ToScreen(marquee));

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
                    dc.DrawEllipse(paper, new Pen(tangerine, 1.5), point, 5, 5);
                }
                else DrawHandle(dc, point, paper, tangerine);
            }
        }
        else if (selected.Count > 1)
        {
            var union = DocumentOps.UnionBounds(selected);
            if (!union.IsEmpty) dc.DrawRectangle(null, new Pen(tangerine, 1) { DashStyle = DashStyles.Dash }, Surface.ToScreen(union));
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
            NoSelectionPanel.Visibility = primary == null ? Visibility.Visible : Visibility.Collapsed;
            ItemPanel.Visibility = primary == null ? Visibility.Collapsed : Visibility.Visible;
            if (primary == null) return;
            ItemKindLabel.Text = (primary switch
            {
                ImageItem => "Image", RectItem => "Rectangle", EllipseItem => "Ellipse", ArrowItem => "Arrow", LineItem => "Line", TextItem => "Text", _ => "Item",
            } + (items.Count > 1 ? $" + {items.Count - 1} more" : "")).ToUpperInvariant();
            StrokeStrip.Value = primary.Stroke;
            StrokeWidthSlider.Value = Math.Clamp(primary.StrokeWidth, 0, 24);
            StrokeWidthValue.Text = $"{primary.StrokeWidth:0} px";
            FillRow.Visibility = primary is RectItem or EllipseItem ? Visibility.Visible : Visibility.Collapsed;
            FillStrip.Value = primary.Fill;
            CornerRow.Visibility = primary is RectItem ? Visibility.Visible : Visibility.Collapsed;
            if (primary is RectItem rect) { CornerSlider.Value = Math.Clamp(rect.CornerRadius, 0, 80); CornerValue.Text = $"{rect.CornerRadius:0} px"; }
            HeadRow.Visibility = primary is ArrowItem ? Visibility.Visible : Visibility.Collapsed;
            if (primary is ArrowItem arrow) { HeadSlider.Value = Math.Clamp(arrow.HeadSize, 4, 60); HeadValue.Text = $"{arrow.HeadSize:0} px"; }
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
                PaddingSlider.Value = Math.Clamp(text.Padding, 0, 40);
                PaddingValue.Text = $"{text.Padding:0} px";
            }
        }
        finally { _syncingPanel = false; }
    }

    private void StrokeWidth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPanel) return;
        double width = Math.Round(e.NewValue);
        StrokeWidthValue.Text = $"{width:0} px";
        _strokeWidth = width;
        ApplyToSelection(i => i with { StrokeWidth = width, Stroke = i.Stroke ?? (width > 0 ? _stroke : null) }, "panel:width");
    }

    private void Corner_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPanel) return;
        double radius = Math.Round(e.NewValue);
        CornerValue.Text = $"{radius:0} px";
        ApplyToSelection(i => i is RectItem rect ? rect with { CornerRadius = radius } : i, "panel:corner");
    }

    private void Head_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPanel) return;
        double size = Math.Round(e.NewValue);
        HeadValue.Text = $"{size:0} px";
        ApplyToSelection(i => i is ArrowItem arrow ? arrow with { HeadSize = size } : i, "panel:head");
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPanel) return;
        double opacity = Math.Round(e.NewValue) / 100;
        OpacityValue.Text = $"{opacity * 100:0}%";
        ApplyToSelection(i => i with { Opacity = opacity }, "panel:opacity");
    }

    private void Padding_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPanel) return;
        double padding = Math.Round(e.NewValue);
        PaddingValue.Text = $"{padding:0} px";
        ApplyText(t => t with { Padding = padding }, "panel:padding");
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
        size = Math.Clamp(size, 6, 400);
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
    private void ActualSize_Click(object sender, RoutedEventArgs e) => Surface.Center(Doc.ContentRect, 1);
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
        foreach (var item in new[] { CutMenu, CopyMenu, DuplicateMenu, DeleteMenu, FrontMenu, ForwardMenu, BackwardMenu, BackMenu }) item.IsEnabled = any;
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
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

    private void Export()
    {
        CommitTextEdit();
        var dialog = new SaveFileDialog
        {
            Filter = ScreenshotSaveDialog.Filter, FilterIndex = ScreenshotSaveDialog.FilterIndexFor(_config.Format),
            InitialDirectory = Directory.Exists(_config.OutputFolder) ? _config.OutputFolder : "",
            FileName = _projectPath != null ? DocumentName : $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}", AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var flat = DocumentRenderer.Flatten(Doc, _assets);
            using var stream = File.Create(dialog.FileName);
            ImageEncoding.Encode(flat, ScreenshotSaveDialog.FormatFor(dialog.FilterIndex), _config.WebpQuality, stream);
            Status("Saved: " + dialog.FileName);
        }
        catch (Exception ex) { Status("Export failed: " + ex.Message); }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);

    private bool SaveProject(bool saveAs)
    {
        CommitTextEdit();
        var path = _projectPath;
        if (saveAs || path == null)
        {
            try { Directory.CreateDirectory(ProjectsFolder); } catch (Exception) { }
            var dialog = new SaveFileDialog
            {
                Filter = ProjectFilter, DefaultExt = ProjectFile.Extension, AddExtension = true,
                FileName = _projectPath != null ? DocumentName : $"capture_{DateTime.Now:yyyyMMdd_HHmmss}",
                InitialDirectory = _projectPath != null ? Path.GetDirectoryName(_projectPath) : ProjectsFolder,
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            ProjectFile.Save(path, Doc, _assets);
            _projectPath = path;
            _history.MarkSaved();
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

    private void Open_Click(object sender, RoutedEventArgs e) => OpenProjectDialog();

    private void OpenProjectDialog()
    {
        var dialog = new OpenFileDialog { Filter = ProjectFilter, InitialDirectory = Directory.Exists(ProjectsFolder) ? ProjectsFolder : "" };
        if (dialog.ShowDialog(this) != true) return;
        OpenProjectWindow(dialog.FileName);
    }

    private void OpenProjectWindow(string path)
    {
        try { OpenProject(_config, path).Show(); }
        catch (Exception ex) { Status("Could not open project: " + ex.Message); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitTextEdit();
        if (_history.IsDirty)
        {
            switch (SaveChangesDialog.Show(this, DocumentName))
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
