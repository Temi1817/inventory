using Inventory.Models;
using Inventory.Services.Recognition;

namespace Inventory.Tests;

public sealed class RecognitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-tests-" + Guid.NewGuid().ToString("N"));

    // Лог пишется в AppPaths — уводим его во временную папку, чтобы не засорять настоящий лог программы.
    public RecognitionTests() => Inventory.Infrastructure.AppPaths.UseRoot(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* временная папка */ }
    }

    [Fact]
    public void Parser_ReadsHeaderAndItems()
    {
        string[] rows =
        [
            "Организация (индивидуальный предприниматель) - отправитель  ТОО \"Технодом Плюс\"  БИН 123456789012",
            "Организация - получатель  ТОО \"Альфа\"  БИН 210987654321",
            "НАКЛАДНАЯ НА ОТПУСК ЗАПАСОВ НА СТОРОНУ  Номер документа № ТД-457  Дата составления 05.04.2023",
            "№ п/п  Наименование  Ед. изм.  Количество  Цена за единицу  Сумма с НДС",
            "1  Ноутбук HP ProBook 450 G9  шт  2  400 000,00  800 000,00",
            "2  Мышь Logitech M185  шт  4  5 500,00  22 000,00",
            "3  Кабель UTP cat.5e  м  305  100,00  30 500,00",
            "Итого  311  852 500,00",
        ];
        var r = OcrInvoiceParser.Parse(rows);

        Assert.Equal("ТД-457", r.Number);
        Assert.Equal("2023-04-05", r.Date);
        Assert.Equal("123456789012", r.SupplierBin);
        Assert.Equal("ТОО \"Технодом Плюс\"", r.Supplier);
        Assert.Equal(852500m, r.Total);
        Assert.Equal(3, r.Items.Count);
        Assert.Equal(("Ноутбук HP ProBook 450 G9", 2m, 400000m, "шт"), (r.Items[0].Name, r.Items[0].Quantity, r.Items[0].Price, r.Items[0].Unit));
        Assert.Equal((305m, 100m, "м"), (r.Items[2].Quantity, r.Items[2].Price, r.Items[2].Unit));
    }

    [Fact]
    public void Parser_Helpers()
    {
        Assert.Equal([1250000.5m, 3m], OcrInvoiceParser.Numbers("1 250 000,50  3"));
        Assert.Equal(new DateTime(2022, 3, 7), OcrInvoiceParser.FindDate("от 7 марта 2022 г."));
        Assert.Equal(new DateTime(2022, 5, 1), OcrInvoiceParser.FindDate("1 мая 2022"));
        Assert.Equal((3m, 150m), OcrInvoiceParser.GuessQtyPrice([12345m, 3m, 150m, 450m]));
        Assert.Null(OcrInvoiceParser.ParseItemRow("Итого  5  1 000,00"));
        Assert.Null(OcrInvoiceParser.FindNumber(["№ п/п  Наименование"]));
        Assert.Equal("3  Кабель о кабеле  м  305  30 500,00", OcrInvoiceParser.FixOcrDigits("з  Кабель о кабеле  м  305  зо 500,оо"));
        Assert.Equal("Накладная № ТД-1", OcrInvoiceParser.FixOcrDigits("Накладная N2 ТД-1"));
    }

    [Fact]
    public void Ollama_MergesPages()
    {
        var merged = OllamaRecognizer.Merge(
        [
            new RecognizedInvoice { Number = "1", Date = "2023-01-01", Items = [new RecognizedLine { Name = "A" }] },
            new RecognizedInvoice { Total = 100, Items = [new RecognizedLine { Name = "B" }] },
        ]);
        Assert.Equal("1", merged.Number);
        Assert.Equal(100m, merged.Total);
        Assert.Equal(["A", "B"], merged.Items.Select(i => i.Name));
    }

    [Fact]
    public void GroupRows_SplitsColumnsByGap()
    {
        var words = new List<WindowsOcrRecognizer.OcrWord>
        {
            new("Мышь", 100, 100, 60, 20), new("Logitech", 165, 101, 90, 20),
            new("4", 500, 99, 10, 20), new("Монитор", 100, 140, 80, 20),
        };
        var rows = WindowsOcrRecognizer.GroupRows(words);
        Assert.Equal(["Мышь Logitech  4", "Монитор"], rows);
    }

    /// <summary>Настоящее распознавание Windows OCR на нарисованной накладной.</summary>
    [Fact]
    public async Task WindowsOcr_RecognizesRenderedInvoice()
    {
        if (!WindowsOcrRecognizer.IsAvailable) return;
        var path = SampleInvoice.Render(Path.Combine(Path.GetTempPath(), $"ocr-{Guid.NewGuid():N}.png"));
        try
        {
            var r = await new WindowsOcrRecognizer().RecognizeAsync(path);
            Assert.Equal("123456789012", r.SupplierBin);
            Assert.Equal("2023-04-05", r.Date);
            Assert.Contains(r.Items, i => i.Name!.Contains("Ноутбук") && i.Quantity == 2 && i.Price == 400000m);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>Рисует простую накладную (для тестов распознавания).</summary>
internal static class SampleInvoice
{
    public static string Render(string path)
    {
        using var bmp = new Bitmap(2000, 1400);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var f = new Font("Arial", 22);
        using var b = new Font("Arial", 26, FontStyle.Bold);
        g.DrawString("Отправитель: ТОО \"Технодом Плюс\"   БИН 123456789012", f, Brushes.Black, 80, 60);
        g.DrawString("Получатель: ТОО \"Альфа\"", f, Brushes.Black, 80, 110);
        g.DrawString("НАКЛАДНАЯ НА ОТПУСК ЗАПАСОВ № ТД-457 от 05.04.2023", b, Brushes.Black, 80, 190);
        int[] x = [80, 160, 900, 1040, 1240, 1560];
        string[][] table =
        [
            ["№", "Наименование", "Ед.", "Кол-во", "Цена", "Сумма"],
            ["1", "Ноутбук HP ProBook 450", "шт", "2", "400 000,00", "800 000,00"],
            ["2", "Мышь Logitech M185", "шт", "4", "5 500,00", "22 000,00"],
            ["3", "Кабель UTP", "м", "305", "100,00", "30 500,00"],
        ];
        for (var r = 0; r < table.Length; r++)
            for (var c = 0; c < x.Length; c++)
                g.DrawString(table[r][c], f, Brushes.Black, x[c], 320 + r * 70);
        g.DrawString("Итого: 852 500,00", b, Brushes.Black, 1240, 320 + table.Length * 70 + 30);
        bmp.Save(path);
        return path;
    }
}
