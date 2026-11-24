using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services;
using Inventory.Services.Recognition;

namespace Inventory.Tests;

public sealed class QueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;

    public QueueTests()
    {
        AppPaths.UseRoot(_root);
        AppPaths.EnsureCreated();
        Db.Init(AppPaths.Database);
        Migrations.Run();
        _src = Path.Combine(_root, "src");
        Directory.CreateDirectory(_src);
    }

    public void Dispose()
    {
        QueueProcessor.Instance.RecognizerFactory = null;
        Db.ReleaseAll();
        try { Directory.Delete(_root, true); } catch { /* временная папка */ }
    }

    private string MakeFile(string name, string content)
    {
        var path = Path.Combine(_src, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Поддельный распознаватель: «bad» в имени файла → ошибка.</summary>
    private sealed class FakeRecognizer : IInvoiceRecognizer
    {
        public string Name => "тест";
        public Task<RecognizedInvoice> RecognizeAsync(string filePath, CancellationToken ct = default)
        {
            if (File.ReadAllText(filePath).Contains("bad")) throw new RecognitionException("Не читается");
            return Task.FromResult(new RecognizedInvoice
            {
                Number = Path.GetFileNameWithoutExtension(filePath)[^4..], Date = "2021-06-15", Supplier = "ТОО Бета",
                Items = [new RecognizedLine { Name = "Принтер", Quantity = 1, Price = 90000, Unit = "шт" }],
            });
        }
    }

    [Fact]
    public void Add_CopiesFiles_AndSkipsDuplicatesByContent()
    {
        var a = MakeFile("a.pdf", "A");
        var copyOfA = MakeFile("a-copy.pdf", "A");
        var b = MakeFile("b.pdf", "B");

        Assert.Equal((2, 1), ImportQueueRepo.Add([a, copyOfA, b]));
        Assert.Equal((0, 2), ImportQueueRepo.Add([a, b]));   // повторная загрузка той же папки

        var list = ImportQueueRepo.List(false);
        Assert.All(list, e => Assert.True(File.Exists(e.AbsolutePath)));
        Assert.All(list, e => Assert.Equal(QueueStatus.Pending, e.Status));
    }

    [Fact]
    public async Task Processor_RecognizesInBackground_AndQueueSurvivesRestart()
    {
        ImportQueueRepo.Add([MakeFile("good1.pdf", "1"), MakeFile("bad.pdf", "bad"), MakeFile("good2.pdf", "2")]);

        // «Программу закрыли посреди распознавания»: зависший статус возвращается в очередь.
        var first = ImportQueueRepo.NextPending()!;
        ImportQueueRepo.SetProcessing(first.Id);
        Assert.Equal(1, ImportQueueRepo.ResetStuck());

        QueueProcessor.Instance.RecognizerFactory = _ => Task.FromResult<(IInvoiceRecognizer?, string?)>((new FakeRecognizer(), null));
        QueueProcessor.Instance.Start();
        for (var i = 0; i < 100 && QueueProcessor.Instance.IsRunning; i++) await Task.Delay(50);
        Assert.False(QueueProcessor.Instance.IsRunning);

        var c = ImportQueueRepo.Counts();
        Assert.Equal(2, c[QueueStatus.Done]);
        Assert.Equal(1, c[QueueStatus.Error]);
        var error = ImportQueueRepo.List(false).Single(e => e.Status == QueueStatus.Error);
        Assert.Equal("Не читается", error.Error);

        // Принять распознанную: дата прихода — из накладной, файл уходит из queue\ в files\.
        var done = ImportQueueRepo.List(false).First(e => e.Status == QueueStatus.Done);
        var draft = InvoiceDrafts.FromRecognized(ImportQueueRepo.GetResult(done)!, done.AbsolutePath);
        draft.CompanyName = "ТОО Бета";
        var invoiceId = InvoiceRepo.Accept(draft);
        ImportQueueRepo.MarkAccepted(done.Id, invoiceId);
        Assert.False(File.Exists(done.AbsolutePath));
        Assert.True(File.Exists(AppPaths.ToAbsolute(InvoiceRepo.Get(invoiceId)!.FilePath!)));
        Assert.Equal(new DateTime(2021, 6, 15), ItemRepo.List(new ItemFilter { InvoiceId = invoiceId })[0].ArrivalDate);

        // Ошибочный — на повторное распознавание; пропущенный исчезает из списка.
        ImportQueueRepo.Requeue(error.Id);
        Assert.Equal(QueueStatus.Pending, ImportQueueRepo.Get(error.Id)!.Status);
        ImportQueueRepo.MarkSkipped(error.Id);
        Assert.DoesNotContain(ImportQueueRepo.List(false), e => e.Id == error.Id);
        Assert.Contains(ImportQueueRepo.List(true), e => e.Id == error.Id);

        // Принятый файл повторно в очередь не попадает.
        Assert.Equal((0, 1), ImportQueueRepo.Add([MakeFile("again.pdf", done.OriginalName.Contains('1') ? "1" : "2")]));
    }
}
