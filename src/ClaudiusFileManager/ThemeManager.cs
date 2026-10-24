using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ClaudiusFileManager;

public enum ThemeMode { System, Light, Dark }

/// <summary>Swaps Themes/Light.xaml and Themes/Dark.xaml at runtime and follows Windows when set to System.</summary>
public static class ThemeManager
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    private static ResourceDictionary? _colors;
    private static bool _initialised;

    public static ThemeMode Parse(string? s) =>
        Enum.TryParse<ThemeMode>(s, true, out var m) ? m : ThemeMode.System;

    public static void Init()
    {
        if (_initialised) return;
        _initialised = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Mode == ThemeMode.System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.InvokeAsync(() => Apply(ThemeMode.System));
        };
    }

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        bool dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && WindowsUsesDarkApps());

        var rd = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_colors != null) merged.Remove(_colors);
        merged.Insert(0, rd);
        _colors = rd;
        IsDark = dark;

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark or light native title bar for windows that use the standard frame (viewer).</summary>
    public static void ApplyTitleBar(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int v = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(h, 20, ref v, sizeof(int)); } catch { /* older Windows */ }
    }
}
