using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace ClaudiusFileManager;

/// <summary>One row in a file panel.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _sizeKnown, _calculating;

    public string Name { get; private init; } = "";
    public string FullPath { get; private init; } = "";
    public bool IsDir { get; private init; }
    public bool IsParent { get; private init; }
    /// <summary>Not a real file or folder: a computer, network location or mapped drive in the Network view.</summary>
    public bool IsVirtual { get; private init; }
    public bool IsHidden { get; private init; }
    /// <summary>File length, or the total size of a folder once it has been measured (0 until then).</summary>
    public long Size { get; private set; }
    public DateTime Modified { get; private init; }
    public string DisplayName { get; private init; } = "";
    public string Ext { get; private init; } = "";
    public string Glyph { get; private init; } = "";
    public Brush IconBrush { get; private init; } = Palette.Gray;

    public string SizeText =>
        IsParent || IsVirtual ? "" :
        !IsDir || _sizeKnown ? FormatSize(Size) :
        _calculating ? "…" : Loc.T("Folder");

    /// <summary>Shows an ellipsis in the Size column while the folder is being measured.</summary>
    public void MarkCalculating()
    {
        if (!IsDir || IsParent || _sizeKnown) return;
        _calculating = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
    }

    public void SetFolderSize(long bytes)
    {
        Size = bytes;
        _sizeKnown = true;
        _calculating = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
    }
    public string DateText => IsParent || IsVirtual ? "" : Modified.ToString("yyyy-MM-dd  HH:mm");
    public double RowOpacity => IsHidden ? 0.5 : 1.0;

    public static FileItem ParentOf(string parentPath) => new()
    {
        Name = "..", DisplayName = "..", FullPath = parentPath, IsDir = true, IsParent = true,
        Glyph = "", IconBrush = Palette.Gray
    };

    /// <summary>An entry in the Network view; opening it navigates to <paramref name="target"/>.</summary>
    public static FileItem Link(string display, string target, string glyph, Brush brush, string ext = "") => new()
    {
        Name = display, DisplayName = display, FullPath = target, IsDir = true, IsVirtual = true,
        Glyph = glyph, IconBrush = brush, Ext = ext
    };

    public static FileItem From(FileSystemInfo fsi)
    {
        bool dir = fsi is DirectoryInfo;
        var attr = fsi.Attributes;
        string name = fsi.Name, ext = "", display = name;
        if (!dir)
        {
            int dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1) { ext = name[(dot + 1)..]; display = name[..dot]; }
        }

        var (glyph, brush) = dir ? ("", Palette.Folder) : IconFor(ext.ToLowerInvariant());
        if (dir && (attr & FileAttributes.ReparsePoint) != 0) glyph = ""; // junction / symlink

        return new FileItem
        {
            Name = name,
            DisplayName = display,
            Ext = ext,
            FullPath = fsi.FullName,
            IsDir = dir,
            IsHidden = (attr & (FileAttributes.Hidden | FileAttributes.System)) != 0,
            Size = dir ? 0 : ((FileInfo)fsi).Length,
            Modified = fsi.LastWriteTime,
            Glyph = glyph,
            IconBrush = brush
        };
    }

    private static (string, Brush) IconFor(string ext) => ext switch
    {
        "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "heic" or "tif" or "tiff" or "svg" or "ico" or "raw"
            => ("", Palette.Green),
        "mp3" or "wav" or "flac" or "aac" or "m4a" or "ogg" or "wma" or "opus"
            => ("", Palette.Pink),
        "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm" or "m4v"
            => ("", Palette.Purple),
        "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "cab" or "iso"
            => ("", Palette.Brown),
        "exe" or "msi" or "bat" or "cmd" or "ps1" or "com" or "appx" or "msix"
            => ("", Palette.Indigo),
        "cs" or "js" or "ts" or "py" or "cpp" or "c" or "h" or "java" or "json" or "xml" or "xaml" or "html"
            or "css" or "go" or "rs" or "sh" or "yml" or "yaml" or "sln" or "csproj"
            => ("", Palette.Teal),
        "pdf" => ("", Palette.Red),
        "doc" or "docx" or "rtf" or "odt" => ("", Palette.Blue),
        "xls" or "xlsx" or "csv" or "ods" => ("", Palette.Green),
        "ppt" or "pptx" or "odp" => ("", Palette.Orange),
        "txt" or "md" or "log" or "ini" or "cfg" => ("", Palette.Gray),
        "lnk" or "url" => ("", Palette.Blue),
        _ => ("", Palette.Gray)
    };

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}

public sealed record DriveEntry(string Label, string Root, string Glyph, string Tooltip, bool IsCurrent = false);

internal static class Palette
{
    private static SolidColorBrush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public static readonly Brush Folder = B("#1E90FF");
    public static readonly Brush Gray = B("#8E8E93");
    public static readonly Brush Green = B("#34C759");
    public static readonly Brush Pink = B("#FF2D55");
    public static readonly Brush Purple = B("#AF52DE");
    public static readonly Brush Brown = B("#A2845E");
    public static readonly Brush Indigo = B("#5856D6");
    public static readonly Brush Teal = B("#30B0C7");
    public static readonly Brush Red = B("#FF3B30");
    public static readonly Brush Blue = B("#007AFF");
    public static readonly Brush Orange = B("#FF9500");
}

/// <summary>Explorer-style "natural" ordering (file2 before file10).</summary>
internal static class Natural
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string a, string b);

    public static int Compare(string a, string b) => StrCmpLogicalW(a, b);
}
