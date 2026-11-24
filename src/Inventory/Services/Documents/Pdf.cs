using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Inventory.Services.Documents;

/// <summary>Общее для PDF (PDFsharp/MigraDoc, лицензия MIT — бесплатно для любого использования).</summary>
internal static class Pdf
{
    // Arial есть в любой Windows и содержит кириллицу.
    public const string Font = "Arial";
    private static bool _initialized;
    private static readonly object Sync = new();

    public static void EnsureFonts()
    {
        lock (Sync)
        {
            if (_initialized) return;
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            _initialized = true;
        }
    }

    public static Document NewDocument(double fontSize = 11)
    {
        EnsureFonts();
        var doc = new Document();
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = fontSize;
        return doc;
    }

    public static void Save(Document doc, string path)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    public static Unit Mm(double mm) => Unit.FromMillimeter(mm);
}
