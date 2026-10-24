using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudiusFileManager;

/// <summary>
/// Floating Quick View window: follows the selected file and previews it without opening it —
/// pictures, video and music (with a small player), PDF pages, text/code, and a Windows thumbnail
/// for everything else (Office documents, folders …).
/// </summary>
public partial class PreviewWindow : Window
{
    /// <summary>The user closed the window with its × button.</summary>
    public event Action? UserClosed;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    { "jpg", "jpeg", "jfif", "png", "gif", "bmp", "tif", "tiff", "ico", "webp", "heic", "heif", "jxr", "wdp", "dds" };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    { "mp4", "m4v", "mov", "wmv", "avi", "mkv", "webm", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts", "asf" };

    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
    { "mp3", "wav", "wma", "m4a", "aac", "flac", "ogg", "opus", "aif", "aiff", "mid", "midi" };

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "log", "ini", "cfg", "conf", "csv", "tsv", "json", "xml", "xaml", "yml", "yaml", "toml",
        "cs", "vb", "fs", "js", "ts", "jsx", "tsx", "py", "java", "c", "cpp", "h", "hpp", "go", "rs", "php", "rb",
        "sql", "html", "htm", "css", "scss", "bat", "cmd", "ps1", "sh", "reg", "inf", "csproj", "sln", "props",
        "nfo", "srt", "vtt", "gitignore", "editorconfig",
        // CNC programs
        "nc", "tap", "mpf", "spf", "gcode", "ngc", "cnc", "ptp", "eia", "iso", "min", "apt", "cls"
    };

    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _clock;
    private FileItem? _pending;
    private string? _shownPath;
    private int _request;
    private bool _playing, _seeking, _closingForReal;
    private PdfRender? _pdf;
    private uint _pdfPage;
    private int _rotation;          // degrees a phone video must be turned to stand upright
    private string _baseDetails = "";

    private static Settings S => App.Settings;

    public PreviewWindow()
    {
        InitializeComponent();

        // Wait a moment after the selection changes, so scrolling quickly through a list stays smooth.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Load(_pending); };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => UpdateClock();

        Media.Volume = Math.Clamp(S.PreviewVolume, 0, 1);
        Media.IsMuted = S.PreviewMuted;
        UpdateMuteGlyph();
        TitleText.Text = Loc.T("Preview");
    }

    // ───────────────────────── public API ─────────────────────────

    public void ShowItem(FileItem? item)
    {
        _pending = item;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Stops playback and hides the window (keeps it ready to show again).</summary>
    public void Park()
    {
        _debounce.Stop();
        Reset();
        _shownPath = null;
        Hide();
    }

    public void CloseForReal()
    {
        _closingForReal = true;
        Reset();
        Close();
    }

    public void PlaceNear(Window owner)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (S.PreviewWidth >= MinWidth && S.PreviewHeight >= MinHeight &&
            vs.Contains(new Point(S.PreviewLeft + 40, S.PreviewTop + 20)))
        {
            Left = S.PreviewLeft; Top = S.PreviewTop; Width = S.PreviewWidth; Height = S.PreviewHeight;
            // Keep it fully on screen (above the taskbar) if it was left hanging over an edge.
            var wa = SystemParameters.WorkArea;
            if (Top + Height > wa.Bottom && vs.Bottom <= wa.Bottom + 100) Top = Math.Max(wa.Top, wa.Bottom - Height);
            if (Left + Width > vs.Right) Left = Math.Max(vs.Left, vs.Right - Width);
            return;
        }
        var ownerRect = owner.WindowState == WindowState.Maximized
            ? SystemParameters.WorkArea
            : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        Left = Math.Max(vs.Left, ownerRect.Right - Width - 40);
        Top = Math.Max(vs.Top, ownerRect.Bottom - Height - 90);
    }

