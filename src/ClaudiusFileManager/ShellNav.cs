using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudiusFileManager;

/// <summary>Reads what Windows Explorer shows in its navigation pane: real icons, display names
/// and the pinned Quick access folders (in the user's order).</summary>
public static class ShellNav
{
    // Shell folders
    public const string HomeFolder = "shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    public const string GalleryFolder = "shell:::{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
    public const string QuickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
    public const string ThisPc = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    public const string NetworkFolder = "shell:::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
    /// <summary>Personal OneDrive ("Michal - Personal" in Explorer).</summary>
    public const string OneDrivePersonal = "shell:::{018D5C66-4533-4307-9B53-224DE2ED1FE6}";

    /// <summary>The file Windows updates whenever a folder is pinned or unpinned in Quick access.</summary>
    public static string QuickAccessStoreFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\AutomaticDestinations");
    public const string QuickAccessStoreFile = "f01b4d95cf55d32a.automaticDestinations-ms";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_DISPLAYNAME = 0x200, SHGFI_PIDL = 0x8,
                       SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_DIRECTORY = 0x10;

    private static readonly Dictionary<string, (ImageSource? Icon, string? Name)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void ClearCache() => Cache.Clear();

    /// <summary>Small icon and display name exactly as Explorer shows them, for a path or a "shell:::" folder.
    /// Returns (null, null) when the item does not exist on this PC.</summary>
    public static (ImageSource? Icon, string? Name) Info(string pathOrShell, bool offline = false)
    {
        if (Cache.TryGetValue(pathOrShell, out var hit)) return hit;
        var fi = new SHFILEINFO();
        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
        const uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_DISPLAYNAME;
        bool ok = false;
        try
        {
            if (pathOrShell.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                if (SHParseDisplayName(pathOrShell, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                {
                    ok = SHGetFileInfoPidl(pidl, 0, ref fi, size, flags | SHGFI_PIDL) != IntPtr.Zero;
                    Marshal.FreeCoTaskMem(pidl);
                }
            }
            else if (offline)
                // Generic folder icon without touching the disk (network folders may be unreachable)
                ok = SHGetFileInfo(pathOrShell, FILE_ATTRIBUTE_DIRECTORY, ref fi, size, flags | SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero;
            else ok = SHGetFileInfo(pathOrShell, 0, ref fi, size, flags) != IntPtr.Zero;
        }
        catch { ok = false; }

        ImageSource? icon = null;
        if (fi.hIcon != IntPtr.Zero)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                icon = bmp;
            }
            catch { /* ignore */ }
            DestroyIcon(fi.hIcon);
        }
        var result = ok ? (icon, string.IsNullOrEmpty(fi.szDisplayName) ? null : fi.szDisplayName) : ((ImageSource?)null, (string?)null);
        Cache[pathOrShell] = result;
        return result;
    }

    /// <summary>Folders pinned to Quick access, in the same order as Explorer's navigation pane.</summary>
    public static List<(string Name, string Path)> PinnedFolders()
    {
        var list = new List<(string, string)>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return list;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic? folder = shell.Namespace(QuickAccess);
            if (folder == null) return list;
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                if (item == null) continue;
                string path, name;
                try { path = item.Path; name = item.Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(path) || path.StartsWith("::")) continue;

                // Windows 11 marks pinned items; frequent (not pinned) folders and recent files are left out,
                // like Explorer's navigation pane. Pinned folders are kept even when offline, as Explorer does.
                object? pinned = null;
                try { pinned = item.ExtendedProperty("System.Home.IsPinned"); } catch { /* Windows 10 */ }
                if (pinned is bool b)
                {
                    if (!b) continue;
                }
                else
                {
                    bool isFolder = false;
                    try { isFolder = item.IsFolder; } catch { /* ignore */ }
                    if (!isFolder) continue;
                }
                list.Add((name, path));
            }
            Marshal.FinalReleaseComObject(shell);
        }
        catch { /* Quick access not available */ }
        return list;
    }
}
