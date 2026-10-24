using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudiusFileManager;

/// <summary>F3 Lister: quick viewer for images and text (like Total Commander's Lister).</summary>
public sealed class ViewerWindow : Window
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".jxr", ".wdp", ".heic" };

    private const int MaxTextBytes = 2 * 1024 * 1024;

    public ViewerWindow(string path)
    {
        Title = Path.GetFileName(path);
        Width = 920;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "LabelBrush");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        FontFamily = (FontFamily)Application.Current.FindResource("AppFont");
        UseLayoutRounding = true;

        UIElement child;
        try
        {
            if (ImageExt.Contains(Path.GetExtension(path)))
            {
                var bmp = new BitmapImage();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = fs;
                    bmp.EndInit();
                }
                bmp.Freeze();
                Title += $"   ·   {bmp.PixelWidth} × {bmp.PixelHeight}";
                child = new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            }
            else
            {
                var tb = new TextBox
                {
                    Text = ReadText(path),
                    IsReadOnly = true,
                    FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
                    FontSize = 13,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    TextWrapping = TextWrapping.NoWrap,
                    Padding = new Thickness(8)
                };
                // W toggles word wrap, as in Total Commander's Lister
                tb.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.None)
                    {
                        tb.TextWrapping = tb.TextWrapping == TextWrapping.NoWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                        e.Handled = true;
                    }
                };
                tb.SetResourceReference(ForegroundProperty, "LabelBrush");
                tb.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
                Loaded += (_, _) => tb.Focus();
                child = tb;
            }
        }
        catch (Exception ex)
        {
            child = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(14),
            Padding = new Thickness(6),
            Child = child
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        Content = card;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F3 or Key.Q) { Close(); e.Handled = true; }
        };
    }

    private static string ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool truncated = fs.Length > MaxTextBytes;
        var buf = new byte[Math.Min(fs.Length, MaxTextBytes)];
        int n = 0, r;
        while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
        using var sr = new StreamReader(new MemoryStream(buf, 0, n), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var s = sr.ReadToEnd();
        if (s.Contains('\0')) s = s.Replace('\0', '·');
        if (truncated) s += "\n\n" + Loc.T("ViewerTruncated");
        return s;
    }
}
