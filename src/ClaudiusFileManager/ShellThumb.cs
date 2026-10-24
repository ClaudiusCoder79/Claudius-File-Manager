using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudiusFileManager;

/// <summary>
/// Large thumbnails exactly as Windows Explorer shows them (documents, videos, album art, folders …),
/// from the Windows shell's thumbnail providers. Call from an STA thread.
/// </summary>
public static class ShellThumb
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;

    /// <summary>A real thumbnail (document page, video frame, album art …), or null when the file has none.</summary>
    public static BitmapSource? Thumbnail(string path, int size) => Get(path, size, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK);

    /// <summary>A thumbnail if there is one, otherwise the file's large icon.</summary>
    public static BitmapSource? ThumbnailOrIcon(string path, int size) => Get(path, size, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);

    /// <summary>
    /// Converts the shell's 32-bit bitmap keeping its transparency (icons would otherwise get a black
    /// background), and fixes bitmaps that carry no alpha at all (photos would otherwise be invisible).
    /// </summary>
    private static BitmapSource FromHBitmap(IntPtr hbm)
    {
        var ds = new DIBSECTION();
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds) == Marshal.SizeOf<DIBSECTION>() &&
            ds.dsBm.bmBitsPixel == 32 && ds.dsBm.bmBits != IntPtr.Zero)
        {
            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, pixels, 0, pixels.Length);

            if (ds.dsBmih.biHeight > 0)              // bottom-up: flip rows
            {
                var row = new byte[stride];
                for (int y = 0; y < h / 2; y++)
                {
                    Buffer.BlockCopy(pixels, y * stride, row, 0, stride);
                    Buffer.BlockCopy(pixels, (h - 1 - y) * stride, pixels, y * stride, stride);
                    Buffer.BlockCopy(row, 0, pixels, (h - 1 - y) * stride, stride);
                }
            }

            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }

            var src = BitmapSource.Create(w, h, 96, 96, anyAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32,
                                          null, pixels, stride);
            src.Freeze();
            return src;
        }

        var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                                                             BitmapSizeOptions.FromEmptyOptions());
        fallback.Freeze();
        return fallback;
    }

    private static BitmapSource? Get(string path, int size, int flags)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
        finally { if (factory != null) Marshal.ReleaseComObject(factory); }
    }
}
