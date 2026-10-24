namespace ClaudiusFileManager;

/// <summary>Reads Windows file properties (the same ones Explorer shows in Details). Call from an STA thread.</summary>
public static class ShellProps
{
    /// <summary>Returns the named properties (e.g. "System.Video.Orientation"), or null for each one that is missing.</summary>
    public static object?[] Get(string path, params string[] names)
    {
        var result = new object?[names.Length];
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return result;
            shell = Activator.CreateInstance(type);
            dynamic sh = shell!;
            dynamic? folder = sh.NameSpace(Path.GetDirectoryName(path));
            if (folder == null) return result;
            dynamic? item = folder.ParseName(Path.GetFileName(path));
            if (item == null) return result;
            for (int i = 0; i < names.Length; i++)
            {
                try { result[i] = item.ExtendedProperty(names[i]); } catch { /* not available */ }
            }
        }
        catch { /* shell unavailable */ }
        finally
        {
            if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        return result;
    }

    public static string Text(object? value) => value switch
    {
        null => "",
        string s => s.Trim(),
        string[] arr => string.Join(", ", arr.Where(a => !string.IsNullOrWhiteSpace(a))),
        object[] arr => string.Join(", ", arr.Select(a => a?.ToString()).Where(a => !string.IsNullOrWhiteSpace(a))),
        _ => value.ToString()?.Trim() ?? ""
    };

    public static int Int(object? value)
    {
        try { return value == null ? 0 : Convert.ToInt32(value); } catch { return 0; }
    }
}
