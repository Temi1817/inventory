using Inventory.Models;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using QRCoder;

namespace Inventory.Services.Documents;

/// <summary>QR-наклейки: инв. номер + наименование + QR (в QR — инв. номер).</summary>
public static class QrStickers
{
    public enum Layout
    {
        /// <summary>Лист A4 с наклейками 70×37 мм (3×8 = 24 на листе).</summary>
        A4Grid,
        /// <summary>Отдельная страница на каждую наклейку 58×40 мм (для принтера этикеток).</summary>
        Single,
    }

    public static byte[] QrPng(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12);
        // QRCoder отдаёт 1-битный PNG с палитрой — PDFsharp его не понимает. Перекодируем в обычный 24-битный.
        using var src = Image.FromStream(new MemoryStream(png));
        using var rgb = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(rgb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImage(src, 0, 0, src.Width, src.Height);
        }
        using var ms = new MemoryStream();
        rgb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Создать PDF. Вещи без инв. номера пропускаются (возвращается, сколько вошло).</summary>
    public static int Generate(string path, IEnumerable<Item> items, Layout layout, int copies = 1)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i.InventoryNumber))
            .SelectMany(i => Enumerable.Repeat(i, Math.Max(1, copies))).ToList();
        if (list.Count == 0) throw new ArgumentException("Ни у одной выбранной вещи нет инвентарного номера.");
        var qr = list.Select(i => i.InventoryNumber!).Distinct()
            .ToDictionary(n => n, n => "base64:" + Convert.ToBase64String(QrPng(n)));

        var doc = Pdf.NewDocument();
        if (layout == Layout.Single) SingleStickers(doc, list, qr);
        else GridSheet(doc, list, qr);
        Pdf.Save(doc, path);
        return list.Count;
    }

    private static void SingleStickers(Document doc, List<Item> list, Dictionary<string, string> qr)
    {
        foreach (var item in list)
        {
            var sec = doc.AddSection();
            sec.PageSetup = doc.DefaultPageSetup.Clone();
            sec.PageSetup.PageWidth = Pdf.Mm(58);
            sec.PageSetup.PageHeight = Pdf.Mm(40);
            sec.PageSetup.TopMargin = sec.PageSetup.BottomMargin = Pdf.Mm(2);
            sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = Pdf.Mm(2);
            var table = sec.AddTable();
            table.AddColumn(Pdf.Mm(27));
            table.AddColumn(Pdf.Mm(27));
            var row = table.AddRow();
            row.Height = Pdf.Mm(35.5);
            row.HeightRule = RowHeightRule.Exactly;
            Fill(row.Cells[0], row.Cells[1], item, qr[item.InventoryNumber!], 25, 12, 7.5);
        }
    }

    private static void GridSheet(Document doc, List<Item> list, Dictionary<string, string> qr)
    {
        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.TopMargin = sec.PageSetup.BottomMargin = Pdf.Mm(0.5);
        sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = Pdf.Mm(0.5);

        // 3 наклейки в ряд, у каждой две колонки: QR и текст (вложенных таблиц MigraDoc не умеет).
        var table = sec.AddTable();
        for (var i = 0; i < 3; i++)
        {
            table.AddColumn(Pdf.Mm(32));
            table.AddColumn(Pdf.Mm(37.6));
        }
        for (var start = 0; start < list.Count; start += 3)
        {
            var row = table.AddRow();
            row.Height = Pdf.Mm(36.9);
            row.HeightRule = RowHeightRule.Exactly;
            for (var k = 0; k < 3 && start + k < list.Count; k++)
            {
                var item = list[start + k];
                Fill(row.Cells[k * 2], row.Cells[k * 2 + 1], item, qr[item.InventoryNumber!], 27, 12, 8);
            }
        }
    }

    private static void Fill(Cell qrCell, Cell textCell, Item item, string qrImage, double qrMm, double numberPt, double namePt)
    {
        qrCell.VerticalAlignment = VerticalAlignment.Center;
        qrCell.Format.Alignment = ParagraphAlignment.Center;
        var image = qrCell.AddParagraph().AddImage(qrImage);
        image.Width = Pdf.Mm(qrMm);
        image.LockAspectRatio = true;

        textCell.VerticalAlignment = VerticalAlignment.Center;
        var number = textCell.AddParagraph(item.InventoryNumber!);
        number.Format.Font.Size = numberPt;
        number.Format.Font.Bold = true;
        var name = textCell.AddParagraph(Shorten(item.Name, 70));
        name.Format.Font.Size = namePt;
        name.Format.SpaceBefore = Pdf.Mm(1);
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}
