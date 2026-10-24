using System.Globalization;
using System.Text.Json;
using System.Windows;

namespace ClaudiusFileManager;

/// <summary>
/// Loads the translation files in Languages/*.json. Every string becomes an application resource
/// named "L.&lt;key&gt;" (used in XAML with DynamicResource), and code uses Loc.T / Loc.F.
/// Missing keys fall back to English.
/// </summary>
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("pl", "Polski"), ("de", "Deutsch"),
        ("fr", "Français"), ("it", "Italiano"), ("es", "Español")
    };

    public static string Current { get; private set; } = "en";
    public static event Action? Changed;

    private static Dictionary<string, string> _strings = new();
    private static ResourceDictionary? _dict;

    public static string Normalize(string? code)
    {
        code = code?.Trim().ToLowerInvariant();
        return code != null && Languages.Any(l => l.Code == code) ? code : "en";
    }

    public static string Detect() => Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public static string NameOf(string code) => Languages.FirstOrDefault(l => l.Code == code).Name ?? code;

    public static void Apply(string? code)
    {
        code = Normalize(code);
        var map = Read("en");
        if (code != "en")
            foreach (var kv in Read(code)) map[kv.Key] = kv.Value;

        _strings = map;
        Current = code;

        var rd = new ResourceDictionary();
        foreach (var kv in map) rd["L." + kv.Key] = kv.Value;

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_dict != null) merged.Remove(_dict);
        merged.Add(rd);
        _dict = rd;

        Changed?.Invoke();
    }

    public static string T(string key) => _strings.TryGetValue(key, out var v) ? v : key;

    public static string F(string key, params object[] args)
    {
        try { return string.Format(T(key), args); }
        catch (FormatException) { return T(key); }
    }

    private static Dictionary<string, string> Read(string code)
    {
        try
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream($"ClaudiusFileManager.Languages.{code}.json");
            if (s == null) return new();
            var opts = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s, opts) ?? new();
        }
        catch { return new(); }
    }
}
