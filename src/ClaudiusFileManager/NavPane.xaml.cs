using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudiusFileManager;

/// <summary>One row of the navigation pane.</summary>
public sealed class NavItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; init; } = "";
    /// <summary>Folder to open; null when the row only expands or collapses (This PC).</summary>
    public string? Target { get; init; }
    public ImageSource? Icon { get; init; }
    public string? Tooltip { get; init; }
    public double Indent { get; init; }
    public bool IsSeparator { get; init; }
    public bool Pinned { get; init; }
    public string Chevron { get; init; } = "";
    public Action? Toggle { get; init; }

    public bool IsClickable => Target != null || Toggle != null;
    public Thickness IndentMargin => new(Indent, 0, 0, 0);
    public Visibility LineVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowVisibility => IsSeparator ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;

    private bool _current;
    public bool IsCurrent
    {
        get => _current;
        set { if (_current != value) { _current = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent))); } }
    }
}

/// <summary>
/// A copy of Windows Explorer's navigation pane: Home, Gallery, OneDrive, the pinned Quick access folders,
/// other cloud drives (iCloud Drive …), This PC with its drives, and Network. It rebuilds itself when
/// folders are pinned/unpinned in Explorer, when drives come and go, and when the window is activated.
/// </summary>
public partial class NavPane : UserControl
{
    public event Action<string>? NavigateRequested;

    private readonly DispatcherTimer _driveTimer;
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _quickAccessWatcher;
    private string _driveSignature = "";
    private string _current = "";
    private bool _thisPcExpanded;
    private List<NavItem> _items = new();

    public NavPane()
    {
        InitializeComponent();
        _thisPcExpanded = App.Settings.NavThisPcExpanded;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rebuild(); };

        // Drives are cheap to check: rebuild only when the list of drives changes.
        _driveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _driveTimer.Tick += (_, _) => { if (DriveSignature() != _driveSignature) Rebuild(); };

