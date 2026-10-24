using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudiusFileManager;

/// <summary>
/// Copy / move / delete through the Windows shell, so you get the native
/// Windows 11 progress dialog, conflict resolution, Undo and the Recycle Bin.
/// </summary>
public static class ShellOps
{
    private const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    private const ushort FOF_RENAMEONCOLLISION = 0x0008;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;
    private const int ERROR_CANCELLED = 0x4C7;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public static bool Copy(Window owner, IEnumerable<string> sources, string destDir)
    {
        var list = sources.ToList();
        bool sameDir = list.All(p => SamePath(Path.GetDirectoryName(p), destDir));
        ushort flags = (ushort)(FOF_NOCONFIRMMKDIR | FOF_ALLOWUNDO | (sameDir ? FOF_RENAMEONCOLLISION : 0));
        return Run(owner, FO_COPY, list, destDir, flags);
    }

    public static bool Move(Window owner, IEnumerable<string> sources, string destDir) =>
        Run(owner, FO_MOVE, sources.ToList(), destDir, (ushort)(FOF_NOCONFIRMMKDIR | FOF_ALLOWUNDO));

    public static bool Delete(Window owner, IEnumerable<string> sources, bool permanent) =>
        Run(owner, FO_DELETE, sources.ToList(), null,
            (ushort)(FOF_NOCONFIRMATION | (permanent ? 0 : FOF_ALLOWUNDO | FOF_WANTNUKEWARNING)));

    // ---- Windows "Properties" dialog ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    public static void ShowProperties(Window owner, string path)
    {
        const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            hwnd = new WindowInteropHelper(owner).Handle,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5 // SW_SHOW
        };
        ShellExecuteEx(ref info);
    }

    public static bool SamePath(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
                      StringComparison.OrdinalIgnoreCase);

    private static bool Run(Window owner, uint func, List<string> sources, string? dest, ushort flags)
    {
        if (sources.Count == 0) return false;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = new WindowInteropHelper(owner).Handle,
            wFunc = func,
            // Both lists must be double-null terminated; the marshaller adds the final \0.
            pFrom = string.Join("\0", sources) + "\0",
            pTo = dest == null ? null : dest + "\0",
            fFlags = flags
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0 && rc != ERROR_CANCELLED && !op.fAnyOperationsAborted)
            IosDialog.Alert(owner, Loc.T("ErrOpFailed"), Loc.F("ErrOpFailedMsg", rc.ToString("X")));
        return rc == 0 && !op.fAnyOperationsAborted;
    }
}
