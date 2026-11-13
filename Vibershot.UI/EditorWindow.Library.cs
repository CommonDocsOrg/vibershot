using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Vibershot.Editing;

namespace Vibershot.UI;

/// <summary>
/// The editor's right sidebar and what it drives: the style theme, the library of themed presets
/// (clicked or dragged onto the picture), the style each drawing tool gives its next item, and the
/// sidebar folding down to a strip.
/// </summary>
public partial class EditorWindow
{
    private const string PresetDragFormat = "Vibershot.StylePreset";
    private const double SidebarWidth = 292, SidebarStripWidth = 42;

    private StyleTheme _theme = null!;
    private PresetGroup _libraryTab = PresetGroup.Arrows;
    private bool _libraryBuilt;

    // The style each drawing tool gives its next item: set by the theme, by a library preset, and by the tool panel.
    private ArrowItem _arrowStyle = null!;
    private LineItem _lineStyle = null!;
    private RectItem _rectStyle = null!;
    private EllipseItem _ellipseStyle = null!;
    private TextItem _textStyle = null!;
    private CalloutItem _calloutStyle = null!;

    // A press on a library tile that may turn into a drag.
    private Point? _tilePress;
    private int _insertStep;

    private IReadOnlyList<StyleTheme> Themes() =>
        StyleLibrary.Themes(TryParseColor(_config.Editor.DefaultStrokeColor) ?? Color.FromRgb(0xF0, 0x61, 0x2A), _config.Editor.DefaultStrokeWidth);

    /// <summary>Called once from the constructor, before the first tool is set.</summary>
    private void InitLibrary()
    {
        var themes = Themes();
        AdoptTheme(themes.FirstOrDefault(t => string.Equals(t.Id, _config.Editor.Theme, StringComparison.OrdinalIgnoreCase)) ?? themes[0]);
        SetSidebar(!_config.Editor.SidebarCollapsed, remember: false);
    }

    // ----- Tool styles -----

    /// <summary>The style the active tool gives its next item; null for tools that do not draw styled items.</summary>
    private Item? ToolStyle => _tool switch
    {
        Tool.Arrow => _arrowStyle,
        Tool.Shape => _shape switch { ShapeKind.Rect => _rectStyle, ShapeKind.Ellipse => _ellipseStyle, _ => _lineStyle },
        Tool.Text => _textStyle,
        Tool.Callout => _calloutStyle.Kind == _calloutKind ? _calloutStyle : _calloutStyle with { Kind = _calloutKind },
        _ => null,
    };

    /// <summary>The tool style an item of this kind was, or would be, drawn with.</summary>
    private Item? StyleLike(Item item) => item switch
    {
        ArrowItem => _arrowStyle, LineItem => _lineStyle, RectItem => _rectStyle, EllipseItem => _ellipseStyle,
        CalloutItem => _calloutStyle, TextItem => _textStyle, _ => null,
    };

    private void SetToolStyle(Item style)
    {
        switch (style)
        {
            case ArrowItem arrow: _arrowStyle = arrow; break;
            case LineItem line: _lineStyle = line; break;
            case RectItem rect: _rectStyle = rect; break;
            case EllipseItem ellipse: _ellipseStyle = ellipse; break;
            case CalloutItem callout: _calloutStyle = callout; break;
            case TextItem text: _textStyle = text; break;
        }
    }

    /// <summary>Text styles take the font from Settings → Editor; a theme only sets colours and lines.</summary>
    private Item WithConfiguredFont(Item style) => style is TextItem text
        ? text with
        {
            FontFamily = string.IsNullOrWhiteSpace(_config.Editor.DefaultFontFamily) ? "Segoe UI" : _config.Editor.DefaultFontFamily,
            FontSize = Math.Clamp(_config.Editor.DefaultFontSize, 6, 2000),
        }
        : style;

    // ----- Themes -----

