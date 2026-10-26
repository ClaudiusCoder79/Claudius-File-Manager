using System.IO.Compression;

namespace ClaudiusFileManager;

/// <summary>
/// ZIP archives: pack, unpack, and browse inside a .zip like a folder (paths such as
/// <c>C:\Photos\holiday.zip\day 1\beach.jpg</c>). Built on System.IO.Compression – no extra software.
/// </summary>
public static class ZipOps
{
    public sealed record Entry(string Name, string InnerPath, bool IsDir, long Size, DateTime Modified);

    /// <summary>(fraction 0–1, current item name)</summary>
    public delegate void Progress(double fraction, string name);

    public static bool IsZipName(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static bool IsZipFile(string path) => IsZipName(path) && File.Exists(path);

    /// <summary>
    /// Splits a path that goes inside an archive, e.g. <c>C:\a\b.zip\x\y</c> → (<c>C:\a\b.zip</c>, <c>x/y</c>).
    /// Returns false for ordinary paths.
    /// </summary>
    public static bool TrySplit(string? path, out string zip, out string inner)
    {
        zip = inner = "";
        if (string.IsNullOrEmpty(path)) return false;
        int idx = 0;
        while ((idx = path.IndexOf(".zip", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = idx + 4;
            if (end == path.Length || path[end] is '\\' or '/')
            {
                var candidate = path[..end];
                try
                {
                    if (File.Exists(candidate))
                    {
                        zip = candidate;
                        inner = path[end..].Replace('\\', '/').Trim('/');
                        return true;
                    }
                }
                catch { /* invalid path */ }
            }
            idx = end;
        }
        return false;
    }

    public static bool IsInside(string? path) => TrySplit(path, out _, out _);

    /// <summary>The display path of something inside an archive.</summary>
    public static string Combine(string zip, string inner) =>
        inner.Length == 0 ? zip : zip + "\\" + inner.Replace('/', '\\');

    private static string Norm(string name) => name.Replace('\\', '/');

    // ───────────────────────── browsing ─────────────────────────

    /// <summary>The files and folders directly inside <paramref name="inner"/> (folder sizes are totals).</summary>
    public static List<Entry> List(string zip, string inner)
    {
        using var za = ZipFile.OpenRead(zip);
        string prefix = inner.Length == 0 ? "" : inner.TrimEnd('/') + "/";
        var dirs = new Dictionary<string, (long Size, DateTime Mod)>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        bool found = prefix.Length == 0;

        foreach (var e in za.Entries)
        {
            var full = Norm(e.FullName);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            var rest = full[prefix.Length..];
            if (rest.Length == 0) continue;
            DateTime mod = SafeDate(e);
            int slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var d = rest[..slash];
                if (d.Length == 0) continue;
                dirs.TryGetValue(d, out var cur);
                dirs[d] = (cur.Size + e.Length, mod > cur.Mod ? mod : cur.Mod);
            }
            else files[rest] = new Entry(rest, prefix + rest, false, e.Length, mod);
        }
        if (!found) throw new DirectoryNotFoundException(Loc.F("ErrNotExist", Combine(zip, inner)));

        var list = dirs.Select(d => new Entry(d.Key, prefix + d.Key, true, d.Value.Size, d.Value.Mod)).ToList();
        list.AddRange(files.Values.Where(f => !dirs.ContainsKey(f.Name)));
        return list;
    }

    private static DateTime SafeDate(ZipArchiveEntry e)
    {
        try { return e.LastWriteTime.LocalDateTime; } catch { return DateTime.MinValue; }
    }

    /// <summary>Short summary and file list of an archive (for the Preview window).</summary>
    public static (int Files, long Unpacked, long Packed, List<string> Lines) Summary(string zip, int maxLines = 2000)
    {
        using var za = ZipFile.OpenRead(zip);
        int files = 0;
        long unpacked = 0, packed = 0;
        var lines = new List<string>();
        foreach (var e in za.Entries.OrderBy(e => Norm(e.FullName), StringComparer.OrdinalIgnoreCase))
        {
            var name = Norm(e.FullName);
            bool dir = name.EndsWith('/');
            if (!dir) { files++; unpacked += e.Length; packed += e.CompressedLength; }
            if (lines.Count < maxLines)
                lines.Add(dir ? name : $"{name}    {FileItem.FormatSize(e.Length)}");
        }
        return (files, unpacked, packed, lines);
    }

    // ───────────────────────── unpacking ─────────────────────────

    /// <summary>What would be written where: (entry name in the archive, target path on disk).</summary>
    public static List<(string Entry, string Target, bool IsDir, long Size)> Plan(string zip, IReadOnlyCollection<string> inners, string destDir)
    {
        var dest = Path.GetFullPath(destDir);
        var destRoot = Path.TrimEndingDirectorySeparator(dest) + Path.DirectorySeparatorChar;
        var wanted = inners.Select(i => Norm(i).Trim('/')).ToList();
        var plan = new List<(string, string, bool, long)>();

        using var za = ZipFile.OpenRead(zip);
        foreach (var e in za.Entries)
        {
            var full = Norm(e.FullName);
            foreach (var w in wanted)
            {
                // The whole archive, the item itself, or anything inside a selected folder.
                bool match = w.Length == 0 || full.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                             full.StartsWith(w + "/", StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                int cut = w.Length == 0 ? 0 : (w.LastIndexOf('/') + 1);
                var rel = full[cut..].TrimStart('/');
                if (rel.Length == 0) break;
                var target = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar)));
                // Never write outside the target folder ("zip slip").
                if (!target.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(dest), StringComparison.OrdinalIgnoreCase))
                    break;
                plan.Add((e.FullName, target, full.EndsWith('/'), e.Length));
                break;
            }
        }
        return plan;
    }

    public static void Extract(string zip, List<(string Entry, string Target, bool IsDir, long Size)> plan,
                               bool overwrite, Progress? progress, CancellationToken ct)
    {
        using var za = ZipFile.OpenRead(zip);
        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in za.Entries) byName.TryAdd(e.FullName, e);

        long total = Math.Max(1, plan.Sum(p => p.Size)), done = 0;
        foreach (var (entry, target, isDir, size) in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (isDir) { Directory.CreateDirectory(target); continue; }
            progress?.Invoke((double)done / total, Path.GetFileName(target));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && !overwrite) { done += size; continue; }
            if (!byName.TryGetValue(entry, out var e)) continue;

            var tmp = target + ".~claudius";
            try
            {
                using (var src = e.Open())
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    CopyWithProgress(src, dst, ct, n => { done += n; progress?.Invoke((double)done / total, Path.GetFileName(target)); });
                File.Move(tmp, target, overwrite: true);
                try { File.SetLastWriteTime(target, e.LastWriteTime.LocalDateTime); } catch { /* invalid date */ }
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        progress?.Invoke(1, "");
    }

    /// <summary>Unpacks whole archives. If an archive holds more than one item at its top level,
    /// it goes into its own folder (named after the archive) so the target folder doesn't get cluttered.</summary>
    public static string TargetFor(string zip, string destDir)
    {
        var top = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var za = ZipFile.OpenRead(zip))
            foreach (var e in za.Entries)
            {
                var n = Norm(e.FullName).TrimStart('/');
                int s = n.IndexOf('/');
                top.Add(s < 0 ? n : n[..s]);
                if (top.Count > 1) break;
            }
        if (top.Count <= 1) return destDir;

        var baseName = Path.GetFileNameWithoutExtension(zip);
        var dir = Path.Combine(destDir, baseName);
        for (int i = 2; File.Exists(dir); i++) dir = Path.Combine(destDir, $"{baseName} ({i})");
        return dir;
    }

    /// <summary>Copies one file out of an archive into a private temporary folder (to open, view or preview it).</summary>
    public static string ExtractToTemp(string zip, string inner)
    {
        var fi = new FileInfo(zip);
        var key = (fi.FullName.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks).GetHashCode().ToString("x8");
        var target = Path.Combine(TempRoot, key, inner.Replace('/', Path.DirectorySeparatorChar));
        using var za = ZipFile.OpenRead(zip);
        var e = za.Entries.FirstOrDefault(x => Norm(x.FullName).Equals(inner, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException(Loc.F("ErrNotExist", Combine(zip, inner)));
        if (File.Exists(target) && new FileInfo(target).Length == e.Length) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        e.ExtractToFile(target, overwrite: true);
        return target;
    }

    public static string TempRoot => Path.Combine(Path.GetTempPath(), "ClaudiusFileManager", "zip");

    /// <summary>Removes temporary copies left from earlier sessions.</summary>
    public static void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var d in Directory.GetDirectories(TempRoot))
                if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-1))
                    try { Directory.Delete(d, true); } catch { /* in use */ }
        }
        catch { /* ignore */ }
    }

    // ───────────────────────── packing ─────────────────────────

    private static List<(string Source, string EntryName, bool IsDir, long Size)> Collect(IEnumerable<string> sources, string prefix, string? skip)
    {
        var list = new List<(string, string, bool, long)>();
        prefix = prefix.Length == 0 ? "" : prefix.TrimEnd('/') + "/";
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var src in sources)
        {
            if (File.Exists(src))
            {
                list.Add((src, prefix + Path.GetFileName(src), false, new FileInfo(src).Length));
                continue;
            }
            if (!Directory.Exists(src)) continue;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(src));
            var name = Path.GetFileName(root);
            if (string.IsNullOrEmpty(name)) name = root.TrimEnd(':', '\\');      // a whole drive
            list.Add((root, prefix + name + "/", true, 0));
            foreach (var fsi in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
            {
                if (skip != null && string.Equals(fsi.FullName, skip, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Norm(Path.GetRelativePath(root, fsi.FullName));
                if (fsi is DirectoryInfo) list.Add((fsi.FullName, $"{prefix}{name}/{rel}/", true, 0));
                else list.Add((fsi.FullName, $"{prefix}{name}/{rel}", false, ((FileInfo)fsi).Length));
            }
        }
        return list;
    }

    /// <summary>Packs files and folders into a new archive (written to a temporary file first, so a
    /// cancelled or failed run never leaves a broken .zip behind).</summary>
    public static void Create(string zipPath, IEnumerable<string> sources, Progress? progress, CancellationToken ct)
    {
        var items = Collect(sources, "", Path.GetFullPath(zipPath));
        var tmp = zipPath + ".~claudius";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
                Write(za, items, progress, ct);
            File.Move(tmp, zipPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        progress?.Invoke(1, "");
    }

    /// <summary>Adds files and folders to an existing archive (into <paramref name="innerFolder"/>);
    /// entries with the same name are replaced.</summary>
    public static void Add(string zipPath, string innerFolder, IEnumerable<string> sources, Progress? progress, CancellationToken ct)
    {
        var items = Collect(sources, innerFolder, Path.GetFullPath(zipPath));
        // Work on a copy so the original stays intact if anything goes wrong or is cancelled.
        var tmp = zipPath + ".~claudius";
        try
        {
            File.Copy(zipPath, tmp, overwrite: true);
            using (var za = ZipFile.Open(tmp, ZipArchiveMode.Update))
            {
                var names = items.Select(i => i.EntryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var old in za.Entries.Where(e => names.Contains(Norm(e.FullName))).ToList()) old.Delete();
                Write(za, items, progress, ct);
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        progress?.Invoke(1, "");
    }

    private static void Write(ZipArchive za, List<(string Source, string EntryName, bool IsDir, long Size)> items,
                              Progress? progress, CancellationToken ct)
    {
        long total = Math.Max(1, items.Sum(i => i.Size)), done = 0;
        foreach (var (source, entryName, isDir, size) in items)
        {
            ct.ThrowIfCancellationRequested();
            if (isDir) { za.CreateEntry(entryName); continue; }
            progress?.Invoke((double)done / total, Path.GetFileName(source));
            // Already-compressed formats are stored as they are – much faster, same size.
            var level = AlreadyCompressed(source) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = za.CreateEntry(entryName, level);
            try { entry.LastWriteTime = File.GetLastWriteTime(source); } catch { /* out of zip date range */ }
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = entry.Open();
            CopyWithProgress(src, dst, ct, n => { done += n; progress?.Invoke((double)done / total, Path.GetFileName(source)); });
        }
    }

    private static readonly HashSet<string> Compressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".cab", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".pdf", ".msi", ".iso"
    };

    private static bool AlreadyCompressed(string path) => Compressed.Contains(Path.GetExtension(path));

    private static void CopyWithProgress(Stream src, Stream dst, CancellationToken ct, Action<long> advanced)
    {
        var buf = new byte[1 << 17];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buf, 0, n);
            advanced(n);
        }
    }

    // ───────────────────────── deleting ─────────────────────────

    public static void Delete(string zipPath, IReadOnlyCollection<string> inners, CancellationToken ct)
    {
        var wanted = inners.Select(i => Norm(i).Trim('/')).Where(w => w.Length > 0).ToList();
        var tmp = zipPath + ".~claudius";
        try
        {
            File.Copy(zipPath, tmp, overwrite: true);
            using (var za = ZipFile.Open(tmp, ZipArchiveMode.Update))
            {
                foreach (var e in za.Entries.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    var full = Norm(e.FullName);
                    if (wanted.Any(w => full.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                                        full.StartsWith(w + "/", StringComparison.OrdinalIgnoreCase)))
                        e.Delete();
                }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