    public void SaveGeometry()
    {
        if (WindowState != WindowState.Normal || ActualWidth <= 0) return;
        S.PreviewLeft = Left; S.PreviewTop = Top; S.PreviewWidth = ActualWidth; S.PreviewHeight = ActualHeight;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingForReal)
        {
            // Alt+F4: keep the window alive. A window cannot be hidden while it is closing,
            // so hide it right after.
            e.Cancel = true;
            Dispatcher.BeginInvoke(HideByUser);
        }
        base.OnClosing(e);
    }

    private void HideByUser()
    {
        if (!IsVisible) return;
        SaveGeometry();
        Park();
        UserClosed?.Invoke();
    }

    // ───────────────────────── loading ─────────────────────────

    private void Reset()
    {
        _request++;
        _clock.Stop();
        try { Media.Stop(); } catch { /* ignore */ }
        Media.Source = null;
        Media.LayoutTransform = Transform.Identity;
        _rotation = 0;
        _playing = false;
        _pdf?.Dispose();
        _pdf = null;
        ImageView.Source = null;
        AlbumArt.Source = null;
        AudioArtist.Text = "";
        AudioArtist.Visibility = Visibility.Collapsed;
        InfoImage.Source = null;
        TextView.Text = "";
        foreach (var v in new UIElement[] { ImageView, Media, AudioView, TextView, InfoView, Loading, MediaBar, PageBar })
            v.Visibility = Visibility.Collapsed;
    }

    private void Load(FileItem? it)
    {
        if (it != null && it.FullPath == _shownPath) return;
        Reset();
        int req = _request;

        if (it == null || it.IsParent || it.IsVirtual)
        {
            _shownPath = null;
            TitleText.Text = Loc.T("Preview");
            _baseDetails = DetailsText.Text = "";
            ShowInfo(null, "", Loc.T("NoPreview"));
            return;
        }

        _shownPath = it.FullPath;
        TitleText.Text = it.Name;
        _baseDetails = DetailsText.Text = Details(it);
        string ext = Path.GetExtension(it.FullPath).TrimStart('.');

        if (it.IsDir) LoadThumbnail(it, req, "");
        else if (ImageExt.Contains(ext)) LoadImage(it, req);
        else if (VideoExt.Contains(ext)) LoadMedia(it, req, video: true);
        else if (AudioExt.Contains(ext)) LoadMedia(it, req, video: false);
        else if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase)) _ = LoadPdf(it, req);
        else if (TextExt.Contains(ext) || (ext.Length == 0 && it.Size < 512 * 1024)) LoadText(it, req);
        else LoadThumbnail(it, req, "");
    }

    private static string Details(FileItem it)
    {
        var parts = new List<string>();
        if (it.IsDir) parts.Add(Loc.T("Folder"));
        else if (it.Ext.Length > 0) parts.Add(it.Ext.ToUpperInvariant());
        if (it.SizeText is { Length: > 0 } size && size != "…" && size != Loc.T("Folder")) parts.Add(size);
        if (it.DateText.Length > 0) parts.Add(it.DateText);
        return string.Join("  ·  ", parts);
    }

    private void AddDetail(string extra) => DetailsText.Text = _baseDetails.Length > 0 ? _baseDetails + "  ·  " + extra : extra;

    private void ShowInfo(BitmapSource? image, string glyph, string name)
    {
        InfoImage.Source = image;
        InfoImage.Visibility = image != null ? Visibility.Visible : Visibility.Collapsed;
        InfoGlyph.Text = glyph;
        InfoGlyph.Visibility = image != null ? Visibility.Collapsed : Visibility.Visible;
        InfoName.Text = name;
        InfoView.Visibility = Visibility.Visible;
    }

    /// <summary>Runs shell work on a short-lived STA thread (the Windows shell requires it).</summary>
    private void OnSta<T>(Func<T> work, Action<T> done)
    {
        var t = new Thread(() =>
        {
            T result;
            try { result = work(); } catch { result = default!; }
            Dispatcher.InvokeAsync(() => done(result));
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private void LoadThumbnail(FileItem it, int req, string glyph)
    {
        ShowInfo(null, glyph, it.Name);
        string path = it.FullPath;
        OnSta(() => ShellThumb.ThumbnailOrIcon(path, 256), bmp =>
        {
            if (req != _request || bmp == null) return;
            ShowInfo(bmp, glyph, it.Name);
        });
    }

    private void LoadImage(FileItem it, int req)
    {
        Loading.Visibility = Visibility.Visible;
        string path = it.FullPath;
        Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                                             BitmapCacheOption.None).Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            fs.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (w > 1800) bmp.DecodePixelWidth = 1800;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, w, h);
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (req != _request) return;
            Loading.Visibility = Visibility.Collapsed;
            if (t.IsFaulted) { LoadThumbnail(it, req, ""); return; }
            var (bmp, w, h) = t.Result;
            ImageView.Source = bmp;
            ImageView.Visibility = Visibility.Visible;
            AddDetail($"{w} × {h}");
        }));
    }

    private void LoadMedia(FileItem it, int req, bool video)
    {
        MediaBar.Visibility = Visibility.Visible;
        Seek.Value = 0;
        Seek.Maximum = 1;
        TimeText.Text = "0:00";
        string path = it.FullPath;
        if (video)
        {
            Media.Visibility = Visibility.Visible;
            // Phone videos are often stored sideways with a "rotate" flag that the player ignores.
            OnSta(() => ShellProps.Int(ShellProps.Get(path, "System.Video.Orientation")[0]), deg =>
            {
                if (req != _request) return;
                deg = ((deg % 360) + 360) % 360;
                if (deg is 90 or 180 or 270)
                {
                    _rotation = deg;
                    Media.LayoutTransform = new RotateTransform(deg);
                }
            });
        }
        else
        {
            AudioTitle.Text = Path.GetFileNameWithoutExtension(it.Name);
            AudioView.Visibility = Visibility.Visible;
            OnSta(() => ShellThumb.Thumbnail(path, 400), art =>
            {
                if (req == _request && art != null) AlbumArt.Source = art;
            });
            // Song title and artist from the file's tags, when it has them.
            OnSta(() => ShellProps.Get(path, "System.Title", "System.Music.Artist", "System.Music.AlbumTitle"), p =>
            {
                if (req != _request || p == null) return;
                string title = ShellProps.Text(p[0]), artist = ShellProps.Text(p[1]), album = ShellProps.Text(p[2]);
                if (title.Length > 0) AudioTitle.Text = title;
                var sub = string.Join("  ·  ", new[] { artist, album }.Where(s => s.Length > 0));
                AudioArtist.Text = sub;
                AudioArtist.Visibility = sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            });
        }
        try
        {
            Media.Source = new Uri(it.FullPath);
            Media.Play();
            _playing = true;
            PlayGlyph.Text = "";   // pause
            _clock.Start();
        }
        catch { Media_Failed(this, null!); }
    }

    private async Task LoadPdf(FileItem it, int req)
    {
        Loading.Visibility = Visibility.Visible;
        try
        {
            var pdf = await PdfRender.OpenAsync(it.FullPath);
            if (req != _request) { pdf.Dispose(); return; }
            _pdf = pdf;
            _pdfPage = 0;
            AddDetail(Loc.F("Pages", pdf.PageCount));
            await ShowPdfPage(req);
        }
        catch
        {
            if (req != _request) return;
            Loading.Visibility = Visibility.Collapsed;
            LoadThumbnail(it, req, "");
        }
    }

    private async Task ShowPdfPage(int req)
    {
        if (_pdf == null) return;
        Loading.Visibility = Visibility.Visible;
        var bmp = await _pdf.RenderAsync(_pdfPage, 1400);
        if (req != _request) return;
        Loading.Visibility = Visibility.Collapsed;
        ImageView.Source = bmp;
        ImageView.Visibility = Visibility.Visible;
        PageBar.Visibility = _pdf.PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = Loc.F("PageOf", _pdfPage + 1, _pdf.PageCount);
    }

    private void LoadText(FileItem it, int req)
    {
        string path = it.FullPath;
        Task.Run(() =>
        {
            const int max = 256 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(fs.Length, max)];
            int n = 0, r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
            // Binary file? Show its thumbnail instead.
            int probe = Math.Min(n, 8000);
            for (int i = 0; i < probe; i++) if (buf[i] == 0) return null;
            using var sr = new StreamReader(new MemoryStream(buf, 0, n), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = sr.ReadToEnd();
            return fs.Length > max ? text + "\n\n…" : text;
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (req != _request) return;
            if (t.IsFaulted || t.Result == null) { LoadThumbnail(it, req, ""); return; }
            TextView.Text = t.Result;
            TextView.ScrollToHome();
            TextView.Visibility = Visibility.Visible;
        }));
    }

    // ───────────────────────── player ─────────────────────────

    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void UpdateClock()
    {
        if (!Media.NaturalDuration.HasTimeSpan) return;
        var dur = Media.NaturalDuration.TimeSpan;
        if (!_seeking) Seek.Value = Media.Position.TotalSeconds;
        TimeText.Text = $"{Clock(Media.Position)} / {Clock(dur)}";
    }

    private void Media_Opened(object sender, RoutedEventArgs e)
    {
        if (Media.NaturalDuration.HasTimeSpan)
        {
            Seek.Maximum = Math.Max(1, Media.NaturalDuration.TimeSpan.TotalSeconds);
            int w = Media.NaturalVideoWidth, h = Media.NaturalVideoHeight;
            if (_rotation is 90 or 270) (w, h) = (h, w);
            AddDetail(Clock(Media.NaturalDuration.TimeSpan) + (Media.HasVideo ? $"  ·  {w} × {h}" : ""));
        }
        UpdateClock();
    }

    private void Media_Ended(object sender, RoutedEventArgs e)
    {
        Media.Stop();
        _playing = false;
        PlayGlyph.Text = "";   // play
        Seek.Value = 0;
        UpdateClock();
    }

    private void Media_Failed(object? sender, ExceptionRoutedEventArgs? e)
    {
        var path = _shownPath;
        int req = _request;
        _clock.Stop();
        MediaBar.Visibility = Visibility.Collapsed;
        Media.Visibility = AudioView.Visibility = Visibility.Collapsed;
        if (path == null) return;
        var name = Path.GetFileName(path);
        ShowInfo(null, "", name + "\n" + Loc.T("CantPlay"));
        OnSta(() => ShellThumb.Thumbnail(path, 256), bmp =>
        {
            if (req == _request && bmp != null) ShowInfo(bmp, "", name + "\n" + Loc.T("CantPlay"));
        });
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Media.Source == null) return;
        if (_playing) { Media.Pause(); _playing = false; PlayGlyph.Text = ""; }
        else { Media.Play(); _playing = true; PlayGlyph.Text = ""; _clock.Start(); }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        Media.IsMuted = !Media.IsMuted;
        S.PreviewMuted = Media.IsMuted;
        UpdateMuteGlyph();
    }

    private void UpdateMuteGlyph() => MuteGlyph.Text = Media.IsMuted ? "" : "";

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Media.Position = TimeSpan.FromSeconds(Seek.Value);
        _seeking = false;
    }

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) Media.Position = TimeSpan.FromSeconds(Seek.Value);
    }

    // ───────────────────────── PDF pages ─────────────────────────

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdf == null || _pdfPage == 0) return;
        _pdfPage--;
        await ShowPdfPage(_request);
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdf == null || _pdfPage + 1 >= _pdf.PageCount) return;
        _pdfPage++;
        await ShowPdfPage(_request);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideByUser();

    // The title bar is ordinary window content (not a system caption), so its × button always gets the click.
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
        try { DragMove(); } catch { /* mouse already released */ }
    }

    private static bool FindButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Button) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
