namespace ClaudiusFileManager;

/// <summary>Measures folders in the background and remembers the results for a short while.</summary>
public static class FolderSizer
{
    private static readonly Dictionary<string, (long Bytes, DateTime At)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheLife = TimeSpan.FromMinutes(2);

    public static bool TryGetCached(string path, out long bytes)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var e) && DateTime.UtcNow - e.At < CacheLife) { bytes = e.Bytes; return true; }
        }
        bytes = 0;
        return false;
    }

    /// <summary>Drops cached sizes for a folder and everything inside it (after files change there).</summary>
    public static void Forget(string path)
    {
        lock (Cache)
        {
            foreach (var k in Cache.Keys.Where(k => k.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList())
                Cache.Remove(k);
        }
    }

    /// <summary>Total size of every file below the folder. Junctions and folder links are not followed
    /// (so nothing is counted twice) and unreadable folders are skipped.</summary>
    public static (long Bytes, int Files, int Dirs) Measure(string path, CancellationToken ct = default)
    {
        long bytes = 0;
        int files = 0, dirs = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
                {
                    if (fsi is FileInfo fi) { bytes += fi.Length; files++; }
                    else
                    {
                        dirs++;
                        if ((fsi.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(fsi.FullName);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* folder vanished or is unreadable */ }
        }
        lock (Cache) Cache[path] = (bytes, DateTime.UtcNow);
        return (bytes, files, dirs);
    }
}