        Loaded += (_, _) =>
        {
            Rebuild();
            _driveTimer.Start();
            WatchQuickAccess();
        };
        Unloaded += (_, _) =>
        {
            _driveTimer.Stop();
            _quickAccessWatcher?.Dispose();
        };
    }

    /// <summary>Rebuild shortly (several triggers in a row cause only one rebuild).</summary>
    public void RequestRefresh()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Highlights the row for the folder shown in the active panel.</summary>
    public void Highlight(string path)
    {
        _current = path;
        NavItem? match = null;
        foreach (var it in _items)
            if (match == null && it.Target != null && SamePlace(it.Target, path)) match = it;
        foreach (var it in _items) it.IsCurrent = ReferenceEquals(it, match);
    }

    private static bool SamePlace(string a, string b) =>
        NetworkOps.IsNetRoot(a) ? NetworkOps.IsNetRoot(b) : ShellOps.SamePath(a, b);

    private void WatchQuickAccess()
    {
        try
        {
            var dir = ShellNav.QuickAccessStoreFolder;
            if (!Directory.Exists(dir)) return;
            var w = new FileSystemWatcher(dir, ShellNav.QuickAccessStoreFile)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.InvokeAsync(RequestRefresh);
            w.Changed += h;
            w.Created += h;
            w.Renamed += (_, _) => Dispatcher.InvokeAsync(RequestRefresh);
            w.EnableRaisingEvents = true;
            _quickAccessWatcher = w;
        }
        catch { /* the window-activation refresh still keeps it current */ }
    }

    /// <summary>Network paths and network drives: never block the pane waiting for them.</summary>
    private static bool IsRemote(string path)
    {
        if (path.StartsWith(@"\\")) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return true; }
    }

    private static string DriveSignature()
    {
        try { return string.Join("|", DriveInfo.GetDrives().Select(d => d.Name + (int)d.DriveType)); }
        catch { return ""; }
    }

    private bool _building, _pending;

    /// <summary>
    /// Gathers everything on a background thread (pinned folders, icons and drives can be slow when a
    /// network drive is offline), then shows the result. The window never waits for the network.
    /// </summary>
    private void Rebuild()
    {
        if (_building) { _pending = true; return; }
        _building = true;
        bool expanded = _thisPcExpanded;
        string networkName = Loc.T("Network");

        var worker = new Thread(() =>
        {
            List<NavItem>? list = null;
            string signature = "";
            try
            {
                signature = DriveSignature();
                list = Build(expanded, networkName);
            }
            catch { /* keep what is shown */ }

            Dispatcher.InvokeAsync(() =>
            {
                _building = false;
                if (list != null)
                {
                    _driveSignature = signature;
                    _items = list;
                    Items.ItemsSource = list;
                    Highlight(_current);
                }
                if (_pending) { _pending = false; Rebuild(); }
            });
        }) { IsBackground = true, Name = "Navigation pane" };
        worker.SetApartmentState(ApartmentState.STA);   // the Windows shell needs an STA thread
        worker.Start();
    }

    private List<NavItem> Build(bool expanded, string networkName)
    {
        ShellNav.ClearCache();
        var list = new List<NavItem>();

        void Sep()
        {
            if (list.Count > 0 && !list[^1].IsSeparator) list.Add(new NavItem { IsSeparator = true });
        }
        void AddShell(string shellPath, string? target, string? fallbackName = null)
        {
            var (icon, name) = ShellNav.Info(shellPath);
            name ??= fallbackName;
            if (name == null) return;     // not on this version of Windows
            list.Add(new NavItem { Name = name, Icon = icon, Target = target, Tooltip = target });
        }
        void AddPath(string path, string? name = null, double indent = 0, bool pinned = false)
        {
            var (icon, displayName) = ShellNav.Info(path, offline: IsRemote(path));
            list.Add(new NavItem
            {
                Name = name ?? displayName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                Icon = icon, Target = path, Indent = indent, Pinned = pinned, Tooltip = path
            });
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clouds = CloudRoots();

        // ── Home, Gallery, OneDrive ──
        AddShell(ShellNav.HomeFolder, home);
        AddShell(ShellNav.GalleryFolder, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        // Personal OneDrive the way Explorer shows it ("Michal - Personal"), even if it registers itself differently
        if (!clouds.Any(c => c.IsOneDrive))
        {
            var oneDrivePath = Environment.GetEnvironmentVariable("OneDriveConsumer") ?? Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(oneDrivePath) && Directory.Exists(oneDrivePath))
            {
                var (odIcon, odName) = ShellNav.Info(ShellNav.OneDrivePersonal);
                var (pathIcon, _) = ShellNav.Info(oneDrivePath);
                list.Add(new NavItem { Name = odName ?? "OneDrive", Icon = odIcon ?? pathIcon, Target = oneDrivePath, Tooltip = oneDrivePath });
            }
        }
        foreach (var c in clouds.Where(c => c.IsOneDrive))
        {
            var (icon, _) = ShellNav.Info(c.ShellPath);
            list.Add(new NavItem { Name = c.Name, Icon = icon ?? ShellNav.Info(c.Path).Icon, Target = c.Path, Tooltip = c.Path });
        }

        // ── Pinned Quick access folders, in Explorer's order ──
        Sep();
        foreach (var (name, path) in ShellNav.PinnedFolders()) AddPath(path, name, pinned: true);

        // ── Other cloud drives (iCloud Drive, Dropbox, Google Drive …) ──
        Sep();
        foreach (var c in clouds.Where(c => !c.IsOneDrive))
        {
            var (icon, _) = ShellNav.Info(c.ShellPath);
            list.Add(new NavItem { Name = c.Name, Icon = icon ?? ShellNav.Info(c.Path).Icon, Target = c.Path, Tooltip = c.Path });
        }

        // ── This PC (expands to its drives), removable drives, Network ──
        var (pcIcon, pcName) = ShellNav.Info(ShellNav.ThisPc);
        list.Add(new NavItem
        {
            Name = pcName ?? "This PC", Icon = pcIcon,
            Chevron = expanded ? "" : "",
            Toggle = () =>
            {
                _thisPcExpanded = !_thisPcExpanded;
                App.Settings.NavThisPcExpanded = _thisPcExpanded;
                Rebuild();
            }
        });
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { drives = Array.Empty<DriveInfo>(); }
        if (expanded)
            foreach (var d in drives) AddPath(d.Name, indent: 16);
        foreach (var d in drives.Where(d => d.DriveType == DriveType.Removable)) AddPath(d.Name);
        AddShell(ShellNav.NetworkFolder, NetworkOps.NetRoot, networkName);
        return list;
    }

    private sealed record CloudRoot(string Name, string Path, string ShellPath, bool IsOneDrive, int Order);

    /// <summary>
    /// Cloud folders exactly as Explorer lists them: the entries that OneDrive, iCloud, Dropbox, Google Drive …
    /// register under HKCU\…\Explorer\Desktop\NameSpace with "pinned to navigation pane" set.
    /// Falls back to the folders found on disk when nothing is registered.
    /// </summary>
    private static List<CloudRoot> CloudRoots()
    {
        var result = new List<CloudRoot>();
        try
        {
            using var ns = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace");
            if (ns != null)
            {
                foreach (var clsid in ns.GetSubKeyNames())
                {
                    using var c = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{clsid}");
                    if (c == null || c.GetValue("System.IsPinnedToNameSpaceTree") is not int pinned || pinned == 0) continue;
                    using var bag = c.OpenSubKey(@"Instance\InitPropertyBag");
                    var target = bag?.GetValue("TargetFolderPath") as string;
                    if (string.IsNullOrEmpty(target)) continue;
                    target = Environment.ExpandEnvironmentVariables(target);
                    if (!Directory.Exists(target)) continue;
                    var name = c.GetValue("") as string;
                    using var nsKey = ns.OpenSubKey(clsid);
                    name ??= nsKey?.GetValue("") as string ?? Path.GetFileName(target);
                    int order = c.GetValue("SortOrderIndex") is int o ? o : 1000;
                    bool oneDrive = name.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                                    target.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                                    name.EndsWith("- Personal", StringComparison.OrdinalIgnoreCase);
                    if (!result.Any(r => ShellOps.SamePath(r.Path, target)))
                        result.Add(new CloudRoot(name, target, "shell:::" + clsid, oneDrive, order));
                }
            }
        }
        catch { /* ignore */ }

        if (result.Count == 0)
        {
            foreach (var (name, path) in NetworkOps.CloudFolders())
                result.Add(new CloudRoot(name, path, path, name.StartsWith("OneDrive"), 1000));
        }
        return result.OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NavItem item) return;
        if (item.Toggle != null) { item.Toggle(); return; }
        if (item.Target != null) NavigateRequested?.Invoke(item.Target);
    }
}
