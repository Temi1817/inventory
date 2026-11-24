using ClosedXML.Excel;
using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services.Documents;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Inventory.Tests;

public sealed class DocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Preview = Path.Combine(Path.GetTempPath(), "claude");

    public DocumentTests()
    {
        AppPaths.UseRoot(_root);
        AppPaths.EnsureCreated();
        Db.Init(AppPaths.Database);
        Migrations.Run();
        Directory.CreateDirectory(Preview);
    }

    public void Dispose()
    {
        Db.ReleaseAll();
        try { Directory.Delete(_root, true); } catch { /* временная папка */ }
    }

    private static List<Item> SampleItems(int n) => Enumerable.Range(1, n).Select(i => new Item
    {
        Name = i % 3 == 0 ? "Монитор Dell P2422H 23.8\" IPS, чёрный" : $"Ноутбук Lenovo ThinkPad E14 Gen {i}",
        InventoryNumber = $"00012{i:D2}", SerialNumber = $"PF3AB{i:D3}", CompanyName = "ТОО Альфа",
    }).ToList();

    /// <summary>Отрендерить страницы PDF в PNG (для просмотра) и вернуть число страниц.</summary>
    private static async Task<uint> RenderPages(string pdfPath, string prefix)
    {
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(pdfPath));
        using var page = pdf.GetPage(0);
        using var ras = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = 1000 });
        using var s = ras.AsStreamForRead();
        using var img = Image.FromStream(s);
        img.Save(Path.Combine(Preview, prefix + ".png"));
        return pdf.PageCount;
    }

    [Fact]
    public async Task Act_IsValidPdf_WithCyrillic()
    {
        var path = Path.Combine(_root, "act.pdf");
        var emp = new Employee { FullName = "Иванов Иван Иванович", Position = "Инженер", Department = "ИТ" };
        ActPdf.Generate(path, emp, SampleItems(5), new DateTime(2026, 11, 23), "ТОО «Альфа»", "Петров П.П.");
        Assert.Equal(1u, await RenderPages(path, "s5-act"));
        Assert.Equal("Иванов И.И.", ActPdf.ShortName("Иванов Иван Иванович"));
        Assert.Equal("Petrov", ActPdf.ShortName("Petrov"));
    }

    [Fact]
    public async Task QrStickers_GridAndSingle()
    {
        var items = SampleItems(30);
        items.Add(new Item { Name = "Без номера" });

        var grid = Path.Combine(_root, "grid.pdf");
        Assert.Equal(30, QrStickers.Generate(grid, items, QrStickers.Layout.A4Grid));
        Assert.Equal(2u, await RenderPages(grid, "s5-qr-grid"));   // 24 на листе → 2 листа

        var single = Path.Combine(_root, "single.pdf");
        Assert.Equal(6, QrStickers.Generate(single, items.Take(3), QrStickers.Layout.Single, copies: 2));
        Assert.Equal(6u, await RenderPages(single, "s5-qr-single"));

        // QR реально нарисован (а не заглушка «Image has no valid type»): слева много чёрных пикселей.
        using var bmp = new Bitmap(Path.Combine(Preview, "s5-qr-single.png"));
        int dark = 0, total = 0;
        for (var x = 0; x < bmp.Width * 45 / 100; x += 2)
            for (var y = 0; y < bmp.Height; y += 2, total++)
                if (bmp.GetPixel(x, y).GetBrightness() < 0.2f) dark++;
        Assert.True(dark > total * 0.15, $"QR не отрисован: тёмных пикселей {dark} из {total}");

        Assert.Throws<ArgumentException>(() => QrStickers.Generate(grid, [new Item { Name = "x" }], QrStickers.Layout.A4Grid));
    }

    [Fact]
    public void Excel_ItemsExport_ReadsBack()
    {
        var path = Path.Combine(_root, "items.xlsx");
        var items = SampleItems(3);
        items[0].Price = 420000.5m;
        items[0].ArrivalDate = new DateTime(2024, 5, 2);
        ExcelExport.Items(path, "Склад: На складе", items);

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet(1);
        Assert.Equal("Склад: На складе", ws.Cell(1, 1).GetString());
        Assert.Equal("Инв. номер", ws.Cell(4, 1).GetString());
        Assert.Equal("0001201", ws.Cell(5, 1).GetString());
        Assert.Equal(420000.5, ws.Cell(5, 8).GetDouble());
        Assert.Equal(new DateTime(2024, 5, 2), ws.Cell(5, 9).GetDateTime());
        Assert.Equal(3, ws.Tables.First().DataRange.RowCount());
    }

    [Fact]
    public void Consumables_IncomeIssue_NoNegativeStock()
    {
        var emp = EmployeeRepo.Insert(new Employee { FullName = "Петров" });
        var id = ConsumableRepo.Insert(new Consumable { Name = "Бумага А4", Unit = "пачка", Quantity = 10, MinQuantity = 5 });
        Assert.Throws<ArgumentException>(() => ConsumableRepo.Insert(new Consumable { Name = "бумага а4" }));

        ConsumableRepo.Move(id, -7, DateTime.Today, emp, "в бухгалтерию");
        Assert.Equal(3m, ConsumableRepo.Get(id)!.Quantity);
        Assert.Throws<ArgumentException>(() => ConsumableRepo.Move(id, -4, DateTime.Today, null, null));
        Assert.Equal(3m, ConsumableRepo.Get(id)!.Quantity);

        ConsumableRepo.Move(id, 20, DateTime.Today, null, "закупка");
        var moves = ConsumableRepo.Moves(id);
        Assert.Equal(3, moves.Count);   // начальный остаток + выдача + приход
        var issue = moves.Single(m => m.Kind == "Выдача");
        Assert.Equal((7m, "Петров"), (issue.Amount, issue.EmployeeName));

        // Ниже минимума — первым в списке.
        ConsumableRepo.Insert(new Consumable { Name = "Ааа ручки", Quantity = 1, MinQuantity = 10 });
        Assert.Equal("Ааа ручки", ConsumableRepo.List()[0].Name);

        ConsumableRepo.Delete(id);
        Assert.Null(ConsumableRepo.Get(id));
    }
}
