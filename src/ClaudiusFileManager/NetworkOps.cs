using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClaudiusFileManager;

/// <summary>Network drives, network locations, computers and shares, plus cloud-storage folders.</summary>
public static class NetworkOps
{
    /// <summary>Virtual path of the "Network" view.</summary>
    public const string NetRoot = @"\\";

    public static bool IsNetRoot(string? p) => p is @"\\" or @"\\\" ;

    /// <summary>"\\SERVER" (a computer, not yet a share).</summary>
    public static bool IsServerPath(string? p)
    {
        if (p == null || !p.StartsWith(@"\\") || IsNetRoot(p)) return false;
        var rest = p[2..].TrimEnd('\\');
        return rest.Length > 0 && !rest.Contains('\\');
    }

    public static bool IsVirtualPath(string? p) => IsNetRoot(p) || IsServerPath(p);

    /// <summary>"\\SERVER\share" exactly (the top of a share).</summary>
    public static bool IsShareRoot(string? p)
    {
        if (p == null || !p.StartsWith(@"\\")) return false;
        var parts = p[2..].TrimEnd('\\').Split('\\');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0;
    }

    public static string ServerOf(string uncPath) => @"\\" + uncPath[2..].Split('\\')[0];

    // ───────────── Windows "Map network drive" / "Disconnect network drive" dialogs ─────────────

    private const int RESOURCETYPE_DISK = 1;

    [DllImport("mpr.dll")] private static extern int WNetConnectionDialog(IntPtr hwnd, int dwType);
    [DllImport("mpr.dll")] private static extern int WNetDisconnectDialog(IntPtr hwnd, int dwType);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    /// <summary>Shows the standard Windows dialog. True when a drive was mapped.</summary>
    public static bool MapDriveDialog(IntPtr owner) => WNetConnectionDialog(owner, RESOURCETYPE_DISK) == 0;

    public static bool DisconnectDriveDialog(IntPtr owner) => WNetDisconnectDialog(owner, RESOURCETYPE_DISK) == 0;

    /// <summary>"\\server\share" behind a mapped drive letter such as "Z:", or null.</summary>
    public static string? UncOf(string drive)
    {
        try
        {
            var sb = new StringBuilder(512);
            int len = sb.Capacity;
            return WNetGetConnection(drive.TrimEnd('\\'), sb, ref len) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    // ───────────── Shares on a computer ─────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string shi1_netname;
        public uint shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string serverName, int level, out IntPtr bufPtr, int prefMaxLen,
                                           out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>Disk shares on a computer (admin shares like C$ only when <paramref name="includeHidden"/>).</summary>
    public static List<(string Name, string Remark)> GetShares(string server, bool includeHidden)
    {
        var result = new List<(string, string)>();
        int resume = 0;
        int rc = NetShareEnum(server, 1, out var buf, -1, out var read, out _, ref resume);
        if (rc != 0 && rc != 234) throw new Win32Exception(rc);
        try
        {
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < read; i++)
            {
                var si = Marshal.PtrToStructure<SHARE_INFO_1>(buf + i * size);
                bool disk = (si.shi1_type & 0xFF) == 0;               // STYPE_DISKTREE
                bool special = (si.shi1_type & 0x80000000) != 0 || si.shi1_netname.EndsWith('$');
                if (disk && (includeHidden || !special)) result.Add((si.shi1_netname, si.shi1_remark ?? ""));
            }
        }
        finally { if (buf != IntPtr.Zero) NetApiBufferFree(buf); }
        return result.OrderBy(s => s.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────────── Computers on the network (background discovery) ─────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetOpenEnum(int scope, int type, int usage, NETRESOURCE? resource, out IntPtr handle);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetEnumResource(IntPtr handle, ref int count, IntPtr buffer, ref int bufferSize);
    [DllImport("mpr.dll")] private static extern int WNetCloseEnum(IntPtr handle);

    private static List<string> _computers = new();
    private static DateTime _computersAt = DateTime.MinValue;
    private static Task? _discovery;

    public static List<string> KnownComputers { get { lock (_computers) return _computers.ToList(); } }

    /// <summary>Starts looking for computers unless a recent result exists; <paramref name="done"/> runs on success.</summary>
    public static void DiscoverComputers(Action done)
    {
        if (DateTime.UtcNow - _computersAt < TimeSpan.FromMinutes(3) || _discovery is { IsCompleted: false }) return;
        _discovery = Task.Run(() =>
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try { Walk(null, found, 0); } catch { /* discovery is best-effort */ }
            lock (_computers) _computers = found.ToList();
            _computersAt = DateTime.UtcNow;
            done();
        });
    }

    private static void Walk(NETRESOURCE? container, SortedSet<string> found, int depth)
    {
        if (depth > 4 || found.Count > 500) return;
        // RESOURCE_GLOBALNET = 2, usage 0 = everything
        if (WNetOpenEnum(2, RESOURCETYPE_DISK, 0, container, out var handle) != 0) return;
        const int bufSize = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(bufSize);
        try
        {
            int itemSize = Marshal.SizeOf<NETRESOURCE>();
            while (true)
            {
                int count = -1, size = bufSize;
                int rc = WNetEnumResource(handle, ref count, buffer, ref size);
                if (rc != 0) break;                                   // 259 = no more items
                for (int i = 0; i < count; i++)
                {
                    var nr = new NETRESOURCE();
                    Marshal.PtrToStructure(buffer + i * itemSize, nr);
                    if (nr.dwDisplayType == 2 && nr.lpRemoteName is { Length: > 2 } name)   // RESOURCEDISPLAYTYPE_SERVER
                        found.Add(name);
                    else if ((nr.dwUsage & 2) != 0)                    // RESOURCEUSAGE_CONTAINER
                        Walk(nr, found, depth + 1);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            WNetCloseEnum(handle);
        }
    }

    // ───────────── Network locations (shortcuts in "This PC → Network locations") ─────────────

    public static List<(string Name, string Target)> NetworkLocations()
    {
        var list = new List<(string, string)>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   @"Microsoft\Windows\Network Shortcuts");
            if (!Directory.Exists(dir)) return list;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return list;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var lnk = Path.Combine(sub, "target.lnk");
                if (!File.Exists(lnk)) continue;
                try
                {
                    string target = shell.CreateShortcut(lnk).TargetPath;
                    if (!string.IsNullOrEmpty(target) && target.StartsWith(@"\\"))
                        list.Add((Path.GetFileName(sub), target));
                }
                catch { /* not a folder link (FTP, web, media server) */ }
            }
        }
        catch { /* ignore */ }
        return list;
    }

    // ───────────── Cloud storage folders ─────────────

    public static List<(string Name, string Path)> CloudFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<(string Name, string Path)>();
        void Add(string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (Directory.Exists(path) && !list.Any(x => ShellOps.SamePath(x.Path, path))) list.Add((name, path));
            }
            catch { /* ignore */ }
        }

