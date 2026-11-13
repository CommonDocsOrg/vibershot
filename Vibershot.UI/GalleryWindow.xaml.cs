using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Core;
using Vibershot.Gallery;

namespace Vibershot.UI;

/// <summary>
/// The library window: every capture lands here and stays until deleted. Reads from a
/// <see cref="GalleryIndex"/>, shows tiles from a <see cref="ThumbnailCache"/>, and hands
/// "open" to the editors through <see cref="OpenInEditor"/> / <see cref="OpenInVideoEditor"/>.
/// </summary>
public partial class GalleryWindow : Window, INotifyPropertyChanged
{
    /// <summary>Count shown at the right of a rail row (attached so the template can bind it).</summary>
    public static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached("Count", typeof(object), typeof(GalleryWindow));
    public static void SetCount(DependencyObject element, object? value) => element.SetValue(CountProperty, value);
    public static object? GetCount(DependencyObject element) => element.GetValue(CountProperty);

    public sealed class TagCount : INotifyPropertyChanged
    {
        private bool _isChecked;
        public TagCount(string tag, int count) { Tag = tag; Count = count; }
        public string Tag { get; }
        public int Count { get; }
        public bool IsChecked { get => _isChecked; set { if (_isChecked != value) { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class TagToggle
    {
        public string Tag { get; init; } = "";
        public bool IsOn { get; set; }
    }

    private const int MinTile = 120, MaxTile = 360;

    private readonly AppConfig _config;
    private readonly GalleryIndex _index;
    private readonly ThumbnailCache _thumbs;
    private GalleryFilter _filter = GalleryFilter.All;
    private string? _tag;
    private GallerySort _sort = GallerySort.Date;
    private bool _descending = true;
    private double _tileSize = 200;
    private List<GalleryItem> _view = new();
    private string? _pendingSelect;
    private DateTime _lastActivationScan = DateTime.MinValue;
    private bool _refreshing, _buildingToggles, _detailVisible = true;
    private int _suspendRefresh, _detailVersion;
    private GalleryItem? _detailItem;

    // Mouse state for marquee selection and dragging files out.
    private Point _pressPoint;
    private bool _marquee, _marqueeAdditive, _pressOnItem, _deferredSelect;
    private GalleryItem? _pressedItem;
    private HashSet<GalleryItem> _marqueeBase = new();

    /// <summary>Seam for the image editor: receives images and .vibeshot projects. Null opens the file with the shell.</summary>
    public Action<GalleryItem>? OpenInEditor { get; set; }

    /// <summary>Seam for the video editor: receives videos and .vibevideo projects. Null opens the file with the shell.</summary>
    public Action<GalleryItem>? OpenInVideoEditor { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double TileSize
    {
        get => _tileSize;
        set
        {
            value = Math.Clamp(Math.Round(value), MinTile, MaxTile);
            if (_tileSize == value) return;
            _tileSize = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileImageHeight)));
        }
    }

    public double TileImageHeight => Math.Round(TileSize * 0.68);

    public GalleryWindow(AppConfig config)
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
        _config = config;
        var settings = config.Gallery;
        RestorePlacement(settings);
        _tileSize = Math.Clamp(settings.ThumbnailSize, MinTile, MaxTile);
        SizeSlider.Value = _tileSize;
        SizeLabel.Text = $"{(int)_tileSize} px";
        SetDetailVisible(settings.ShowDetailPane);

        Action<Action> post = action => Dispatcher.BeginInvoke(action);
        _index = new GalleryIndex(GalleryIndex.DefaultFolders(config), post);
        _thumbs = new ThumbnailCache(ThumbnailCache.DefaultFolder, post, new ShellVideoThumbnailer());
        _index.Changed += RefreshView;
        _index.Watch();

        Loaded += (_, _) => { Tiles.Focus(); _ = RescanAsync(); };
        Activated += (_, _) =>
        {
            // The watcher is an optimization; activation always brings the index up to date.
            if (DateTime.Now - _lastActivationScan > TimeSpan.FromSeconds(2)) _ = RescanAsync();
        };
        Closing += (_, _) => SaveWindowSettings();
        Closed += (_, _) => { _index.Dispose(); _thumbs.Dispose(); };
        PreviewKeyDown += Window_PreviewKeyDown;
        UpdateDetail(null);
        ShowStatus("Scanning the library…");
    }

