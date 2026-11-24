using System.Text.Json.Nodes;
using Dapper;
using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services;
using Inventory.Services.Claude;

namespace Inventory.Tests;

public sealed class InvoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-tests-" + Guid.NewGuid().ToString("N"));

    public InvoiceTests()
    {
        AppPaths.UseRoot(_root);
        AppPaths.EnsureCreated();
        Db.Init(AppPaths.Database);
        Migrations.Run();
    }

    public void Dispose()
    {
        Db.ReleaseAll();
        try { Directory.Delete(_root, true); } catch { /* временная папка */ }
    }

    private InvoiceDraft Draft(string number = "123") => new()
    {
        Number = number, Date = new DateTime(2023, 4, 5), SupplierName = "ТОО Технодом", SupplierBin = "БИН 123 456 789 012",
        CompanyName = "ТОО Бета", Total = 900000,
        Lines =
        [
            new InvoiceLine { Name = "Ноутбук HP", Category = "Ноутбук", Quantity = 2, Price = 400000 },
            new InvoiceLine { Name = "Кабель UTP", Quantity = 305, Unit = "м", Price = 100, ToConsumables = true },
        ],
    };

    [Fact]
    public void Accept_CreatesInvoiceSupplierItemsHistoryAndConsumables()
    {
        var file = Path.Combine(_root, "scan.pdf");
        File.WriteAllText(file, "%PDF-1.4 fake");
        var d = Draft();
        d.SourceFile = file;

        var id = InvoiceRepo.Accept(d);

        var inv = InvoiceRepo.Get(id)!;
        Assert.Equal("123", inv.Number);
        Assert.Equal(new DateTime(2023, 4, 5), inv.Date);
        Assert.Equal("ТОО Технодом", inv.SupplierName);
        Assert.Equal("123456789012", inv.SupplierBin);
        Assert.Equal(2, inv.ItemCount);
        Assert.True(File.Exists(AppPaths.ToAbsolute(inv.FilePath!)));

        var items = ItemRepo.List(new ItemFilter { InvoiceId = id });
        Assert.All(items, i =>
        {
            Assert.Equal(ItemStatus.InStock, i.Status);
            Assert.Equal(400000m, i.Price);
            Assert.Equal("ТОО Бета", i.CompanyName);
            Assert.Equal(new DateTime(2023, 4, 5), i.ArrivalDate);   // дата накладной, не сегодняшняя
            Assert.Equal("123", i.InvoiceNumber);
        });

        using var c = Db.Open();
        Assert.Equal(305m, c.ExecuteScalar<decimal>("SELECT quantity FROM consumables WHERE name = 'Кабель UTP'"));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM consumable_moves"));
    }

    [Fact]
    public void Accept_SecondTime_FindsDuplicate_AndReusesSupplierAndConsumable()
    {
        InvoiceRepo.Accept(Draft());
        Assert.NotNull(InvoiceRepo.FindDuplicate("123", "другое имя", "123456789012")); // поиск по БИН
        Assert.NotNull(InvoiceRepo.FindDuplicate("123", "тоо технодом", null));         // по имени
        Assert.Null(InvoiceRepo.FindDuplicate("124", "ТОО Технодом", null));

        InvoiceRepo.Accept(Draft("124"));
        using var c = Db.Open();
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM suppliers"));
        Assert.Equal(610m, c.ExecuteScalar<decimal>("SELECT quantity FROM consumables"));
    }

    [Fact]
    public void Accept_IssuedToEmployee_ShowsOnEmployeePage()
    {
        var emp = EmployeeRepo.Insert(new Employee { FullName = "Иванов" });
        var d = Draft();
        d.ItemStatus = ItemStatus.Issued;
        d.EmployeeId = emp;
        InvoiceRepo.Accept(d);

        var mine = ItemRepo.List(new ItemFilter { EmployeeId = emp });
        Assert.Equal(2, mine.Count);
        Assert.All(mine, i => Assert.Equal(new DateTime(2023, 4, 5), i.IssuedDate));
    }

    [Fact]
    public void UpdateHeader_MovesArrivalAndIssueDates()
    {
        var emp = EmployeeRepo.Insert(new Employee { FullName = "Иван" });
        var d = Draft();
        d.Lines[0].EmployeeId = emp;                          // ноутбуки выданы сразу
        var id = InvoiceRepo.Accept(d);
        var other = ItemRepo.Insert(new Item { Name = "Чужая вещь" }, new DateTime(2023, 4, 5));

        CompanyRepo.Add("ТОО Альфа");
        InvoiceRepo.UpdateHeader(id, "ТН-5", new DateTime(2026, 11, 13), "исправлено", "тоо альфа");

        var inv = InvoiceRepo.Get(id)!;
        Assert.Equal(("ТН-5", new DateTime(2026, 11, 13), "исправлено"), (inv.Number, inv.Date!.Value, inv.Notes));
        Assert.Equal("ТОО Альфа", inv.CompanyName);                                   // существующая, без дубля
        var items = ItemRepo.List(new ItemFilter { InvoiceId = id });
        Assert.All(items, i => Assert.Equal("ТОО Альфа", i.CompanyName));
        Assert.Null(ItemRepo.Get(other)!.CompanyName);
        Assert.All(items, i => Assert.Equal(new DateTime(2026, 11, 13), i.ArrivalDate));
        Assert.All(items, i => Assert.Equal(new DateTime(2026, 11, 13), i.IssuedDate));
        Assert.Equal("Накладная № ТН-5, выдано сразу", HistoryRepo.ForItem(items[0].Id).Single().Comment);
        Assert.Equal(new DateTime(2023, 4, 5), ItemRepo.Get(other)!.ArrivalDate);       // другие вещи не тронуты

        using var c = Db.Open();
        Assert.Equal(new DateTime(2026, 11, 13), c.ExecuteScalar<DateTime>("SELECT date FROM consumable_moves WHERE invoice_id = @id", new { id }));
    }

    [Fact]
    public void Validate_RejectsBadLines_AndNothingIsWritten()
    {
        var d = Draft();
        d.Lines[0].Quantity = 1.5m;
        Assert.Contains("дробное", InvoiceRepo.Validate(d));
        Assert.Throws<ArgumentException>(() => InvoiceRepo.Accept(d));

        d = Draft();
        d.ItemStatus = ItemStatus.Issued;
        Assert.Contains("сотрудника", InvoiceRepo.Validate(d));

        Assert.Empty(InvoiceRepo.List());
    }

    [Fact]
    public void Recognizer_Parse_And_DraftSuggestions()
    {
        const string json = """
            {"number":"А-17","date":"2022-11-30","supplier":"ТОО Альфа","supplier_bin":"987654321098","total":125500.5,
             "items":[{"name":"Монитор 24\"","quantity":2,"unit":"шт","price":60000,"category":"Монитор"},
                      {"name":"Картридж 85A","quantity":1,"unit":"шт","price":5500.5,"category":"Картриджи"},
                      {"name":null,"quantity":null,"unit":null,"price":null,"category":null}]}
            """;
        var r = InvoiceRecognizer.Parse(json);
        Assert.Equal(2, r.Items.Count);

        var d = InvoiceDrafts.FromRecognized(r, null);
        Assert.Equal(new DateTime(2022, 11, 30), d.Date);
        Assert.Equal("987654321098", d.SupplierBin);
        Assert.False(d.Lines[0].ToConsumables);
        Assert.True(d.Lines[1].ToConsumables);
        Assert.True(InvoiceDrafts.SuggestConsumable(new InvoiceLine { Name = "x", Quantity = 1, Unit = "м" }));

        Assert.Equal(new DateTime(2022, 1, 2), InvoiceRecognizer.ParseDate("02.01.2022"));
        Assert.Null(InvoiceRecognizer.ParseDate("вчера"));
    }

    [Fact]
    public void ExtractText_SkipsThinking_AndReportsRefusalAndTruncation()
    {
        const string ok = """{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":""},{"type":"text","text":"{\"a\":1}"}]}""";
        Assert.Equal("{\"a\":1}", ClaudeClient.ExtractText(ok));
        Assert.Throws<ClaudeException>(() => ClaudeClient.ExtractText("""{"stop_reason":"refusal","content":[]}"""));
        Assert.Throws<ClaudeException>(() => ClaudeClient.ExtractText("""{"stop_reason":"max_tokens","content":[]}"""));
        Assert.Equal("", ClaudeClient.ExtractText("""{"stop_reason":"max_tokens","content":[]}""", strict: false));
    }

    [Fact]
    public void BuildFileBlock_ImageIsJpeg_PdfIsDocument()
    {
        var png = Path.Combine(_root, "a.png");
        using (var bmp = new Bitmap(3000, 1000)) bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var img = InvoiceRecognizer.BuildFileBlock(png);
        Assert.Equal("image", img["type"]!.GetValue<string>());
        Assert.Equal("image/jpeg", img["source"]!["media_type"]!.GetValue<string>());
        using (var ms = new MemoryStream(Convert.FromBase64String(img["source"]!["data"]!.GetValue<string>())))
        using (var back = Image.FromStream(ms))
            Assert.Equal(2400, back.Width); // уменьшено до 2400 по длинной стороне

        var pdf = Path.Combine(_root, "a.pdf");
        File.WriteAllText(pdf, "%PDF-1.4");
        var doc = InvoiceRecognizer.BuildFileBlock(pdf);
        Assert.Equal("document", doc["type"]!.GetValue<string>());
        Assert.Equal("application/pdf", doc["source"]!["media_type"]!.GetValue<string>());

        var schema = InvoiceRecognizer.Schema();
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(6, schema["required"]!.AsArray().Count);
    }

    [Fact]
    public void Settings_ApiKey_IsEncryptedOnDisk()
    {
        var s = new AppSettings();
        s.SetApiKey("sk-ant-secret-123");
        s.Save();
        Assert.DoesNotContain("sk-ant-secret-123", File.ReadAllText(AppPaths.Settings));
        Assert.Equal("sk-ant-secret-123", AppSettings.Load().GetApiKey());
        Assert.Equal(AppSettings.DefaultModel, AppSettings.Load().Model);
    }
}
