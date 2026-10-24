using System.Windows;
using Microsoft.Win32;

namespace ClaudiusFileManager;

public partial class App : Application
{
    public static Settings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = Settings.Load();

        ThemeManager.Init();
        ThemeManager.Apply(ThemeManager.Parse(Settings.Theme));

        // Language: saved choice → the one picked in the installer → Windows display language.
        string? lang = Settings.Language;
        if (string.IsNullOrEmpty(lang))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\ClaudiusFileManager");
                lang = key?.GetValue("Language") as string;
            }
            catch { /* ignore */ }
        }
        Loc.Apply(string.IsNullOrEmpty(lang) ? Loc.Detect() : lang);

        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            IosDialog.Alert(MainWindow, Loc.T("ErrUnexpected"), ex.Exception.Message);
            ex.Handled = true;
        };
    }
}

/// <summary>Attached property used by the function-key bar to show the key badge (F5, F6 …).</summary>
public static class Hint
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(Hint), new PropertyMetadata(""));

    public static string GetKey(DependencyObject o) => (string)o.GetValue(KeyProperty);
    public static void SetKey(DependencyObject o, string value) => o.SetValue(KeyProperty, value);
}

/// <summary>Attached properties for the glossy ribbon tiles: the glyph and the tile colour.</summary>
public static class Rb
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(Rb), new PropertyMetadata(""));

    public static readonly DependencyProperty TileProperty =
        DependencyProperty.RegisterAttached("Tile", typeof(System.Windows.Media.Brush), typeof(Rb),
                                            new PropertyMetadata(null));

    public static string GetGlyph(DependencyObject o) => (string)o.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject o, string value) => o.SetValue(GlyphProperty, value);
    public static System.Windows.Media.Brush? GetTile(DependencyObject o) => (System.Windows.Media.Brush?)o.GetValue(TileProperty);
    public static void SetTile(DependencyObject o, System.Windows.Media.Brush? value) => o.SetValue(TileProperty, value);
}