        Add("iCloud Drive", Path.Combine(home, "iCloudDrive"));
        Add("iCloud Photos", Path.Combine(home, "Pictures", "iCloud Photos"));
        Add("iCloud Photos", Path.Combine(home, "iCloudPhotos"));

        var personal = Environment.GetEnvironmentVariable("OneDriveConsumer") ?? Environment.GetEnvironmentVariable("OneDrive");
        Add("OneDrive", personal);
        var business = Environment.GetEnvironmentVariable("OneDriveCommercial");
        if (!string.IsNullOrEmpty(business)) Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(business)), business);

        foreach (var (name, path) in DropboxFolders()) Add(name, path);
        Add("Dropbox", Path.Combine(home, "Dropbox"));

        // Google Drive for desktop and pCloud appear as their own drive letters
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is DriveType.Network or DriveType.CDRom) continue;   // never wait for these
                string label;
                try { label = d.IsReady ? d.VolumeLabel : ""; } catch { continue; }
                if (label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                {
                    var myDrive = Path.Combine(d.RootDirectory.FullName, "My Drive");
                    Add("Google Drive", Directory.Exists(myDrive) ? myDrive : d.RootDirectory.FullName);
                }
                else if (label.Contains("pCloud", StringComparison.OrdinalIgnoreCase))
                    Add("pCloud", d.RootDirectory.FullName);
            }
        }
        catch { /* ignore */ }
        Add("Google Drive", Path.Combine(home, "Google Drive"));
        Add("Box", Path.Combine(home, "Box"));
        Add("MEGA", Path.Combine(home, "MEGA"));
        return list;
    }

    private static IEnumerable<(string, string)> DropboxFolders()
    {
        var found = new List<(string, string)>();
        foreach (var baseDir in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            try
            {
                var info = Path.Combine(Environment.GetFolderPath(baseDir), "Dropbox", "info.json");
                if (!File.Exists(info)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(info));
                foreach (var kind in new[] { "personal", "business" })
                    if (doc.RootElement.TryGetProperty(kind, out var e) && e.TryGetProperty("path", out var p) &&
                        p.GetString() is { Length: > 0 } path)
                        found.Add((kind == "business" ? "Dropbox (Business)" : "Dropbox", path));
            }
            catch { /* ignore */ }
        }
        return found;
    }
}