    /// <summary>Selects a file once the index knows it (used right after a capture is saved).</summary>
    public void SelectFile(string path)
    {
        _pendingSelect = path;
        if (_filter != GalleryFilter.All || SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            FilterAll.IsChecked = true;
        }
        if (_index.Refresh(path) is null) _ = RescanAsync();
        else RefreshView();
    }

    private async Task RescanAsync()
    {
        _lastActivationScan = DateTime.Now;
        try { await _index.RescanAsync(); }
        catch (Exception ex) { ShowStatus("Scan failed: " + ex.Message); return; }
        if (_index.Items.Count == 0) ShowStatus("No captures yet. Capture region to add the first one.");
        else
        {
            int folders = _index.Folders.Count(Directory.Exists);
            ShowStatus($"{_index.Items.Count} item{(_index.Items.Count == 1 ? "" : "s")} in {folders} folder{(folders == 1 ? "" : "s")} · {_config.OutputFolder}");
            UpdateCountLabel();
        }
    }

    // ----- View -----

    private void RefreshView()
    {
        if (_suspendRefresh > 0 || _refreshing) return;
        _refreshing = true;
        try
        {
            var selected = Tiles.SelectedItems.Cast<GalleryItem>().ToHashSet();
            _view = GalleryQuery.Apply(_index.Items, _filter, _tag, SearchBox.Text, _sort, _descending);
            var today = DateTime.Now;
            foreach (var item in _view) item.Group = GalleryQuery.GroupLabel(item, _sort, today);
            var view = new ListCollectionView(_view);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GalleryItem.Group)));
            Tiles.ItemsSource = view;
            foreach (var item in _view) if (selected.Contains(item)) Tiles.SelectedItems.Add(item);
            if (_pendingSelect is not null)
            {
                var hit = _view.FirstOrDefault(i => string.Equals(i.Path, _pendingSelect, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    _pendingSelect = null;
                    Tiles.SelectedItems.Clear();
                    Tiles.SelectedItem = hit;
                    Tiles.ScrollIntoView(hit);
                }
            }
            UpdateCounts();
            UpdateTags();
            UpdateCountLabel();
            if (_detailItem is not null && Tiles.SelectedItems.Contains(_detailItem)) UpdateDetail(_detailItem);
            bool empty = _view.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty)
            {
                bool filtered = _index.Items.Count > 0;
                EmptyTitle.Text = filtered ? "No matches" : "Nothing here yet";
                EmptyHint.Text = filtered ? "Try another filter or search." : "Captures land here automatically. Drop files in to import them.";
            }
        }
        finally { _refreshing = false; }
    }

    private void UpdateCounts()
    {
        var items = _index.Items;
        SetCount(FilterAll, items.Count);
        SetCount(FilterImages, items.Count(i => i.Kind == GalleryItemKind.Image));
        SetCount(FilterVideos, items.Count(i => i.Kind == GalleryItemKind.Video));
        SetCount(FilterProjects, items.Count(i => i.Kind == GalleryItemKind.Project));
        SetCount(FilterFavourites, items.Count(i => i.Favourite));
    }

    private void UpdateTags()
    {
        var tags = GalleryQuery.Tags(_index.Items).Select(t => new TagCount(t.Tag, t.Count) { IsChecked = _filter == GalleryFilter.Tag && string.Equals(t.Tag, _tag, StringComparison.OrdinalIgnoreCase) }).ToList();
        TagList.ItemsSource = tags;
        NoTagsHint.Visibility = tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_filter == GalleryFilter.Tag && !tags.Any(t => t.IsChecked))
        {
            // The last item carrying this tag is gone; fall back to the whole library.
            _filter = GalleryFilter.All; _tag = null;
            FilterAll.IsChecked = true;
        }
    }

    private void UpdateCountLabel()
    {
        int selected = Tiles.SelectedItems.Count;
        CountLabel.Text = _view.Count == _index.Items.Count ? $"{_index.Items.Count} items" : $"{_view.Count} of {_index.Items.Count} items";
        if (selected > 1) CountLabel.Text += $" · {selected} selected";
    }

    private IEnumerable<GalleryItem> Selected => Tiles.SelectedItems.Cast<GalleryItem>().ToList();

    private GalleryItem? Single => Tiles.SelectedItems.Count == 1 ? Tiles.SelectedItems[0] as GalleryItem : null;

    private void ShowStatus(string message)
    {
        StatusLabel.Text = message;
        string key = message.StartsWith("Saved", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Copied", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Imported", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Moved", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Renamed", StringComparison.OrdinalIgnoreCase) ? "Ok"
            : message.Contains("failed", StringComparison.OrdinalIgnoreCase) || message.StartsWith("Could not", StringComparison.OrdinalIgnoreCase) ? "Danger"
            : "Ink3";
        StatusDot.Fill = (Brush)FindResource(key);
    }

    // ----- Header controls -----

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (_index is not null) RefreshView(); }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; Tiles.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter || e.Key == Key.Down) { Tiles.Focus(); if (Tiles.SelectedItem is null && _view.Count > 0) Tiles.SelectedItem = _view[0]; e.Handled = true; }
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        (_sort, _descending) = SortBox.SelectedIndex switch
        {
            1 => (GallerySort.Date, false),
            2 => (GallerySort.Name, false),
            3 => (GallerySort.Name, true),
            4 => (GallerySort.Size, true),
            5 => (GallerySort.Size, false),
            _ => (GallerySort.Date, true),
        };
        if (_index is not null) RefreshView();
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TileSize = e.NewValue;
        if (SizeLabel is not null) SizeLabel.Text = $"{(int)TileSize} px";
    }

    private void DetailToggle_Click(object sender, RoutedEventArgs e) => SetDetailVisible(!_detailVisible);

    private void SetDetailVisible(bool visible)
    {
        _detailVisible = visible;
        DetailPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailToggle.Background = (Brush)FindResource(visible ? "AccentSoft" : "Paper");
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).CaptureNow();

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _index is null) return;
        _filter = ReferenceEquals(sender, FilterImages) ? GalleryFilter.Images
            : ReferenceEquals(sender, FilterVideos) ? GalleryFilter.Videos
            : ReferenceEquals(sender, FilterProjects) ? GalleryFilter.Projects
            : ReferenceEquals(sender, FilterFavourites) ? GalleryFilter.Favourites
            : GalleryFilter.All;
        _tag = null;
        RefreshView();
    }

    private void TagFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || (sender as RadioButton)?.DataContext is not TagCount tag) return;
        _filter = GalleryFilter.Tag; _tag = tag.Tag;
        RefreshView();
    }

    // ----- Tiles -----

    private void Tile_Loaded(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is GalleryItem item) _thumbs.Request(item); }

    private void Tile_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is GalleryItem item && _thumbs is not null) _thumbs.Request(item); }

    private void Tiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetail(Tiles.SelectedItem as GalleryItem ?? Tiles.SelectedItems.Cast<GalleryItem>().LastOrDefault());
        UpdateCountLabel();
    }

    private void TileFavourite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GalleryItem item) SetFavourite(new[] { item }, !item.Favourite);
        e.Handled = true;
    }

    private void Tiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: GalleryItem item }) { OpenItem(item); e.Handled = true; }
    }

    private void Tiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Delete: DeleteSelected(); break;
            case Key.Enter: OpenSelected(); break;
            case Key.F2: BeginRename(); break;
            case Key.Space: ToggleFavouriteSelected(); break;
            case Key.T when !ctrl: OpenTagPopup(); break;
            case Key.C when ctrl && shift: CopyImage(); break;
            case Key.C when ctrl: CopyFiles(); break;
            case Key.A when ctrl: Tiles.SelectAll(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && TagPopup.IsOpen) { TagPopup.IsOpen = false; e.Handled = true; }
    }

    // Mouse: plain ListBox selection for clicks; a marquee over empty space; drag out as files.
    private void Tiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ScrollBar>(source) is not null || FindAncestor<TextBox>(source) is not null || FindAncestor<Button>(source) is not null) return;
        _pressPoint = e.GetPosition(Tiles);
        var container = FindAncestor<ListBoxItem>(source);
        if (container is not null)
        {
            _pressedItem = container.DataContext as GalleryItem;
            _pressOnItem = true;
            // Keep a multi-selection intact on mouse down so it can be dragged; a click resolves on mouse up.
            _deferredSelect = container.IsSelected && Keyboard.Modifiers == ModifierKeys.None && Tiles.SelectedItems.Count > 1;
            if (_deferredSelect) { container.Focus(); e.Handled = true; }
            return;
        }
        _marquee = true;
        _marqueeAdditive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _marqueeBase = _marqueeAdditive ? Tiles.SelectedItems.Cast<GalleryItem>().ToHashSet() : new HashSet<GalleryItem>();
        if (!_marqueeAdditive) Tiles.SelectedItems.Clear();
        Tiles.Focus();
        Tiles.CaptureMouse();
        e.Handled = true;
    }

    private void Tiles_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(Tiles);
        if (_marquee)
        {
            var rect = new Rect(_pressPoint, point);
            Canvas.SetLeft(Marquee, rect.X); Canvas.SetTop(Marquee, rect.Y);
            Marquee.Width = rect.Width; Marquee.Height = rect.Height;
            Marquee.Visibility = Visibility.Visible;
            SelectWithin(rect);
            return;
        }
        if (_pressOnItem && _pressedItem is not null && (Math.Abs(point.X - _pressPoint.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - _pressPoint.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            _pressOnItem = false; _deferredSelect = false;
            if (!Tiles.SelectedItems.Contains(_pressedItem)) { Tiles.SelectedItems.Clear(); Tiles.SelectedItem = _pressedItem; }
            var paths = Selected.Select(i => i.Path).ToArray();
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            data.SetData(InternalDragFormat, true);
            try { DragDrop.DoDragDrop(Tiles, data, DragDropEffects.Copy); } catch { }
        }
    }

    private void Tiles_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_marquee)
        {
            _marquee = false;
            Tiles.ReleaseMouseCapture();
            Marquee.Visibility = Visibility.Collapsed;
        }
        else if (_deferredSelect && _pressedItem is not null)
        {
            Tiles.SelectedItems.Clear();
            Tiles.SelectedItem = _pressedItem;
        }
        _pressOnItem = false; _deferredSelect = false; _pressedItem = null;
    }

    private void SelectWithin(Rect rect)
    {
        foreach (var item in _view)
        {
            if (Tiles.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container || !container.IsVisible) continue;
            Rect bounds;
            try { bounds = container.TransformToAncestor(Tiles).TransformBounds(new Rect(container.RenderSize)); }
            catch (InvalidOperationException) { continue; }
            bool inside = rect.IntersectsWith(bounds) || _marqueeBase.Contains(item);
            if (inside && !container.IsSelected) Tiles.SelectedItems.Add(item);
            else if (!inside && container.IsSelected) Tiles.SelectedItems.Remove(item);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit) return hit;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // ----- Actions -----

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();
    private void OpenOriginal_Click(object sender, RoutedEventArgs e) { foreach (var item in Selected) Try(() => GalleryFiles.OpenWithShell(item.Path), "Could not open"); }
    private void ShowInFolder_Click(object sender, RoutedEventArgs e) { if ((Single ?? Selected.FirstOrDefault()) is { } item) Try(() => GalleryFiles.ShowInFolder(item.Path), "Could not open folder"); }
    private void CopyImage_Click(object sender, RoutedEventArgs e) => CopyImage();
    private void CopyFile_Click(object sender, RoutedEventArgs e) => CopyFiles();
    private void Rename_Click(object sender, RoutedEventArgs e) => BeginRename();
    private void Favourite_Click(object sender, RoutedEventArgs e) => ToggleFavouriteSelected();
    private void Tag_Click(object sender, RoutedEventArgs e) => OpenTagPopup();
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OpenSelected() { foreach (var item in Selected) OpenItem(item); }

    private void OpenItem(GalleryItem item)
    {
        bool video = item.Kind == GalleryItemKind.Video || item.Extension == "vibevideo";
        var handler = video ? OpenInVideoEditor : OpenInEditor;
        if (handler is not null) { Try(() => handler(item), "Could not open"); return; }
        Try(() => GalleryFiles.OpenWithShell(item.Path), "Could not open");
    }

    private void CopyImage()
    {
        var item = Single ?? Selected.FirstOrDefault();
        if (item is null) return;
        if (item.Kind != GalleryItemKind.Image) { ShowStatus("Copy image works for images; use Copy file for videos and projects."); return; }
        var image = ThumbnailCache.LoadImage(item.Path, 0);
        if (image is null) { ShowStatus("Could not read " + item.Name); return; }
        Try(() => { ClipboardService.SetImage(image); ShowStatus("Copied image: " + item.Name); }, "Could not copy");
    }

    private void CopyFiles()
    {
        var paths = Selected.Select(i => i.Path).ToList();
        if (paths.Count == 0) return;
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(paths.ToArray());
        Try(() => { Clipboard.SetFileDropList(list); ShowStatus(paths.Count == 1 ? "Copied file: " + System.IO.Path.GetFileName(paths[0]) : $"Copied {paths.Count} files"); }, "Could not copy");
    }

    private void ToggleFavouriteSelected()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        SetFavourite(items, !items.All(i => i.Favourite));
    }

    private void SetFavourite(IEnumerable<GalleryItem> items, bool on)
    {
        foreach (var item in items)
        {
            item.Favourite = on;
            Try(item.SaveSidecar, "Could not save metadata");
        }
        UpdateCounts();
        if (_filter == GalleryFilter.Favourites) RefreshView();
        if (_detailItem is not null) UpdateDetail(_detailItem);
    }

    private void DeleteSelected()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        int anchor = _view.FindIndex(items.Contains);
        int done = 0;
        _suspendRefresh++;
        try
        {
            foreach (var item in items)
            {
                try { GalleryFiles.Delete(item, _thumbs); _index.Remove(item); done++; }
                catch (Exception ex) { ShowStatus("Delete failed: " + ex.Message); }
            }
        }
        finally { _suspendRefresh--; }
        RefreshView();
        if (done > 0)
        {
            ShowStatus(done == 1 ? "Moved to Recycle Bin: " + items[0].Name : $"Moved {done} files to the Recycle Bin");
            if (_view.Count > 0) { var next = _view[Math.Clamp(anchor, 0, _view.Count - 1)]; Tiles.SelectedItem = next; Tiles.ScrollIntoView(next); }
        }
    }

    private void BeginRename()
    {
        var item = Single;
        if (item is null) { ShowStatus("Select one capture to rename it."); return; }
        item.IsRenaming = true;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || e.NewValue is not true || box.DataContext is not GalleryItem item) return;
        box.Text = item.Name;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            int stem = System.IO.Path.GetFileNameWithoutExtension(item.Name).Length;
            box.Select(0, stem);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not GalleryItem item) return;
        if (e.Key == Key.Enter) { CommitRename(box, item); Tiles.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { item.IsRenaming = false; Tiles.Focus(); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is GalleryItem item && item.IsRenaming) CommitRename(box, item);
    }

    private void CommitRename(TextBox box, GalleryItem item)
    {
        item.IsRenaming = false;
        var name = box.Text.Trim();
        if (name.Length == 0 || string.Equals(name, item.Name, StringComparison.Ordinal)) return;
        try
        {
            var newPath = GalleryFiles.Rename(item, name);
            _index.Rekey(item, newPath);
            _thumbs.Invalidate(item);
            _thumbs.Request(item);
            Tiles.SelectedItem = item;
            UpdateDetail(item);
            ShowStatus("Renamed: " + item.Name);
        }
        catch (Exception ex) { ShowStatus("Rename failed: " + ex.Message); }
    }

    // ----- Tags -----

    private void OpenTagPopup()
    {
        if (Tiles.SelectedItems.Count == 0) { ShowStatus("Select a capture to tag it."); return; }
        BuildTagToggles();
        NewTagBox.Text = "";
        TagPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => NewTagBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BuildTagToggles()
    {
        _buildingToggles = true;
        var items = Selected.ToList();
        TagToggles.ItemsSource = GalleryQuery.Tags(_index.Items).Select(t => new TagToggle { Tag = t.Tag, IsOn = items.Count > 0 && items.All(i => i.Sidecar.HasTag(t.Tag)) }).ToList();
        _buildingToggles = false;
    }

    private void TagToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_buildingToggles || sender is not CheckBox box || box.Tag is not string tag) return;
        ApplyTag(Selected, tag, box.IsChecked == true);
    }

    private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var tag = CaptureSidecar.NormalizeTag(NewTagBox.Text);
        if (tag.Length == 0) return;
        ApplyTag(Selected, tag, true);
        NewTagBox.Text = "";
        BuildTagToggles();
        e.Handled = true;
    }

    private void DetailTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _detailItem is null) return;
        var tag = CaptureSidecar.NormalizeTag(DetailTagBox.Text);
        if (tag.Length == 0) return;
        ApplyTag(new[] { _detailItem }, tag, true);
        DetailTagBox.Text = "";
        e.Handled = true;
    }

    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem is not null && (sender as FrameworkElement)?.Tag is string tag) ApplyTag(new[] { _detailItem }, tag, false);
    }

    private void ApplyTag(IEnumerable<GalleryItem> items, string tag, bool add)
    {
        foreach (var item in items)
        {
            bool changed = add ? item.Sidecar.AddTag(tag) : item.Sidecar.RemoveTag(tag);
            if (changed) { Try(item.SaveSidecar, "Could not save metadata"); item.NotifyAll(); }
        }
        if (_filter == GalleryFilter.Tag) RefreshView(); else UpdateTags();
        if (_detailItem is not null) UpdateDetail(_detailItem);
    }

    // ----- Detail pane -----

    private void UpdateDetail(GalleryItem? item)
    {
        _detailItem = item;
        int version = ++_detailVersion;
        bool has = item is not null;
        DetailEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        DetailBody.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        BindingOperations.ClearBinding(DetailImage, Image.SourceProperty);
        if (item is null) { DetailName.Text = ""; DetailSubtitle.Text = ""; DetailImage.Source = null; return; }
        DetailName.Text = item.Name;
        DetailSubtitle.Text = item.Favourite ? "Favourite · " + item.Hint : item.Hint;
        MetaKind.Text = item.Kind switch { GalleryItemKind.Video => "Video", GalleryItemKind.Project => "Project", _ => "Image" } + (item.CaptureKind == CaptureKind.Unknown ? "" : " · " + item.CaptureKind);
        MetaSize.Text = item.Kind == GalleryItemKind.Video && item.Duration is double d ? $"{GalleryItem.FormatDuration(d)}" + (item.Width > 0 ? $" · {item.Width} × {item.Height}" : "") : item.Width > 0 ? $"{item.Width} × {item.Height} px" : "—";
        MetaBytes.Text = GalleryItem.FormatSize(item.Size) + " · " + item.Extension;
        MetaCreated.Text = item.Created.ToString("yyyy-MM-dd HH:mm:ss");
        MetaApp.Text = item.SourceProcess ?? "—";
        MetaTitle.Text = item.SourceTitle ?? "—";
        MetaFolder.Text = item.Folder;
        DetailTags.ItemsSource = item.Tags.ToList();
        if (item.Kind == GalleryItemKind.Image)
        {
            DetailImage.Source = item.Thumbnail;
            var path = item.Path;
            Task.Run(() => ThumbnailCache.LoadImage(path, 1600)).ContinueWith(t =>
            {
                if (version == _detailVersion && t.Status == TaskStatus.RanToCompletion && t.Result is not null) DetailImage.Source = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else
        {
            DetailImage.SetBinding(Image.SourceProperty, new Binding(nameof(GalleryItem.Thumbnail)) { Source = item });
            _thumbs.Request(item);
        }
    }

    // ----- Drag and drop in -----

    private const string InternalDragFormat = "Vibershot.GalleryDrag";

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool external = e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent(InternalDragFormat);
        e.Effects = external ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(InternalDragFormat)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        int imported = 0, skipped = 0;
        string? last = null;
        _suspendRefresh++;
        try
        {
            foreach (var file in files)
            {
                try
                {
                    var target = GalleryFiles.Import(file, _config.OutputFolder);
                    if (target is null) { skipped++; continue; }
                    _index.Refresh(target);
                    imported++; last = target;
                }
                catch (Exception ex) { ShowStatus("Import failed: " + ex.Message); }
            }
        }
        finally { _suspendRefresh--; }
        if (last is not null) { _pendingSelect = last; FilterAll.IsChecked = true; }
        RefreshView();
        if (imported > 0) ShowStatus($"Imported {imported} file{(imported == 1 ? "" : "s")} into {_config.OutputFolder}" + (skipped > 0 ? $" · {skipped} unsupported skipped" : ""));
        else if (skipped > 0) ShowStatus("Nothing imported: only images, videos and Vibershot projects are supported.");
        e.Handled = true;
    }

    // ----- Window settings -----

    private void RestorePlacement(GallerySettings settings)
    {
        if (settings.WindowWidth is not double w || settings.WindowHeight is not double h || w < MinWidth || h < MinHeight) return;
        double left = settings.WindowLeft ?? Left, top = settings.WindowTop ?? Top;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(left, top, w, h))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = w; Height = h; Left = left; Top = top;
    }

    private void SaveWindowSettings()
    {
        var settings = _config.Gallery;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : base.RestoreBounds;
        settings.WindowLeft = bounds.Left; settings.WindowTop = bounds.Top; settings.WindowWidth = bounds.Width; settings.WindowHeight = bounds.Height;
        settings.ThumbnailSize = (int)TileSize;
        settings.ShowDetailPane = _detailVisible;
        try { _config.Save(); } catch { /* Placement is a convenience; losing it is harmless. */ }
    }

    private void Try(Action action, string failure)
    {
        try { action(); }
        catch (Exception ex) { ShowStatus(failure + ": " + ex.Message); }
    }
}
