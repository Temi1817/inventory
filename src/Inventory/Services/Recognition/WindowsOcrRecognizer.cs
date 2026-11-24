using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Inventory.Infrastructure;
using Inventory.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Inventory.Services.Recognition;

/// <summary>
/// Встроенное в Windows распознавание текста (бесплатно, без интернета).
/// Текст раскладывается по строкам, дальше OcrInvoiceParser ищет поля по шаблонам.
/// </summary>
public sealed class WindowsOcrRecognizer : IInvoiceRecognizer
{
    public string Name => RecognitionEngine.Title(RecognitionEngine.WindowsOcr);

    public static bool IsAvailable => CreateEngine() != null;

    private static OcrEngine? CreateEngine() =>
        OcrEngine.TryCreateFromLanguage(new Language("ru")) ?? OcrEngine.TryCreateFromUserProfileLanguages();

    public async Task<RecognizedInvoice> RecognizeAsync(string filePath, CancellationToken ct = default)
    {
        var engine = CreateEngine() ?? throw new RecognitionException("В Windows нет распознавания текста для русского языка.");
        var side = (int)Math.Min(OcrEngine.MaxImageDimension, 3000u);
        var pages = await PageImages.LoadAsync(filePath, side, ct);
        var rows = new List<string>();
        foreach (var jpeg in pages)
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(await OcrRowsAsync(engine, jpeg));
        }
        Log.Info($"Windows OCR: {rows.Count} строк из {pages.Count} стр.");
        return OcrInvoiceParser.Parse(rows);
    }

    private static async Task<List<string>> OcrRowsAsync(OcrEngine engine, byte[] jpeg)
    {
        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(jpeg.AsBuffer());
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        var words = result.Lines.SelectMany(l => l.Words)
            .Select(w => new OcrWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
            .ToList();
        return GroupRows(words);
    }

    public readonly record struct OcrWord(string Text, double X, double Y, double W, double H)
    {
        public double CenterY => Y + H / 2;
    }

    /// <summary>
    /// Слова → строки таблицы: группируем по вертикали, внутри строки — слева направо.
    /// Большой промежуток между словами (другая колонка) помечаем двумя пробелами.
    /// </summary>
    internal static List<string> GroupRows(List<OcrWord> words)
    {
        var rows = new List<string>();
        if (words.Count == 0) return rows;
        var medianH = words.Select(w => w.H).OrderBy(h => h).ElementAt(words.Count / 2);

        var groups = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(w => w.CenterY))
        {
            var last = groups.LastOrDefault();
            if (last != null && Math.Abs(last.Average(x => x.CenterY) - w.CenterY) < medianH * 0.55) last.Add(w);
            else groups.Add([w]);
        }

        foreach (var g in groups)
        {
            var sb = new StringBuilder();
            OcrWord? prev = null;
            foreach (var w in g.OrderBy(x => x.X))
            {
                if (prev is { } p)
                {
                    var gap = w.X - (p.X + p.W);
                    sb.Append(gap > medianH * 1.2 ? "  " : " ");
                }
                sb.Append(w.Text);
                prev = w;
            }
            rows.Add(sb.ToString());
        }
        return rows;
    }
}