    private void AdoptTheme(StyleTheme theme)
    {
        _theme = theme;
        var styles = StyleLibrary.Defaults(theme);
        _arrowStyle = styles.Arrow;
        _lineStyle = styles.Line;
        _rectStyle = styles.Rect;
        _ellipseStyle = styles.Ellipse;
        _textStyle = (TextItem)WithConfiguredFont(styles.Text);
        _calloutStyle = (CalloutItem)WithConfiguredFont(styles.Callout);
        ThemeButton.Content = ThemeChip(theme, caret: true);
        BuildLibrary();
    }

    /// <summary>Three colour dots and the theme's name, as shown on the theme button and in its menu.</summary>
    private UIElement ThemeChip(StyleTheme theme, bool caret)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var color in new[] { theme.Primary, theme.Tint, theme.Highlight })
            row.Children.Add(new Ellipse { Width = 11, Height = 11, Margin = new Thickness(0, 0, 3, 0), Fill = new SolidColorBrush(color), Stroke = (Brush)FindResource("PaperEdge"), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = theme.Name, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (caret) row.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 9, Margin = new Thickness(7, 1, 0, 0), Foreground = (Brush)FindResource("Ink2"), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = PlacementMode.Bottom };
        foreach (var theme in Themes())
        {
            var item = new MenuItem { Header = ThemeChip(theme, caret: false), InputGestureText = theme.Id == _theme.Id ? "current" : $"{theme.StrokeWidth:0.#} px" };
            AutomationProperties.SetName(item, theme.Name + " theme");
            item.Click += (_, _) => ChooseTheme(theme);
            menu.Items.Add(item);
        }
        ThemeButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ChooseTheme(StyleTheme theme)
    {
        AdoptTheme(theme);
        _config.Editor.Theme = theme.Id;
        SaveConfig();
        UpdatePanel();
        Status($"{theme.Name} theme: new arrows, shapes and callouts use its colours and {theme.StrokeWidth:0.#} px lines. Items already on the picture keep their look.");
        Surface.Focus();
    }

    private void SaveConfig()
    {
        try { _config.Save(); }
        catch (Exception) { /* Remembering the theme and the sidebar is a convenience. */ }
    }

    // ----- Library -----

    private void LibraryTab_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryTab(Enum.Parse<PresetGroup>((string)((ToggleButton)sender).Tag), rebuild: true);
        Surface.Focus();
    }

    private void ShowLibraryTab(PresetGroup tab, bool rebuild = false)
    {
        if (tab == _libraryTab && _libraryBuilt && !rebuild) return;
        _libraryTab = tab;
        BuildLibrary();
    }

    /// <summary>Fills the library with the current tab's presets, each drawn in the current theme.</summary>
    private void BuildLibrary()
    {
        _libraryBuilt = true;
        foreach (var button in LibraryTabs.Children.OfType<ToggleButton>()) button.IsChecked = (string)button.Tag == _libraryTab.ToString();
        LibraryGrid.Children.Clear();
        foreach (var preset in StyleLibrary.Presets(_theme, _libraryTab)) LibraryGrid.Children.Add(BuildPresetTile(preset));
    }

    private Button BuildPresetTile(StylePreset preset)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var box = new Rect(StyleLibrary.SampleSize);
            dc.DrawRectangle(Brushes.Transparent, null, box);
            dc.PushClip(new RectangleGeometry(box));
            DocumentRenderer.RenderItem(dc, preset.Sample, _assets);
            dc.Pop();
        }
        drawing.Freeze();
        var content = new StackPanel();
        content.Children.Add(new Border
        {
            // A light tile, like a screenshot, so dark strokes and white fills both read.
            Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF2, 0xF6)), BorderBrush = (Brush)FindResource("PaperEdge"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(3, 2, 3, 2),
            Child = new Image { Source = new DrawingImage(drawing), Stretch = Stretch.Uniform },
        });
        content.Children.Add(new TextBlock
        {
            Text = preset.Name, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Ink2"),
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        });
        var tile = new Button
        {
            Content = content, Padding = new Thickness(0), Margin = new Thickness(3, 0, 3, 8), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, ToolTip = $"{preset.Name}: drag onto the picture, or click to insert", HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(tile, preset.Name + " preset");
        tile.Click += (_, _) => InsertPreset(preset, null);
        tile.PreviewMouseLeftButtonDown += (_, e) => _tilePress = e.GetPosition(this);
        tile.PreviewMouseLeftButtonUp += (_, _) => _tilePress = null;
        tile.PreviewMouseMove += (_, e) =>
        {
            if (_tilePress is not Point press || e.LeftButton != MouseButtonState.Pressed) return;
            var moved = e.GetPosition(this) - press;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _tilePress = null;
            tile.ReleaseMouseCapture();
            DragDrop.DoDragDrop(tile, new DataObject(PresetDragFormat, $"{preset.Group}/{preset.Id}"), DragDropEffects.Copy);
        };
        return tile;
    }

    private bool IsOverSurface(DragEventArgs e)
    {
        var point = e.GetPosition(Surface);
        return point.X >= 0 && point.Y >= 0 && point.X <= Surface.ActualWidth && point.Y <= Surface.ActualHeight;
    }

    private void DropPreset(DragEventArgs e)
    {
        e.Handled = true;
        if (!IsOverSurface(e) || e.Data.GetData(PresetDragFormat) is not string key) return;
        var parts = key.Split('/', 2);
        if (parts.Length != 2 || !Enum.TryParse<PresetGroup>(parts[0], out var group) || StyleLibrary.Find(_theme, group, parts[1]) is not { } preset) return;
        var at = Surface.ToDoc(e.GetPosition(Surface));
        // The drop arrives inside the drag loop; place the item once it has unwound so focus can move to the picture.
        Dispatcher.BeginInvoke(() => InsertPreset(preset, at));
    }

    /// <summary>
    /// Puts a library preset on the picture, at a point or in the middle of the view, selects it, and
    /// makes its style the one the matching tool draws next.
    /// </summary>
    private void InsertPreset(StylePreset preset, Point? at)
    {
        CommitTextEdit();
        var content = Doc.ContentRect;
        // Sized for the picture: about a fifth of a 720p frame, larger on a 4K capture.
        double fit = Math.Clamp(Math.Min(content.Width, content.Height) / 720, 0.5, 3);
        var style = WithConfiguredFont(preset.Style);
        // Clicked presets fan out from the middle of the view, so one does not land exactly on the last.
        var centre = at ?? Surface.ViewCenter + new Vector(24, 24) * fit * (_insertStep++ % 6);
        var item = StyleLibrary.Place(style, preset.Sample, centre, (preset.Group == PresetGroup.Stamps ? 1.7 : 2.5) * fit);
        Push(AddToActiveLayer(Doc, [item]));
        _history.BreakCoalescing();
        SetToolStyle(style);
        if (style is CalloutItem callout) _calloutKind = callout.Kind;
        if (style is StampItem stamp) { _stampId = stamp.StampId; BuildStampGrid(); }
        if (_tool != Tool.Select) SetTool(Tool.Select);
        Select(item.Id);
        Status($"Inserted \"{preset.Name}\"{(preset.Group == PresetGroup.Stamps ? "" : $" in the {_theme.Name} theme")}. Drag it into place{(item is CalloutItem ? ", and type its text" : "")}.");
        Activate();
        if (item is CalloutItem) BeginTextEdit(item.Id);
        else Surface.Focus();
    }

    // ----- Sidebar -----

    private void CollapseSidebar_Click(object sender, RoutedEventArgs e) => SetSidebar(false);
    private void ExpandSidebar_Click(object sender, RoutedEventArgs e) => SetSidebar(true);
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => SetSidebar(SidebarContent.Visibility != Visibility.Visible);

    private void SetSidebar(bool open, bool remember = true)
    {
        SidebarColumn.Width = new GridLength(open ? SidebarWidth : SidebarStripWidth);
        SidebarContent.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarStrip.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        SidebarMenu.Header = open ? "Collapse sidebar" : "Expand sidebar";
        if (!remember) return;
        _config.Editor.SidebarCollapsed = !open;
        SaveConfig();
        Surface.Focus();
    }
}
