using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClaudiusFileManager;

public partial class MainWindow : Window
{
    private FilePanel _active = null!;
    private FilePanel Other => _active == LeftPanel ? RightPanel : LeftPanel;
    private Settings S => App.Settings;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        _active = LeftPanel;

        foreach (var p in new[] { LeftPanel, RightPanel })
        {
            p.Activated += (s, _) => SetActive((FilePanel)s!);
            p.CommandRequested += (panel, cmd) => { SetActive(panel); Execute(cmd); };
            p.FilesDropped += OnFilesDropped;
            p.Navigated += panel => { if (panel == _active) Nav.Highlight(panel.CurrentPath); };
            p.CursorChanged += panel => { if (panel == _active) UpdatePreview(); };
        }

        // Navigation pane: open the clicked place in the active panel
        Nav.NavigateRequested += path =>
        {
            _active.Navigate(path);
            _active.FocusList();
        };
        // Pick up anything pinned or unpinned in Explorer while we were in the background
        Activated += (_, _) => Nav.RequestRefresh();

        Width = S.Width;
        Height = S.Height;
        if (S.Maximized) WindowState = WindowState.Maximized;

        var v = typeof(App).Assembly.GetName().Version;
        AboutVersion.Text = v == null ? "" : Loc.F("Version", v.ToString(3));
        UpdateLanguageButton();
        Loc.Changed += () =>
        {
            UpdateLanguageButton();
            if (v != null) AboutVersion.Text = Loc.F("Version", v.ToString(3));
        };

