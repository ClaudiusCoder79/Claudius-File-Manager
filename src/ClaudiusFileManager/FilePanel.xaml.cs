using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudiusFileManager;

/// <summary>One Total-Commander-style file panel.</summary>
public partial class FilePanel : UserControl
{
    public event EventHandler? Activated;
    /// <summary>Raised after the panel shows a new folder.</summary>
    public event Action<FilePanel>? Navigated;
    /// <summary>Raised when the cursor or selection moves to another item (drives the Preview window).</summary>
    public event Action<FilePanel>? CursorChanged;
    public event Action<FilePanel, string>? CommandRequested;
    public event Action<FilePanel, string[], string, bool>? FilesDropped;

    public string CurrentPath { get; private set; } = "";
    public bool ShowHidden { get; set; }
    /// <summary>Show the total size of each folder in the Size column (measured in the background).</summary>
    public bool ShowFolderSizes { get; set; } = true;
    public bool IsActivePanel { get; private set; }
    /// <summary>The panel is showing the inside of a ZIP archive.</summary>
    public bool InArchive { get; private set; }

    private List<FileItem> _all = new();
    private List<DriveEntry> _drives = new();
    private ICollectionView? _view;
    private string _sortKey = "Name";
    private bool _sortAsc = true;
    private Stack<string> _history => Tab.History;

    // Tabs: each one remembers its folder, back history, sort order and cursor.
    private sealed class TabState
    {
        public string Path = "";
        public Stack<string> History = new();
        public string SortKey = "Name";
        public bool SortAsc = true;
        public string? Cursor;
    }
    public sealed record TabEntry(string Title, string Path, int Index, bool IsCurrent);
    private readonly List<TabState> _tabs = new() { new TabState() };
    private int _tabIndex;
    private TabState Tab => _tabs[_tabIndex];

    // Search in subfolders
    private bool _deep;
    private string? _searchRoot;            // non-null while search results are shown
    private bool _searching;
    private bool _searchCapped;
    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer _searchTimer;
    private ObservableCollection<FileItem>? _items;
    private const int MaxResults = 5000;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _refreshTimer;
    private Point _dragStart;
    private bool _dragArmed;
    private long _freeBytes = -1;
    private CancellationTokenSource? _sizeCts;

