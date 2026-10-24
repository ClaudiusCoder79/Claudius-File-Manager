using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClaudiusFileManager;

/// <summary>Renders PDF pages with the PDF engine built into Windows 10/11 (no extra software needed).</summary>
public sealed class PdfRender : IDisposable
{
    private readonly PdfDocument _doc;
    public uint PageCount => _doc.PageCount;

    private PdfRender(PdfDocument doc) => _doc = doc;

    public static async Task<PdfRender> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var doc = await PdfDocument.LoadFromFileAsync(file);
        return new PdfRender(doc);
    }

    /// <summary>Renders one page (0-based) to a bitmap about <paramref name="width"/> pixels wide.</summary>
    public async Task<BitmapImage> RenderAsync(uint index, uint width)
    {
        using var page = _doc.GetPage(index);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width });
        stream.Seek(0);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = stream.AsStream();
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public void Dispose() { /* PdfDocument has no Dispose; pages are disposed after rendering */ }
}