        Loaded += OnLoaded;
        Task.Run(ZipOps.CleanTemp);   // temporary copies of files opened from ZIPs
        Closing += (_, _) =>
        {
            SaveSettings();
            // The preview must never stop the main window from closing.
            try { _preview?.CloseForReal(); } catch { /* ignore */ }
        };
        StateChanged += (_, _) => UpdateMaxState();
    }

    // ───────────────────────── window plumbing ─────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11 rounded window corners (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2)
        var hwnd = new WindowInteropHelper(this).Handle;
        int pref = 2;
        try { DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int)); } catch { /* Windows 10 */ }
        ThemeManager.ApplyTitleBar(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        static string Pick(string? saved, string fallback) =>
            !string.IsNullOrEmpty(saved) && Directory.Exists(saved) ? saved : fallback;

        LeftPanel.ShowHidden = RightPanel.ShowHidden = S.ShowHidden;
        HiddenSwitch.IsChecked = S.ShowHidden;
        LeftPanel.ShowFolderSizes = RightPanel.ShowFolderSizes = S.ShowFolderSizes;
        FolderSizeSwitch.IsChecked = S.ShowFolderSizes;
        NavPaneSwitch.IsChecked = S.ShowNavPane;
        PreviewSwitch.IsChecked = S.AutoPreview;
        ApplyNavPane(S.ShowNavPane);

        switch (ThemeManager.Mode)
        {
            case ThemeMode.Light: ThemeLight.IsChecked = true; break;
            case ThemeMode.Dark: ThemeDark.IsChecked = true; break;
            default: ThemeSystem.IsChecked = true; break;
        }

        // A folder passed on the command line (e.g. from Explorer's "Open in Claudius File Manager") opens on the left.
        var args = Environment.GetCommandLineArgs().Skip(1).Select(a => a.Trim('"')).ToList();
        string? startDir = args.FirstOrDefault(a => Directory.Exists(a))
                           ?? args.Where(a => File.Exists(a)).Select(a => Path.GetDirectoryName(a)).FirstOrDefault();

        LeftPanel.Navigate(startDir ?? Pick(S.Left, home), false);
        RightPanel.Navigate(Pick(S.Right, Directory.Exists(downloads) ? downloads : home), false);

        SetActive(LeftPanel);
        LeftPanel.FocusList();
        UpdateMaxState();
        _ready = true;
    }

    private void SaveSettings()
    {
        S.Left = LeftPanel.CurrentPath;
        S.Right = RightPanel.CurrentPath;
        S.ShowHidden = HiddenSwitch.IsChecked == true;
        S.ShowFolderSizes = FolderSizeSwitch.IsChecked == true;
        S.ShowNavPane = NavPaneSwitch.IsChecked == true;
        S.AutoPreview = PreviewSwitch.IsChecked == true;
        _preview?.SaveGeometry();
        if (S.ShowNavPane && NavColumn.ActualWidth > 0) S.NavPaneWidth = NavColumn.ActualWidth;
        S.Maximized = WindowState == WindowState.Maximized;
        S.Theme = ThemeManager.Mode.ToString();
        S.Language = Loc.Current;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.Width > 0) { S.Width = bounds.Width; S.Height = bounds.Height; }
        S.Save();
    }

    private void UpdateMaxState()
    {
        // A WindowChrome window overhangs the screen by the resize border when maximised.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetActive(FilePanel p)
    {
        _active = p;
        LeftPanel.SetActive(p == LeftPanel);
        RightPanel.SetActive(p == RightPanel);
        Nav.Highlight(p.CurrentPath);
        UpdatePreview();
    }

    // ───────────────────────── ribbon ─────────────────────────

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Fires once during InitializeComponent, before the panels exist.
        if (PanelHome == null || PanelSelect == null || PanelView == null || PanelTools == null || PanelAbout == null)
            return;
        PanelHome.Visibility = TabHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSelect.Visibility = TabSelect.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelView.Visibility = TabView.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelTools.Visibility = TabTools.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = TabAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_ready) _active.FocusList();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        var mode = ThemeManager.Parse(tag);
        if (mode != ThemeManager.Mode) ThemeManager.Apply(mode);
        if (_ready) { S.Theme = mode.ToString(); _active.FocusList(); }
    }

    private void UpdateLanguageButton() => LanguageButton.Content = Loc.NameOf(Loc.Current);

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LanguageButton, Placement = PlacementMode.Bottom };
        foreach (var (code, name) in Loc.Languages)
        {
            var item = new MenuItem { Header = name, IsCheckable = false, IsChecked = code == Loc.Current };
            item.Click += (_, _) =>
            {
                Loc.Apply(code);
                S.Language = code;
                _active.FocusList();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ───────────────────────── preview window (Quick View) ─────────────────────────

    private PreviewWindow? _preview;

    private void PreviewSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) { Loaded += (_, _) => PreviewSwitch_Changed(sender, e); return; }
        if (PreviewSwitch.IsChecked == true)
        {
            if (_preview == null)
            {
                _preview = new PreviewWindow { Owner = this };
                _preview.UserClosed += () => PreviewSwitch.IsChecked = false;
                _preview.PlaceNear(this);
            }
            if (!_preview.IsVisible) _preview.Show();
            UpdatePreview();
        }
        else _preview?.Park();
    }

    private void UpdatePreview()
    {
        if (_preview is { IsVisible: true } && _active != null) _preview.ShowItem(_active.CursorItem);
    }

    private void NavPaneSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (Nav == null) return;
        bool show = NavPaneSwitch.IsChecked == true;
        if (!show && NavColumn.ActualWidth > 0) S.NavPaneWidth = NavColumn.ActualWidth;
        ApplyNavPane(show);
    }

    private void ApplyNavPane(bool show)
    {
        Nav.Visibility = NavSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NavColumn.MinWidth = show ? 150 : 0;
        NavColumn.Width = new GridLength(show ? Math.Clamp(S.NavPaneWidth, 150, 480) : 0);
        NavSplitColumn.Width = new GridLength(show ? 10 : 0);
    }

    private void FolderSizeSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (LeftPanel == null || RightPanel == null) return;
        LeftPanel.ShowFolderSizes = RightPanel.ShowFolderSizes = FolderSizeSwitch.IsChecked == true;
        if (!_ready) return;
        LeftPanel.Refresh();
        RightPanel.Refresh();
    }

    private void HiddenSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (LeftPanel == null || RightPanel == null) return;
        LeftPanel.ShowHidden = RightPanel.ShowHidden = HiddenSwitch.IsChecked == true;
        if (!_ready) return;
        LeftPanel.Refresh();
        RightPanel.Refresh();
    }

    private void Cmd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cmd }) Execute(cmd);
        if (IsLoaded) _active.FocusList();
    }

    // ───────────────────────── keyboard (Total Commander layout) ─────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        bool inText = Keyboard.FocusedElement is TextBox;
        const ModifierKeys None = ModifierKeys.None, Shift = ModifierKeys.Shift, Ctrl = ModifierKeys.Control;
        string? cmd = null;

        switch (key)
        {
            case Key.Tab when m is None or Shift:
                SetActive(Other);
                _active.FocusList();
                e.Handled = true;
                return;
            case Key.F1: cmd = "help"; break;
            case Key.F2 when m == None: cmd = "rename"; break;
            case Key.F3 when m == None: cmd = "view"; break;
            case Key.F4 when m == None: cmd = "edit"; break;
            case Key.F4 when m == Shift: cmd = "newfile"; break;
            case Key.F5 when m == None: cmd = "copy"; break;
            case Key.F5 when m == ModifierKeys.Alt: cmd = "pack"; break;
            case Key.F9 when m == ModifierKeys.Alt: cmd = "unpack"; break;
            case Key.F6 when m == Shift: cmd = "rename"; break;
            case Key.F6 when m == None: cmd = "move"; break;
            case Key.F7 when m == None: cmd = "mkdir"; break;
            case Key.F8 when m == None: cmd = "delete"; break;
            case Key.F8 when m == Shift: cmd = "deletePerm"; break;
            case Key.Delete when !inText && m == None: cmd = "delete"; break;
            case Key.Delete when !inText && m == Shift: cmd = "deletePerm"; break;
            case Key.R when m == Ctrl: cmd = "refresh"; break;
            case Key.H when m == Ctrl: cmd = "hidden"; break;
            case Key.Q when m == Ctrl: cmd = "preview"; break;
            case Key.U when m == Ctrl: cmd = "swap"; break;
            case Key.OemPlus when m == Ctrl: cmd = "samepath"; break;
            case Key.T when m == Ctrl: cmd = "terminal"; break;
            case Key.E when m == Ctrl: cmd = "explorer"; break;
            case Key.C when !inText && m == Ctrl: cmd = "clipcopy"; break;
            case Key.C when !inText && m == (Ctrl | Shift): cmd = "copypath"; break;
            case Key.X when !inText && m == Ctrl: cmd = "clipcut"; break;
            case Key.V when !inText && m == Ctrl: cmd = "paste"; break;
            case Key.F when m == Ctrl:
                _active.FocusSearch(); e.Handled = true; return;
            case Key.L when m == Ctrl:
                _active.FocusPath(); e.Handled = true; return;
            case Key.D when m == ModifierKeys.Alt:
                _active.FocusPath(); e.Handled = true; return;
        }

        if (cmd != null)
        {
            e.Handled = true;
            Execute(cmd);
        }
    }

    // ───────────────────────── commands ─────────────────────────

    private void Execute(string cmd)
    {
        var p = _active;
        try
        {
            switch (cmd)
            {
                case "open": if (p.CursorItem is { } it) p.Open(it); break;
                case "view": View(p); break;
                case "edit": Edit(p); break;
                case "copy": Transfer(p, Other, move: false); break;
                case "pack": Pack(p); break;
                case "unpack": Unpack(p); break;
                case "move": Transfer(p, Other, move: true); break;
                case "mkdir": NewFolder(p); break;
                case "newfile": NewFile(p); break;
                case "rename": Rename(p); break;
                case "delete": Delete(p, permanent: false); break;
                case "deletePerm": Delete(p, permanent: true); break;
                case "refresh": p.LoadDrives(); p.Refresh(); break;
                case "preview": PreviewSwitch.IsChecked = PreviewSwitch.IsChecked != true; break;
                case "hidden": HiddenSwitch.IsChecked = HiddenSwitch.IsChecked != true; break;
                case "selall": p.SelectAllItems(); break;
                case "selnone": p.SelectNoItems(); break;
                case "selinv": p.InvertSelection(); break;
                case "swap":
                    var a = LeftPanel.CurrentPath;
                    LeftPanel.Navigate(RightPanel.CurrentPath);
                    RightPanel.Navigate(a);
                    break;
                case "samepath": Other.Navigate(p.CurrentPath); break;
                case "terminal": OpenTerminal(RealFolder(p)); break;
                case "explorer": ShowInExplorer(p); break;
                case "properties":
                    var target = p.CursorItem is { IsParent: false, IsInArchive: false } ci ? ci.FullPath : p.CurrentPath;
                    if (ZipOps.TrySplit(target, out var propZip, out _)) target = propZip;
                    ShellOps.ShowProperties(this, target);
                    break;
                case "foldersize": if (!p.InArchive) FolderSizeOf(p); break;
                case "network": p.Navigate(NetworkOps.NetRoot); break;
                case "mapdrive":
                    if (NetworkOps.MapDriveDialog(new WindowInteropHelper(this).Handle)) DrivesChanged();
                    break;
                case "unmapdrive":
                    if (NetworkOps.DisconnectDriveDialog(new WindowInteropHelper(this).Handle)) DrivesChanged();
                    break;
                case "clipcopy": ClipboardSet(p, cut: false); break;
                case "clipcut": ClipboardSet(p, cut: true); break;
                case "paste": Paste(p); break;
                case "copypath":
                    var sel = p.GetSelection();
                    Clipboard.SetText(sel.Count > 0 ? string.Join(Environment.NewLine, sel.Select(i => i.FullPath)) : p.CurrentPath);
                    break;
                case "about": TabAbout.IsChecked = true; break;
                case "license": IosDialog.Info(this, Loc.T("License"), Loc.T("LicenseText")); break;
                case "help": IosDialog.Info(this, Loc.T("ShortcutsTitle"), Loc.T("ShortcutsText")); break;
                case "quit": Close(); break;
            }
        }
        catch (Exception ex)
        {
            IosDialog.Alert(this, Loc.T("ErrGeneric"), ex.Message);
        }
    }

    private static string Describe(List<FileItem> items) =>
        items.Count == 1 ? $"“{items[0].Name}”" : Loc.F("NItems", items.Count);

    private void Transfer(FilePanel src, FilePanel dst, bool move)
    {
        var items = src.GetSelection();
        if (items.Count == 0) return;

        var target = IosDialog.Prompt(this, Loc.F(move ? "MoveTitle" : "CopyTitle", Describe(items)), Loc.T("ToFolder"),
                                      dst.CurrentPath, Loc.T(move ? "Move" : "Copy"));
        if (string.IsNullOrWhiteSpace(target)) return;
        target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        target = Path.GetFullPath(Path.Combine(RealFolder(src), target));

        bool toZip = ZipOps.TrySplit(target, out var zip, out var inner);
        if (src.InArchive)
        {
            // Copying out of a ZIP = unpacking the selected items.
            if (toZip) { IosDialog.Alert(this, Loc.T("ErrGeneric"), Loc.T("ErrArchiveToArchive")); return; }
            if (ExtractItems(items, target) && move)
                ProgressWindow.Run(this, Loc.T("Deleting"), (_, ct) => ZipOps.Delete(items[0].Archive!, items.Select(i => i.InnerPath).ToList(), ct));
        }
        else if (toZip)
        {
            var paths = items.Select(i => i.FullPath).ToList();
            if (AddToArchive(zip, inner, paths) && move) ShellOps.Delete(this, paths, permanent: false);
        }
        else
        {
            var paths = items.Select(i => i.FullPath).ToList();
            if (move) ShellOps.Move(this, paths, target); else ShellOps.Copy(this, paths, target);
        }
        src.Refresh();
        dst.Refresh();
    }

    /// <summary>The panel's folder on disk (for a panel inside a ZIP: the folder holding the ZIP).</summary>
    private static string RealFolder(FilePanel p) =>
        ZipOps.TrySplit(p.CurrentPath, out var zip, out _) ? Path.GetDirectoryName(zip)! : p.CurrentPath;

    // ───────────────────────── ZIP ─────────────────────────

    private bool NotInArchive(FilePanel p)
    {
        if (!p.InArchive) return true;
        IosDialog.Alert(this, Loc.T("ArchiveReadOnly"), Loc.T("ArchiveReadOnlyMsg"));
        return false;
    }

    /// <summary>Alt+F5: packs the selected files and folders into a ZIP archive.</summary>
    private void Pack(FilePanel p)
    {
        if (!NotInArchive(p)) return;
        var items = p.GetSelection();
        if (items.Count == 0) return;

        string baseName = items.Count == 1
            ? (items[0].IsDir ? items[0].Name : Path.GetFileNameWithoutExtension(items[0].Name))
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(p.CurrentPath));
        if (string.IsNullOrEmpty(baseName)) baseName = "Archive";
        var other = Other;
        string folder = !other.InArchive && !NetworkOps.IsVirtualPath(other.CurrentPath) && Directory.Exists(other.CurrentPath)
            ? other.CurrentPath : p.CurrentPath;
        var suggestion = Path.Combine(folder, baseName + ".zip");

        var answer = IosDialog.Prompt(this, Loc.F("PackTitle", Describe(items)), Loc.T("PackMsg"), suggestion,
                                      Loc.T("Pack"), suggestion.Length - 4)?.Trim().Trim('"');
        if (string.IsNullOrEmpty(answer)) return;
        var zipPath = Path.GetFullPath(Path.Combine(p.CurrentPath, Environment.ExpandEnvironmentVariables(answer)));
        if (!ZipOps.IsZipName(zipPath)) zipPath += ".zip";
        var sources = items.Select(i => i.FullPath).ToList();
        if (sources.Any(s => ShellOps.SamePath(s, zipPath))) return;

        bool add = false;
        if (File.Exists(zipPath))
        {
            int c = IosDialog.Choice(this, Loc.F("ZipExistsTitle", Path.GetFileName(zipPath)), Loc.T("ZipExistsMsg"),
                                     Loc.T("AddToArchive"), Loc.T("Replace"));
            if (c < 0) return;
            add = c == 0;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        bool done = ProgressWindow.Run(this, Loc.T("Packing"), (progress, ct) =>
        {
            if (add) ZipOps.Add(zipPath, "", sources, progress, ct);
            else ZipOps.Create(zipPath, sources, progress, ct);
        });

        LeftPanel.Refresh();
        RightPanel.Refresh();
        if (done)
            foreach (var panel in new[] { other, p })
                if (ShellOps.SamePath(panel.CurrentPath, Path.GetDirectoryName(zipPath)))
                {
                    panel.SelectByName(Path.GetFileName(zipPath));
                    if (panel == _active) panel.FocusList(Path.GetFileName(zipPath));
                    break;
                }
    }

    /// <summary>Alt+F9: unpacks the selected ZIP files (or, inside an archive, the selected items).</summary>
    private void Unpack(FilePanel p)
    {
        var items = p.GetSelection();
        var other = Other;
        string suggestion = !other.InArchive && !NetworkOps.IsVirtualPath(other.CurrentPath) ? other.CurrentPath : RealFolder(p);

        if (p.InArchive)
        {
            if (items.Count == 0) return;
            var to = AskFolder(Loc.F("UnpackTitle", Describe(items)), suggestion);
            if (to == null) return;
            ExtractItems(items, to);
            other.Refresh();
            return;
        }

        var zips = items.Where(i => !i.IsDir && ZipOps.IsZipName(i.Name)).ToList();
        if (zips.Count == 0)
        {
            IosDialog.Alert(this, Loc.T("NoZipSelected"), Loc.T("NoZipSelectedMsg"));
            return;
        }
        var dest = AskFolder(Loc.F("UnpackTitle", Describe(zips)), suggestion);
        if (dest == null) return;

        // Work out every file first, so the user is asked about existing files only once.
        var jobs = new List<(string Zip, List<(string Entry, string Target, bool IsDir, long Size)> Plan)>();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            foreach (var z in zips)
            {
                try { jobs.Add((z.FullPath, ZipOps.Plan(z.FullPath, new[] { "" }, ZipOps.TargetFor(z.FullPath, dest)))); }
                catch (InvalidDataException) { throw new IOException(Loc.F("ErrBadZip", z.Name)); }
            }
        }
        finally { Mouse.OverrideCursor = null; }

        bool? overwrite = AskOverwrite(jobs.SelectMany(j => j.Plan));
        if (overwrite == null) return;
        ProgressWindow.Run(this, Loc.T("Unpacking"), (progress, ct) =>
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                int index = i;
                ZipOps.Extract(jobs[i].Zip, jobs[i].Plan, overwrite.Value,
                               (f, n) => progress((index + f) / jobs.Count, n), ct);
            }
        });
        LeftPanel.Refresh();
        RightPanel.Refresh();
    }

    private string? AskFolder(string title, string suggestion)
    {
        var answer = IosDialog.Prompt(this, title, Loc.T("ToFolder"), suggestion, Loc.T("Unpack"))?.Trim().Trim('"');
        if (string.IsNullOrEmpty(answer)) return null;
        var full = Path.GetFullPath(Path.Combine(RealFolder(_active), Environment.ExpandEnvironmentVariables(answer)));
        if (ZipOps.IsInside(full)) { IosDialog.Alert(this, Loc.T("ErrGeneric"), Loc.T("ErrArchiveToArchive")); return null; }
        return full;
    }

    /// <summary>null = cancelled, true = replace existing files, false = keep them.</summary>
    private bool? AskOverwrite(IEnumerable<(string Entry, string Target, bool IsDir, long Size)> plan)
    {
        int existing = plan.Count(x => !x.IsDir && File.Exists(x.Target));
        if (existing == 0) return true;
        int c = IosDialog.Choice(this, Loc.F("ConflictTitle", existing), Loc.T("ConflictMsg"),
                                 Loc.T("Replace"), Loc.T("Skip"));
        return c < 0 ? null : c == 0;
    }

    /// <summary>Unpacks items shown inside an archive into a folder. Returns false if cancelled.</summary>
    private bool ExtractItems(List<FileItem> items, string destDir)
    {
        var zip = items[0].Archive!;
        var plan = ZipOps.Plan(zip, items.Select(i => i.InnerPath).ToList(), destDir);
        var overwrite = AskOverwrite(plan);
        if (overwrite == null) return false;
        Directory.CreateDirectory(destDir);
        return ProgressWindow.Run(this, Loc.T("Unpacking"), (progress, ct) => ZipOps.Extract(zip, plan, overwrite.Value, progress, ct));
    }

    /// <summary>Copies files into a ZIP (the target panel or folder is inside an archive).</summary>
    private bool AddToArchive(string zip, string inner, List<string> paths)
    {
        if (paths.Count == 0) return false;
        var what = paths.Count == 1 ? $"“{Path.GetFileName(paths[0])}”" : Loc.F("NItems", paths.Count);
        if (!IosDialog.Confirm(this, Loc.F("AddToArchiveTitle", what, Path.GetFileName(zip)), "", Loc.T("AddToArchive")))
            return false;
        bool ok = ProgressWindow.Run(this, Loc.T("Packing"), (progress, ct) => ZipOps.Add(zip, inner, paths, progress, ct));
        LeftPanel.Refresh();
        RightPanel.Refresh();
        return ok;
    }

    private void OnFilesDropped(FilePanel panel, string[] paths, string dest, bool move)
    {
        // Dropped back onto the folder they came from, or a folder onto itself: nothing to do.
        if (paths.All(p => ShellOps.SamePath(Path.GetDirectoryName(p), dest))) return;
        if (paths.Any(p => ShellOps.SamePath(p, dest))) return;
        SetActive(panel);
        try
        {
            if (ZipOps.TrySplit(dest, out var zip, out var inner))
            {
                if (AddToArchive(zip, inner, paths.ToList()) && move) ShellOps.Delete(this, paths, permanent: false);
            }
            else if (move) ShellOps.Move(this, paths, dest); else ShellOps.Copy(this, paths, dest);
        }
        catch (Exception ex) { IosDialog.Alert(this, Loc.T("ErrGeneric"), ex.Message); }
        LeftPanel.Refresh();
        RightPanel.Refresh();
    }

    private void Delete(FilePanel p, bool permanent)
    {
        var items = p.GetSelection();
        if (items.Count == 0) return;
        if (p.InArchive)
        {
            if (!IosDialog.Confirm(this, Loc.F("DeleteFromArchiveTitle", Describe(items)), Loc.T("DeleteFromArchiveMsg"),
                                   Loc.T("Delete"), destructive: true)) return;
            ProgressWindow.Run(this, Loc.T("Deleting"), (_, ct) => ZipOps.Delete(items[0].Archive!, items.Select(i => i.InnerPath).ToList(), ct));
            p.Refresh();
            return;
        }
        bool ok = IosDialog.Confirm(this,
            Loc.F(permanent ? "DeletePermTitle" : "DeleteRecycleTitle", Describe(items)),
            Loc.T(permanent ? "DeletePermMsg" : "DeleteRecycleMsg"),
            Loc.T("Delete"), destructive: true);
        if (!ok) return;
        ShellOps.Delete(this, items.Select(i => i.FullPath), permanent);
        p.Refresh();
    }

    private bool ValidName(string name, bool allowSubfolders)
    {
        char[] bad = allowSubfolders ? new[] { ':', '*', '?', '"', '<', '>', '|' } : Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(bad) < 0 && name.IndexOfAny(Path.GetInvalidPathChars()) < 0) return true;
        IosDialog.Alert(this, Loc.T("ErrInvalidName"), Loc.T("ErrInvalidNameMsg"));
        return false;
    }

    /// <summary>After mapping or disconnecting a network drive.</summary>
    private void DrivesChanged()
    {
        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            panel.LoadDrives();
            if (NetworkOps.IsVirtualPath(panel.CurrentPath) || !Directory.Exists(panel.CurrentPath)) panel.Refresh();
        }
    }

    private void Rename(FilePanel p)
    {
        var it = p.CursorItem;
        if (it is null || it.IsParent || it.IsVirtual || !NotInArchive(p)) return;
        int selLen = !it.IsDir && it.Ext.Length > 0 ? it.Name.Length - it.Ext.Length - 1 : -1;
        var name = IosDialog.Prompt(this, Loc.T("RenameTitle"), "", it.Name, Loc.T("Rename"), selLen)?.Trim();
        if (string.IsNullOrEmpty(name) || name == it.Name || !ValidName(name, false)) return;

        var dest = Path.Combine(Path.GetDirectoryName(it.FullPath)!, name);
        if (it.IsDir)
        {
            if (string.Equals(it.FullPath, dest, StringComparison.OrdinalIgnoreCase))
            {
                // Case-only rename of a folder needs a hop through a temporary name.
                var tmp = it.FullPath + ".~" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(it.FullPath, tmp);
                Directory.Move(tmp, dest);
            }
            else Directory.Move(it.FullPath, dest);
        }
        else File.Move(it.FullPath, dest);

        p.Refresh();
        p.SelectByName(name);
        p.FocusList(name);
    }

    private void NewFolder(FilePanel p)
    {
        if (!NotInArchive(p)) return;
        var name = IosDialog.Prompt(this, Loc.T("NewFolderTitle"), Loc.T("NewFolderMsg"),
                                    Loc.T("NewFolderDefault"), Loc.T("Create"))?.Trim();
        if (string.IsNullOrEmpty(name) || !ValidName(name, true)) return;
        Directory.CreateDirectory(Path.Combine(p.CurrentPath, name));
        var first = name.Split('\\', '/')[0];
        p.Refresh();
        p.SelectByName(first);
        p.FocusList(first);
    }

    private void NewFile(FilePanel p)
    {
        if (!NotInArchive(p)) return;
        var baseName = Loc.T("NewFileDefault");
        var name = IosDialog.Prompt(this, Loc.T("NewFileTitle"), Loc.T("NewFileMsg"), baseName + ".txt",
                                    Loc.T("Create"), baseName.Length)?.Trim();
        if (string.IsNullOrEmpty(name) || !ValidName(name, false)) return;
        var path = Path.Combine(p.CurrentPath, name);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        p.Refresh();
        p.SelectByName(name);
        p.FocusList(name);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void View(FilePanel p)
    {
        var it = p.CursorItem;
        if (it is null || it.IsParent || it.IsVirtual) return;
        if (it.IsDir) { if (!it.IsInArchive) FolderSizeOf(p); return; }   // like Total Commander: F3 on a folder = its size
        try
        {
            var path = it.IsInArchive ? ZipOps.ExtractToTemp(it.Archive!, it.InnerPath) : it.FullPath;
            new ViewerWindow(path) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            IosDialog.Alert(this, Loc.T("ErrCantView"), ex.Message);
        }
    }

    private async void FolderSizeOf(FilePanel p)
    {
        var it = p.CursorItem;
        string path = it is { IsDir: true, IsParent: false } ? it.FullPath : p.CurrentPath;
        string title = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(title)) title = path;

        Mouse.OverrideCursor = Cursors.Wait;
        (long bytes, int files, int dirs) r;
        try { r = await Task.Run(() => FolderSize(path)); }
        catch (Exception ex) { IosDialog.Alert(this, Loc.T("ErrGeneric"), ex.Message); return; }
        finally { Mouse.OverrideCursor = null; }

        IosDialog.Alert(this, title, Loc.F("FolderSizeMsg", FileItem.FormatSize(r.bytes), r.bytes.ToString("N0"),
                                           r.files.ToString("N0"), r.dirs.ToString("N0")));
    }

    private static (long, int, int) FolderSize(string path) => FolderSizer.Measure(path);

    private void Edit(FilePanel p)
    {
        var it = p.CursorItem;
        if (it is null || it.IsDir) return;
        if (!NotInArchive(p)) return;
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{it.FullPath}\"") { UseShellExecute = true });
    }

    private static void OpenTerminal(string dir)
    {
        // A trailing backslash would escape the closing quote ("C:\" → C:\.)
        var arg = dir.EndsWith('\\') ? dir + "." : dir;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{arg}\"") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = dir, UseShellExecute = true });
        }
    }

    private static void ShowInExplorer(FilePanel p)
    {
        var it = p.CursorItem;
        var args = ZipOps.TrySplit(p.CurrentPath, out var zip, out _) ? $"/select,\"{zip}\""
                 : it is { IsParent: false } ? $"/select,\"{it.FullPath}\"" : $"\"{p.CurrentPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    // Clipboard interop with Explorer (Ctrl+C / Ctrl+X / Ctrl+V)
    private void ClipboardSet(FilePanel p, bool cut)
    {
        var sel = p.GetSelection();
        if (sel.Count == 0) return;
        string[] paths;
        if (p.InArchive)
        {
            // Unpack to a temporary folder, so the files can be pasted anywhere (Explorer, e-mail …).
            if (cut) { NotInArchive(p); return; }
            var tmp = Path.Combine(ZipOps.TempRoot, "clip-" + Guid.NewGuid().ToString("N")[..8]);
            var plan = ZipOps.Plan(sel[0].Archive!, sel.Select(i => i.InnerPath).ToList(), tmp);
            if (!ProgressWindow.Run(this, Loc.T("Unpacking"), (progress, ct) => ZipOps.Extract(sel[0].Archive!, plan, true, progress, ct)))
                return;
            paths = sel.Select(i => Path.Combine(tmp, i.Name)).ToArray();
        }
        else paths = sel.Select(i => i.FullPath).ToArray();
        var data = new DataObject(DataFormats.FileDrop, paths);
        // DROPEFFECT_MOVE = 2, DROPEFFECT_COPY|LINK = 5 (what Explorer uses)
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        Clipboard.SetDataObject(data, true);
    }

    private void Paste(FilePanel p)
    {
        if (!Clipboard.ContainsFileDropList()) return;
        var files = Clipboard.GetFileDropList().Cast<string>().ToList();
        if (files.Count == 0) return;

        bool move = false;
        try
        {
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms)
            {
                var b = new byte[4];
                if (ms.Read(b, 0, 4) == 4)
                {
                    int effect = BitConverter.ToInt32(b, 0);
                    move = (effect & 2) != 0 && (effect & 1) == 0;
                }
            }
        }
        catch { /* treat as copy */ }

        if (ZipOps.TrySplit(p.CurrentPath, out var zip, out var inner))
        {
            if (AddToArchive(zip, inner, files) && move)
            {
                ShellOps.Delete(this, files, permanent: false);
                Clipboard.Clear();
            }
        }
        else if (move)
        {
            if (ShellOps.Move(this, files, p.CurrentPath)) Clipboard.Clear();
        }
        else ShellOps.Copy(this, files, p.CurrentPath);
        LeftPanel.Refresh();
        RightPanel.Refresh();
    }
}