    public FilePanel()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Refresh(); };
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearchNow(); };
        PreviewMouseDown += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        IsKeyboardFocusWithinChanged += (_, e) => { if ((bool)e.NewValue) Activated?.Invoke(this, EventArgs.Empty); };
        Unloaded += (_, _) => { _watcher?.Dispose(); _sizeCts?.Cancel(); _searchCts?.Cancel(); };
        Loc.Changed += OnLanguageChanged;
        // The cursor can move without the selection changing (Insert key, clicks with Ctrl): report that too.
        FileList.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is ListBoxItem) CursorChanged?.Invoke(this);
        }), true);
        LoadDrives();
        UpdateHeaders();
    }

    private void OnLanguageChanged()
    {
        LoadDrives();
        UpdateHeaders();
        Refresh();          // re-creates rows so "Folder" etc. are re-translated
        UpdateStatus();
    }

    // ───────────────────────── public API ─────────────────────────

    public void SetActive(bool active)
    {
        IsActivePanel = active;
        Card.SetResourceReference(Border.BorderBrushProperty, active ? "AccentBrush" : "SeparatorBrush");
    }

    /// <summary>Item under the keyboard cursor (falls back to the selected item).</summary>
    public FileItem? CursorItem
    {
        get
        {
            if (Keyboard.FocusedElement is ListBoxItem lbi &&
                ItemsControl.ItemsControlFromItemContainer(lbi) == FileList &&
                lbi.DataContext is FileItem fi)
                return fi;
            return FileList.SelectedItem as FileItem;
        }
    }

    /// <summary>Marked items, or the cursor item if nothing is marked (Total Commander semantics).</summary>
    public List<FileItem> GetSelection()
    {
        var sel = FileList.SelectedItems.Cast<FileItem>().Where(i => !i.IsParent && !i.IsVirtual).ToList();
        if (sel.Count == 0 && CursorItem is { IsParent: false, IsVirtual: false } c) sel.Add(c);
        return sel;
    }

    public void SelectAllItems()
    {
        FileList.SelectAll();
        foreach (var p in FileList.SelectedItems.Cast<FileItem>().Where(i => i.IsParent).ToList())
            FileList.SelectedItems.Remove(p);
    }

    public void SelectNoItems()
    {
        var cur = CursorItem;
        FileList.SelectedItems.Clear();
        if (cur != null) FileList.SelectedItem = cur;
    }

    public void InvertSelection()
    {
        var selected = FileList.SelectedItems.Cast<FileItem>().ToHashSet();
        FileList.SelectedItems.Clear();
        foreach (var it in FileList.Items.Cast<FileItem>())
            if (!it.IsParent && !selected.Contains(it)) FileList.SelectedItems.Add(it);
    }

    public void LoadDrives()
    {
        var list = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            string glyph = d.DriveType switch
            {
                DriveType.Network => "",
                DriveType.Removable => "",
                DriveType.CDRom => "",
                _ => ""
            };
            string tip = d.Name;
            try
            {
                if (d.DriveType != DriveType.Network && d.IsReady)
                {
                    var label = string.IsNullOrEmpty(d.VolumeLabel) ? Loc.T("LocalDisk") : d.VolumeLabel;
                    tip = Loc.F("DriveTip", label, d.Name.TrimEnd('\\'),
                                FileItem.FormatSize(d.AvailableFreeSpace), FileItem.FormatSize(d.TotalSize));
                }
            }
            catch { /* drive vanished */ }
            if (d.DriveType == DriveType.Network && NetworkOps.UncOf(d.Name) is { } unc) tip += "\n" + unc;
            list.Add(new DriveEntry(d.Name.TrimEnd('\\'), d.Name, glyph, tip));
        }
        // Cloud storage folders (iCloud Drive, OneDrive, Dropbox, Google Drive, Box …) and the Network view
        foreach (var (name, path) in NetworkOps.CloudFolders())
            list.Add(new DriveEntry(name, path, "\uE753", path));
        list.Add(new DriveEntry(Loc.T("Network"), NetworkOps.NetRoot, "\uE968", Loc.T("NetworkTip")));
        _drives = list;
        SyncDrive();
    }

    public bool Navigate(string path, bool pushHistory = true, string? selectName = null)
    {
        bool hadFocus = IsKeyboardFocusWithin;
        CancelSearch();
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.Length == 2 && path[1] == ':') path += "\\";

        string full;
        var items = new List<FileItem>();
        bool isVirtual = NetworkOps.IsVirtualPath(path);
        bool inZip = false;
        try
        {
            if (!isVirtual && ZipOps.TrySplit(path, out var zip, out var inner))
            {
                inZip = true;
                zip = Path.GetFullPath(zip);
                full = ZipOps.Combine(zip, inner);
                BuildArchiveView(zip, inner, items);
            }
            else if (NetworkOps.IsNetRoot(path))
            {
                full = NetworkOps.NetRoot;
                BuildNetworkView(items);
            }
            else if (NetworkOps.IsServerPath(path))
            {
                full = @"\\" + path[2..].TrimEnd('\\');
                BuildServerView(full, items);
            }
            else
            {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (full.Length == 2 && full[1] == ':') full += "\\";
            var di = new DirectoryInfo(full);
            if (!di.Exists) throw new DirectoryNotFoundException(Loc.F("ErrNotExist", path));
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
            };
            if (di.Parent != null) items.Add(FileItem.ParentOf(di.Parent.FullName));
            else if (NetworkOps.IsShareRoot(full)) items.Add(FileItem.ParentOf(NetworkOps.ServerOf(full)));
            foreach (var fsi in di.EnumerateFileSystemInfos("*", opts))
            {
                try { items.Add(FileItem.From(fsi)); } catch { /* skip unreadable entry */ }
            }
            }
        }
        catch (Exception ex)
        {
            IosDialog.Alert(Window.GetWindow(this), Loc.T("ErrCantOpenFolder"), ex.Message);
            SyncDrive();
            PathBox.Text = CurrentPath;
            return false;
        }

        if (pushHistory && CurrentPath.Length > 0 && !ShellOps.SamePath(CurrentPath, full))
            _history.Push(CurrentPath);

        bool folderChanged = !ShellOps.SamePath(CurrentPath, full);
        CurrentPath = full;
        InArchive = inZip;
        _all = items;
        if (folderChanged) SearchBox.Text = "";
        ApplySort();

        PathBox.Text = full;
        TitleText.Text = TitleOf(full);
        Tab.Path = full;
        UpdateTabBar();
        BackButton.IsEnabled = _history.Count > 0;
        UpButton.IsEnabled = !NetworkOps.IsNetRoot(full) &&
                             (isVirtual || NetworkOps.IsShareRoot(full) || Directory.GetParent(full) != null);
        _freeBytes = -1;
        try
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && root.Length <= 3)
                _freeBytes = new DriveInfo(root).AvailableFreeSpace;
        }
        catch { /* ignore */ }

        SyncDrive();
        if (isVirtual || inZip) { _watcher?.Dispose(); _watcher = null; }
        else if (folderChanged) Watch(full);

        if (selectName != null) SelectByName(selectName);
        else if (FileList.Items.Count > 0) FileList.SelectedIndex = 0;

        UpdateStatus();
        if (hadFocus) FocusList();
        StartFolderSizes();
        Navigated?.Invoke(this);
        return true;
    }

    private static string TitleOf(string full)
    {
        var name = Path.GetFileName(full);
        return NetworkOps.IsNetRoot(full) ? Loc.T("Network")
             : NetworkOps.IsServerPath(full) ? full[2..]
             : string.IsNullOrEmpty(name) ? full : name;
    }

    /// <summary>Measures every folder in the list, one after another, on a background thread.</summary>
    private void StartFolderSizes()
    {
        _sizeCts?.Cancel();
        _sizeCts = null;
        if (!ShowFolderSizes) return;

        var todo = new List<FileItem>();
        foreach (var it in _all.Where(i => i.IsDir && !i.IsParent && !i.IsVirtual && !i.IsInArchive))
        {
            if (FolderSizer.TryGetCached(it.FullPath, out var cached)) it.SetFolderSize(cached);
            else { it.MarkCalculating(); todo.Add(it); }
        }
        if (todo.Count == 0) { AfterSizesChanged(); return; }

        var cts = new CancellationTokenSource();
        _sizeCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            foreach (var it in todo)
            {
                if (token.IsCancellationRequested) return;
                long bytes;
                try { bytes = FolderSizer.Measure(it.FullPath, token).Bytes; }
                catch (OperationCanceledException) { return; }
                catch { bytes = 0; }
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested) { it.SetFolderSize(bytes); UpdateStatus(); }
                });
            }
            await Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) AfterSizesChanged(); });
        }, token);
    }

    /// <summary>When sorted by size, re-sort once every folder size is known.</summary>
    private void AfterSizesChanged()
    {
        UpdateStatus();
        if (_sortKey != "Size" || _view == null) return;
        bool hadFocus = FileList.IsKeyboardFocusWithin;
        var cursorName = CursorItem?.Name;
        var marked = FileList.SelectedItems.Cast<FileItem>().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplySort();
        foreach (var it in FileList.Items.Cast<FileItem>())
            if (marked.Contains(it.Name)) FileList.SelectedItems.Add(it);
        if (hadFocus) FocusList(cursorName);
    }

    public void Refresh()
    {
        if (CurrentPath.Length == 0) return;
        if (_searchRoot != null) { RunSearchNow(); return; }
        if (NetworkOps.IsVirtualPath(CurrentPath) || InArchive && ZipOps.IsInside(CurrentPath))
        {
            var keep = CursorItem?.Name;
            Navigate(CurrentPath, false, keep);
            return;
        }

        // The folder itself may have been deleted or renamed: climb to the nearest existing parent.
        if (!Directory.Exists(CurrentPath))
        {
            var p = Directory.GetParent(CurrentPath);
            while (p != null && !p.Exists) p = p.Parent;
            if (p != null) Navigate(p.FullName, false);
            return;
        }

        bool hadFocus = FileList.IsKeyboardFocusWithin;
        var marked = FileList.SelectedItems.Cast<FileItem>().Select(i => i.Name)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cursorName = CursorItem?.Name;
        int idx = FileList.SelectedIndex;
        if (!Navigate(CurrentPath, false)) return;

        FileList.SelectedItems.Clear();
        foreach (var it in FileList.Items.Cast<FileItem>())
            if (marked.Contains(it.Name)) FileList.SelectedItems.Add(it);

        if (FileList.SelectedItems.Count == 0 && FileList.Items.Count > 0)
            FileList.SelectedIndex = Math.Clamp(idx, 0, FileList.Items.Count - 1);

        if (hadFocus) FocusList(cursorName);
    }

    public void SelectByName(string name)
    {
        var item = FileList.Items.Cast<FileItem>()
            .FirstOrDefault(i => !i.IsParent && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            FileList.SelectedItem = item;
            FileList.ScrollIntoView(item);
        }
        else if (FileList.Items.Count > 0) FileList.SelectedIndex = 0;
    }

    public void FocusList(string? cursorName = null)
    {
        if (FileList.Items.Count == 0) { FileList.Focus(); return; }
        if (FileList.SelectedIndex < 0) FileList.SelectedIndex = 0;

        object item = FileList.SelectedItem!;
        if (cursorName != null)
        {
            var match = FileList.Items.Cast<FileItem>()
                .FirstOrDefault(i => string.Equals(i.Name, cursorName, StringComparison.OrdinalIgnoreCase));
            if (match != null) item = match;
        }
        FileList.ScrollIntoView(item);
        FileList.UpdateLayout();
        if (FileList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c) c.Focus();
        else FileList.Focus();
    }

    public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
    public void FocusPath() { PathBox.Focus(); PathBox.SelectAll(); }

    public void Open(FileItem it)
    {
        if (it.IsParent) { GoUp(); return; }
        if (it.IsDir) { Navigate(it.FullPath); return; }
        // Like Total Commander: Enter on a ZIP opens it as a folder.
        if (!it.IsInArchive && ZipOps.IsZipName(it.Name)) { Navigate(it.FullPath); return; }
        if (it.IsInArchive)
        {
            try { ShellOpen(ZipOps.ExtractToTemp(it.Archive!, it.InnerPath)); }
            catch (Exception ex) { IosDialog.Alert(Window.GetWindow(this), Loc.T("ErrCantOpen"), ex.Message); }
            return;
        }
        ShellOpen(it.FullPath);
    }

    public static void ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            });
        }
        catch (Exception ex)
        {
            IosDialog.Alert(Application.Current.MainWindow, Loc.T("ErrCantOpen"), ex.Message);
        }
    }

    public void GoUp()
    {
        if (NetworkOps.IsNetRoot(CurrentPath)) return;
        if (NetworkOps.IsServerPath(CurrentPath)) { Navigate(NetworkOps.NetRoot, true, CurrentPath[2..]); return; }
        var parent = Directory.GetParent(CurrentPath);
        if (parent == null)
        {
            if (NetworkOps.IsShareRoot(CurrentPath))
                Navigate(NetworkOps.ServerOf(CurrentPath), true, CurrentPath.TrimEnd('\\').Split('\\').Last());
            return;
        }
        Navigate(parent.FullName, true, Path.GetFileName(CurrentPath));
    }

    // ───────────────────────── ZIP archives ─────────────────────────

    private static void BuildArchiveView(string zip, string inner, List<FileItem> items)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            int cut = inner.LastIndexOf('/');
            items.Add(FileItem.ParentOf(inner.Length == 0 ? Path.GetDirectoryName(zip)!
                                                           : ZipOps.Combine(zip, cut < 0 ? "" : inner[..cut])));
            foreach (var e in ZipOps.List(zip, inner)) items.Add(FileItem.FromArchive(zip, e));
        }
        catch (InvalidDataException ex) { throw new IOException(Loc.F("ErrBadZip", Path.GetFileName(zip)), ex); }
        finally { Mouse.OverrideCursor = null; }
    }

    // ───────────────────────── network views ─────────────────────────

    /// <summary>Mapped drives, network locations and computers.</summary>
    private void BuildNetworkView(List<FileItem> items)
    {
        var servers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network))
        {
            var letter = d.Name.TrimEnd('\\');
            var unc = NetworkOps.UncOf(d.Name);
            items.Add(FileItem.Link(unc != null ? $"{letter}  {unc}" : letter, d.Name, "\uE8CE", Palette.Blue));
            if (unc != null) servers.Add(NetworkOps.ServerOf(unc));
        }
        foreach (var (name, target) in NetworkOps.NetworkLocations())
        {
            items.Add(FileItem.Link($"{name}  ({target})", target, "\uE71B", Palette.Teal));
            servers.Add(NetworkOps.ServerOf(target));
        }
        foreach (var c in NetworkOps.KnownComputers) servers.Add(c);
        foreach (var s in servers) items.Add(FileItem.Link(s[2..], s, "\uE977", Palette.Indigo));

        // Look for more computers in the background; the view refreshes when they are found.
        NetworkOps.DiscoverComputers(() => Dispatcher.InvokeAsync(() =>
        {
            if (NetworkOps.IsNetRoot(CurrentPath)) Refresh();
        }));
    }

    /// <summary>The shared folders on one computer.</summary>
    private void BuildServerView(string server, List<FileItem> items)
    {
        items.Add(FileItem.ParentOf(NetworkOps.NetRoot));
        bool hidden = ShowHidden;
        var task = Task.Run(() => NetworkOps.GetShares(server, hidden));
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(Loc.F("ErrNetTimeout", server));
        }
        catch (AggregateException ex) { throw ex.InnerException ?? ex; }
        finally { Mouse.OverrideCursor = null; }
        foreach (var (name, remark) in task.Result)
            items.Add(FileItem.Link(name, server + "\\" + name, "\uE8B7", Palette.Folder, remark));
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (root == NetworkOps.NetRoot) return path.StartsWith(@"\\");
        var r = root.TrimEnd('\\');
        return path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
               path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public void GoBack()
    {
        if (_history.Count == 0) return;
        var p = _history.Pop();
        Navigate(p, false);
    }

    // ───────────────────────── internals ─────────────────────────

    private void SyncDrive()
    {
        var best = _drives.Where(d => IsUnder(CurrentPath, d.Root))
                          .OrderByDescending(d => d.Root.Length).FirstOrDefault();
        DriveList.ItemsSource = _drives
            .Select(d => d with { IsCurrent = best != null && d.Root == best.Root })
            .ToList();
    }

    private void ApplySort()
    {
        var parent = _all.Where(i => i.IsParent);
        var dirs = _all.Where(i => i.IsDir && !i.IsParent).ToList();
        var files = _all.Where(i => !i.IsDir).ToList();
        dirs.Sort(Compare);
        files.Sort(Compare);

        var sorted = new ObservableCollection<FileItem>(parent.Concat(dirs).Concat(files));
        _items = sorted;
        _view = CollectionViewSource.GetDefaultView(sorted);
        _view.Filter = FilterItem;
        FileList.ItemsSource = _view;
        UpdateHeaders();
    }

    private int Compare(FileItem a, FileItem b)
    {
        int r = _sortKey switch
        {
            "Ext" => string.Compare(a.Ext, b.Ext, StringComparison.OrdinalIgnoreCase),
            "Size" => a.Size.CompareTo(b.Size),
            "Date" => a.Modified.CompareTo(b.Modified),
            _ => 0
        };
        if (r == 0) r = Natural.Compare(a.Name, b.Name);
        return _sortAsc ? r : -r;
    }

    private bool FilterItem(object o)
    {
        if (_searchRoot != null) return true;      // search results are already matches
        var q = SearchBox.Text.Trim();
        return q.Length == 0 || o is FileItem { IsParent: true } ||
               ((FileItem)o).Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateHeaders()
    {
        string Arrow(string key) => _sortKey == key ? (_sortAsc ? "  ↑" : "  ↓") : "";
        HdrName.Content = Loc.T("ColName") + Arrow("Name");
        HdrExt.Content = Loc.T("ColExt") + Arrow("Ext");
        HdrSize.Content = Loc.T("ColSize") + Arrow("Size");
        HdrDate.Content = Loc.T("ColModified") + Arrow("Date");
    }

    private void UpdateStatus()
    {
        if (_view == null) return;
        if (_searchRoot != null)
        {
            int n = _all.Count;
            var selected = FileList.SelectedItems.Cast<FileItem>().ToList();
            StatusLeft.Text = _searching ? Loc.F("Searching", n)
                            : selected.Count > 1 ? Loc.F("StatusSelected", selected.Count, n, FileItem.FormatSize(selected.Sum(i => i.Size)))
                            : Loc.F(_searchCapped ? "SearchFoundCapped" : "SearchFound", n);
            StatusRight.Text = Loc.F("SearchIn", TitleOf(_searchRoot));
            return;
        }
        var visible = _view.Cast<FileItem>().Where(i => !i.IsParent).ToList();
        int dirs = visible.Count(i => i.IsDir), files = visible.Count - dirs;
        var sel = FileList.SelectedItems.Cast<FileItem>().Where(i => !i.IsParent).ToList();

        StatusLeft.Text = sel.Count > 1
            ? Loc.F("StatusSelected", sel.Count, visible.Count, FileItem.FormatSize(sel.Sum(i => i.Size)))
            : Loc.F("StatusCounts", dirs, files);
        StatusRight.Text = _freeBytes >= 0 ? Loc.F("Available", FileItem.FormatSize(_freeBytes)) : "";
    }

    private void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                               NotifyFilters.LastWrite | NotifyFilters.Attributes
            };
            w.Created += (_, _) => Kick();
            w.Deleted += (_, _) => Kick();
            w.Changed += (_, _) => Kick();
            w.Renamed += (_, _) => Kick();
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { /* some locations can't be watched; Ctrl+R still works */ }
    }

    private void Kick() => Dispatcher.InvokeAsync(() =>
    {
        if (_searchRoot != null) return;     // results stay put until the user refreshes
        FolderSizer.Forget(CurrentPath);
        _refreshTimer.Stop();
        _refreshTimer.Start();
    });

    private void ToggleMarkAndAdvance()
    {
        if (Keyboard.FocusedElement is not ListBoxItem c || ItemsControl.ItemsControlFromItemContainer(c) != FileList)
            return;
        if (c.DataContext is FileItem { IsParent: false }) c.IsSelected = !c.IsSelected;
        int i = FileList.ItemContainerGenerator.IndexFromContainer(c);
        if (i + 1 < FileList.Items.Count)
        {
            FileList.ScrollIntoView(FileList.Items[i + 1]);
            FileList.UpdateLayout();
            (FileList.ItemContainerGenerator.ContainerFromIndex(i + 1) as ListBoxItem)?.Focus();
        }
    }

    // ───────────────────────── tabs ─────────────────────────

    public int TabCount => _tabs.Count;
    public int TabIndex => _tabIndex;

    /// <summary>Folders of all tabs (for saving between sessions).</summary>
    public List<string> TabPaths => _tabs.Select(t => t == Tab ? (_searchRoot ?? CurrentPath) : t.Path).ToList();

    /// <summary>Re-opens the tabs from the last session; the chosen tab is shown.</summary>
    public void RestoreTabs(IList<string>? paths, int index, string fallback)
    {
        var list = (paths ?? new List<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) { Navigate(fallback, false); return; }
        _tabs.Clear();
        foreach (var p in list) _tabs.Add(new TabState { Path = p });
        _tabIndex = Math.Clamp(index, 0, _tabs.Count - 1);
        var start = Tab.Path;
        bool ok = ZipOps.IsInside(start) || NetworkOps.IsVirtualPath(start) || SafeExists(start);
        if (!ok || !Navigate(start, false)) Navigate(fallback, false);
        UpdateTabBar();
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    private void SaveTabState()
    {
        Tab.Path = _searchRoot ?? CurrentPath;
        Tab.SortKey = _sortKey;
        Tab.SortAsc = _sortAsc;
        Tab.Cursor = _searchRoot == null ? CursorItem?.Name : null;
    }

    /// <summary>Opens a new tab (Ctrl+T) showing <paramref name="path"/>, or the current folder.</summary>
    public void NewTab(string? path = null)
    {
        SaveTabState();
        var t = new TabState { Path = path ?? Tab.Path, SortKey = _sortKey, SortAsc = _sortAsc };
        _tabs.Insert(_tabIndex + 1, t);
        _tabIndex++;
        ShowTab();
    }

    public void SwitchTab(int index)
    {
        if (index < 0 || index >= _tabs.Count || index == _tabIndex) return;
        SaveTabState();
        _tabIndex = index;
        ShowTab();
    }

    public void NextTab(int step)
    {
        if (_tabs.Count > 1) SwitchTab(((_tabIndex + step) % _tabs.Count + _tabs.Count) % _tabs.Count);
    }

    /// <summary>Closes a tab (Ctrl+W); the last tab can't be closed.</summary>
    public void CloseTab(int? index = null)
    {
        int i = index ?? _tabIndex;
        if (_tabs.Count <= 1 || i < 0 || i >= _tabs.Count) return;
        if (i != _tabIndex)
        {
            _tabs.RemoveAt(i);
            if (i < _tabIndex) _tabIndex--;
            UpdateTabBar();
            return;
        }
        _tabs.RemoveAt(i);
        _tabIndex = Math.Min(i, _tabs.Count - 1);
        ShowTab();
    }

    private void ShowTab()
    {
        _sortKey = Tab.SortKey;
        _sortAsc = Tab.SortAsc;
        CancelSearch();
        SearchBox.Text = "";
        var cursor = Tab.Cursor;
        if (!Navigate(Tab.Path, false, cursor))
            Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false);
        UpdateTabBar();
        FocusList(cursor);
    }

    private void UpdateTabBar()
    {
        TabBar.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_tabs.Count <= 1) { TabList.ItemsSource = null; return; }
        TabList.ItemsSource = _tabs.Select((t, i) =>
        {
            var path = i == _tabIndex ? (_searchRoot ?? CurrentPath) : t.Path;
            return new TabEntry(path.Length == 0 ? "…" : TitleOf(path), path, i, i == _tabIndex);
        }).ToList();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TabEntry t) return;
        Activated?.Invoke(this, EventArgs.Empty);
        if (t.IsCurrent) FocusList(); else SwitchTab(t.Index);
    }

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Middle-click closes a tab, as in a browser.
        if (e.ChangedButton != MouseButton.Middle || (sender as FrameworkElement)?.DataContext is not TabEntry t) return;
        Activated?.Invoke(this, EventArgs.Empty);
        CloseTab(t.Index);
        e.Handled = true;
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not TabEntry t) return;
        Activated?.Invoke(this, EventArgs.Empty);
        CloseTab(t.Index);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        NewTab();
    }

    // ───────────────────────── search in subfolders ─────────────────────────

    public bool IsShowingSearchResults => _searchRoot != null;

    private bool CanDeepSearch
    {
        get
        {
            var root = _searchRoot ?? CurrentPath;
            return root.Length > 0 && !InArchive && !NetworkOps.IsVirtualPath(root);
        }
    }

    /// <summary>Search in subfolders on or off (the button next to the search box, or Alt+F7).</summary>
    public bool DeepSearch
    {
        get => _deep;
        set
        {
            if (_deep == value) return;
            _deep = value;
            DeepGlyph.SetResourceReference(TextBlock.ForegroundProperty, value ? "AccentBrush" : "SecondaryLabelBrush");
            if (value) DeepButton.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
            else DeepButton.ClearValue(BackgroundProperty);
            SearchBox.SetResourceReference(TagProperty, value ? "L.SearchDeep" : "L.Search");
            // Re-run what is typed with the new setting.
            if (SearchBox.Text.Trim().Length > 0) SearchBox_TextChanged(SearchBox, null!);
        }
    }

    public void FocusDeepSearch()
    {
        DeepSearch = true;
        FocusSearch();
    }

    private void Deep_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        DeepSearch = !DeepSearch;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void CancelSearch()
    {
        _searchTimer.Stop();
        _searchCts?.Cancel();
        _searchCts = null;
        _searchRoot = null;
        _searching = false;
    }

    /// <summary>Back from the search results to the folder the search started in.</summary>
    private void EndSearch()
    {
        if (_searchRoot == null) { CancelSearch(); return; }
        var root = _searchRoot;
        CancelSearch();
        Navigate(root, false);
    }

    private void RunSearchNow()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) { EndSearch(); return; }
        if (!CanDeepSearch) return;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;
        var root = _searchRoot ?? CurrentPath;
        _searchRoot = root;
        _searching = true;
        _searchCapped = false;
        _sizeCts?.Cancel();

        _all = new List<FileItem>();
        ApplySort();
        TitleText.Text = Loc.F("SearchTitle", q);
        UpdateStatus();
        UpdateTabBar();

        bool hidden = ShowHidden;
        bool wild = q.IndexOfAny(new[] { '*', '?' }) >= 0;
        _ = Task.Run(() =>
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = hidden ? 0 : FileAttributes.Hidden | FileAttributes.System
            };
            var batch = new List<FileItem>();
            var sw = Stopwatch.StartNew();
            int total = 0;
            bool capped = false;
            try
            {
                foreach (var fsi in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
                {
                    if (token.IsCancellationRequested) return;
                    var name = fsi.Name;
                    bool hit = wild ? FileSystemName.MatchesSimpleExpression(q, name, ignoreCase: true)
                                    : name.Contains(q, StringComparison.OrdinalIgnoreCase);
                    if (!hit) continue;
                    FileItem item;
                    try { item = FileItem.From(fsi); } catch { continue; }
                    var dir = Path.GetDirectoryName(fsi.FullName) ?? root;
                    var rel = Path.GetRelativePath(root, dir);
                    item.Location = rel == "." ? "" : rel;
                    batch.Add(item);
                    if (++total >= MaxResults) { capped = true; break; }
                    if (sw.ElapsedMilliseconds > 250)
                    {
                        var b = batch;
                        batch = new List<FileItem>();
                        sw.Restart();
                        Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) AddResults(b, false, false); });
                    }
                }
            }
            catch { /* folder vanished or became unreadable: show what was found */ }
            var last = batch;
            Dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) AddResults(last, true, capped); });
        }, token);
    }

    private void AddResults(List<FileItem> found, bool done, bool capped)
    {
        if (_searchRoot == null || _items == null) return;
        _all.AddRange(found);
        foreach (var it in found) _items.Add(it);
        if (done)
        {
            _searching = false;
            _searchCapped = capped;
            // Sort the finished list, keeping the cursor and marks.
            bool hadFocus = FileList.IsKeyboardFocusWithin;
            var cursorName = CursorItem?.FullPath;
            var marked = FileList.SelectedItems.Cast<FileItem>().Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ApplySort();
            foreach (var it in FileList.Items.Cast<FileItem>())
                if (marked.Contains(it.FullPath)) FileList.SelectedItems.Add(it);
            if (FileList.SelectedItems.Count == 0 && FileList.Items.Count > 0) FileList.SelectedIndex = 0;
            if (hadFocus)
            {
                var match = FileList.Items.Cast<FileItem>().FirstOrDefault(i => string.Equals(i.FullPath, cursorName, StringComparison.OrdinalIgnoreCase));
                FocusList(match?.Name);
            }
        }
        else if (FileList.SelectedIndex < 0 && FileList.Items.Count > 0) FileList.SelectedIndex = 0;
        UpdateStatus();
    }

    /// <summary>Shows the folder a search result is in, with the result selected (Ctrl+Enter).</summary>
    public void GoToItemLocation(FileItem it)
    {
        if (it.IsParent || it.IsVirtual) return;
        var folder = Path.GetDirectoryName(it.FullPath);
        if (string.IsNullOrEmpty(folder)) return;
        CancelSearch();
        SearchBox.Text = "";
        if (Navigate(folder, true, it.Name)) FocusList(it.Name);
    }

    // ───────────────────────── event handlers ─────────────────────────

    private void Drive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DriveEntry drive) return;
        Activated?.Invoke(this, EventArgs.Empty);

        bool isDrive = drive.Root.Length <= 3 && drive.Root.Length >= 2 && drive.Root[1] == ':';
        if (isDrive)
        {
            if (!drive.IsCurrent)
            {
                bool ready = true;
                try { ready = new DriveInfo(drive.Root).IsReady; } catch { /* let Navigate report it */ }
                if (!ready)
                {
                    IosDialog.Alert(Window.GetWindow(this), Loc.F("ErrDriveNotReady", drive.Label), Loc.T("ErrDriveNotReadyMsg"));
                    return;
                }
                Navigate(drive.Root);
            }
        }
        else if (!ShellOps.SamePath(CurrentPath, drive.Root) || NetworkOps.IsNetRoot(drive.Root) && !NetworkOps.IsNetRoot(CurrentPath))
        {
            Navigate(drive.Root);
        }
        FocusList();
    }

    private void Back_Click(object sender, RoutedEventArgs e) { GoBack(); FocusList(); }
    private void Up_Click(object sender, RoutedEventArgs e) { GoUp(); FocusList(); }

    private void PathBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Navigate(PathBox.Text)) FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PathBox.Text = CurrentPath;
            FocusList();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        if (_deep && CanDeepSearch)
        {
            _searchTimer.Stop();
            if (q.Length == 0) EndSearch(); else _searchTimer.Start();
            return;
        }
        if (_searchRoot != null) EndSearch();
        _view?.Refresh();
        UpdateStatus();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; FocusList(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Enter)
        {
            if (_searchTimer.IsEnabled) { _searchTimer.Stop(); RunSearchNow(); }
            if (FileList.Items.Count > 1 && FileList.SelectedItem is FileItem { IsParent: true })
                FileList.SelectedIndex = 1;
            FocusList();
            e.Handled = true;
        }
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        if (_sortKey == key) _sortAsc = !_sortAsc;
        else { _sortKey = key; _sortAsc = key is "Name" or "Ext"; }

        var marked = FileList.SelectedItems.Cast<FileItem>().Select(i => i.Name).ToHashSet();
        ApplySort();
        foreach (var it in FileList.Items.Cast<FileItem>())
            if (marked.Contains(it.Name)) FileList.SelectedItems.Add(it);
        FocusList();
    }

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Enter && mods == ModifierKeys.None)
        {
            if (CursorItem is { } it) Open(it);
            e.Handled = true;
        }
        else if (key == Key.Enter && mods == ModifierKeys.Control)
        {
            // In search results: go to where the file is. Otherwise: open the folder in a new tab.
            CommandRequested?.Invoke(this, _searchRoot != null ? "locate" : "opentab");
            e.Handled = true;
        }
        else if (key == Key.Escape && _searchRoot != null && FileList.SelectedItems.Count <= 1)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
        else if (key == Key.Enter && mods == ModifierKeys.Alt)
        {
            CommandRequested?.Invoke(this, "properties");
            e.Handled = true;
        }
        else if (key == Key.Back && mods == ModifierKeys.None)
        {
            if (_searchRoot != null) SearchBox.Text = "";   // leave the search results
            else GoUp();
            e.Handled = true;
        }
        else if (key == Key.Left && mods == ModifierKeys.Alt)
        {
            GoBack();
            e.Handled = true;
        }
        else if (key == Key.Insert && mods == ModifierKeys.None)
        {
            ToggleMarkAndAdvance();
            e.Handled = true;
        }
        else if (key == Key.Escape && FileList.SelectedItems.Count > 1)
        {
            SelectNoItems();
            e.Handled = true;
        }
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d &&
            ItemsControl.ContainerFromElement(FileList, d) is ListBoxItem { DataContext: FileItem it })
            Open(it);
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        CursorChanged?.Invoke(this);
    }

    private void Ctx_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string cmd }) CommandRequested?.Invoke(this, cmd);
    }

    // Drag & drop: between panels, to/from Explorer and the desktop.
    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = e.OriginalSource is DependencyObject d &&
                     ItemsControl.ContainerFromElement(FileList, d) is ListBoxItem { DataContext: FileItem { IsParent: false, IsVirtual: false } };
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragArmed = false;
        if (InArchive) return;     // items inside a ZIP have no real path to drag
        var paths = GetSelection().Select(i => i.FullPath).ToArray();
        if (paths.Length == 0) return;
        DragDrop.DoDragDrop(FileList, new DataObject(DataFormats.FileDrop, paths),
                            DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.None
            : (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void FileList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        string dest = CurrentPath;
        if (e.OriginalSource is DependencyObject d &&
            ItemsControl.ContainerFromElement(FileList, d) is ListBoxItem { DataContext: FileItem { IsDir: true, IsParent: false } f })
            dest = f.FullPath;
        if (NetworkOps.IsVirtualPath(dest)) return;
        bool move = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        // Defer so the drag source isn't blocked while the shell copies.
        Dispatcher.InvokeAsync(() => FilesDropped?.Invoke(this, paths, dest, move));
        e.Handled = true;
    }
}
