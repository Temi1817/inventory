using System.Drawing.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Inventory.Services.Recognition;

/// <summary>Файл накладной → картинки страниц в JPEG (фото повёрнуто по EXIF, PDF отрендерен постранично).</summary>
public static class PageImages
{
    public const int MaxPdfPages = 6;

    public static async Task<List<byte[]>> LoadAsync(string path, int maxSide, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new RecognitionException("Файл не найден: " + path);
        try { return await LoadCoreAsync(path, maxSide, ct); }
        catch (Exception ex) when (ex is not (RecognitionException or OperationCanceledException))
        {
            Infrastructure.Log.Error("Не удалось открыть файл накладной " + path, ex);
            throw new RecognitionException("Файл не открывается — повреждён или это не фото/PDF.", ex);
        }
    }

    private static async Task<List<byte[]>> LoadCoreAsync(string path, int maxSide, CancellationToken ct)
    {
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return [await Task.Run(() => PrepareImage(path, maxSide), ct)];

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        var pages = new List<byte[]>();
        for (uint i = 0; i < Math.Min(pdf.PageCount, (uint)MaxPdfPages); i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = pdf.GetPage(i);
            var landscape = page.Size.Width > page.Size.Height;
            var options = landscape ? new PdfPageRenderOptions { DestinationWidth = (uint)maxSide } : new PdfPageRenderOptions { DestinationHeight = (uint)maxSide };
            using var ras = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ras, options);
            using var stream = ras.AsStreamForRead();
            using var img = Image.FromStream(stream);
            pages.Add(EncodeJpeg(img, img.Width, img.Height));
        }
        return pages;
    }

    /// <summary>Повернуть по EXIF (фото с телефона), уменьшить до maxSide, сохранить в JPEG.</summary>
    public static byte[] PrepareImage(string path, int maxSide)
    {
        using var stream = File.OpenRead(path);
        using var original = Image.FromStream(stream);
        ApplyExifOrientation(original);
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(original.Width, original.Height));
        return EncodeJpeg(original, Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale)));
    }

    private static byte[] EncodeJpeg(Image source, int w, int h)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(Color.White);
            g.DrawImage(source, 0, 0, w, h);
        }
        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
        using var ms = new MemoryStream();
        bmp.Save(ms, encoder, parameters);
        return ms.ToArray();
    }

    public static void ApplyExifOrientation(Image img)
    {
        const int orientationTag = 0x0112;
        if (!img.PropertyIdList.Contains(orientationTag)) return;
        var value = img.GetPropertyItem(orientationTag)?.Value;
        if (value == null || value.Length == 0) return;
        var flip = value[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(flip);
        img.RemovePropertyItem(orientationTag);
    }
}
