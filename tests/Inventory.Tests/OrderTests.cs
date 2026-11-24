using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services;

namespace Inventory.Tests;

public sealed class OrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-tests-" + Guid.NewGuid().ToString("N"));
    private readonly long _ivanov, _petrov;

    public OrderTests()
    {
        AppPaths.UseRoot(_root);
        AppPaths.EnsureCreated();
        Db.Init(AppPaths.Database);
        Migrations.Run();
        _ivanov = EmployeeRepo.Insert(new Employee { FullName = "Иванов Иван" });
        _petrov = EmployeeRepo.Insert(new Employee { FullName = "Петров Пётр" });
    }

    public void Dispose()
    {
        Db.ReleaseAll();
        try { Directory.Delete(_root, true); } catch { /* временная папка */ }
    }

    private InvoiceDraft OrderDraft(string? file = null) => new()
    {
        Number = "СЧ-45", Date = new DateTime(2026, 11, 20), SupplierName = "ТОО Технодом", SupplierBin = "123456789012",
        CompanyName = "ТОО Бета", SourceFile = file,
        Lines =
        [
            new InvoiceLine { Name = "Ноутбук HP ProBook 450 G9", Category = "Ноутбук", Quantity = 1, Price = 400000, EmployeeId = _ivanov },
            new InvoiceLine { Name = "Монитор Dell P2422H", Category = "Монитор", Quantity = 2, Price = 90000, EmployeeId = _petrov },
            new InvoiceLine { Name = "Мышь Logitech M185", Quantity = 3, Price = 5000 },                       // на склад
            new InvoiceLine { Name = "Бумага А4", Quantity = 5, Unit = "пачка", Price = 2100, ToConsumables = true, EmployeeId = _ivanov },
        ],
    };

    [Fact]
    public void Order_SaveListPayEdit()
    {
        var file = Path.Combine(_root, "schet.pdf");
        File.WriteAllText(file, "%PDF");
        var id = OrderRepo.Save(OrderDraft(file));

        var o = OrderRepo.Get(id)!;
        Assert.Equal((OrderStatus.AwaitingPayment, 4L, 605500m), (o.Status, o.LineCount, o.Amount!.Value));
        Assert.Contains("Иванов Иван", o.Recipients);
        Assert.Contains("Петров Пётр", o.Recipients);
        Assert.True(File.Exists(AppPaths.ToAbsolute(o.FilePath!)));
        Assert.Empty(ItemRepo.List(new ItemFilter()));                    // на склад ничего не попало

        var lines = OrderRepo.Lines(id);
        Assert.Equal(_ivanov, lines[0].EmployeeId);
        Assert.Null(lines[3].EmployeeId);                                 // у расходника получатель не хранится
        Assert.Single(OrderRepo.OpenForEmployee(_petrov));

        OrderRepo.SetPaid(id, new DateTime(2026, 11, 21));
        Assert.Equal((OrderStatus.Paid, new DateTime(2026, 11, 21)), (OrderRepo.Get(id)!.Status, OrderRepo.Get(id)!.PaidAt!.Value));

        // Редактирование: файл не копируется заново, позиции заменяются.
        var draft = OrderRepo.ToDraft(id);
        draft.Lines.RemoveAt(2);
        Assert.Equal(id, OrderRepo.Save(draft, id));
        Assert.Equal(3, OrderRepo.Lines(id).Count);
        Assert.Single(Directory.GetFiles(AppPaths.Files, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Receive_ByInvoice_IssuesToRecipients_AndClosesOrder()
    {
        var orderId = OrderRepo.Save(OrderDraft());
        OrderRepo.SetPaid(orderId, DateTime.Today);

        // Распознанная накладная: названия чуть другие, порядок другой.
        var invoice = new InvoiceDraft
        {
            Number = "ТН-1001", Date = new DateTime(2026, 11, 25), SupplierName = "ТОО Технодом", CompanyName = "ТОО Бета",
            Lines =
            [
                new InvoiceLine { Name = "Монитор Dell P2422H 23.8\"", Quantity = 2, Price = 90000 },
                new InvoiceLine { Name = "Мышь беспроводная Logitech M185", Quantity = 3, Price = 5000 },
                new InvoiceLine { Name = "Ноутбук HP ProBook 450 G9 i5/16/512", Quantity = 1, Price = 400000 },
                new InvoiceLine { Name = "Бумага офисная А4", Quantity = 5, Unit = "пачка", Price = 2100 },
            ],
        };
        var matched = OrderMatcher.Apply(invoice.Lines, OrderRepo.Lines(orderId));
        Assert.Equal(11, matched);   // штук: 2 монитора + 3 мыши + 1 ноутбук + 5 пачек
        invoice.OrderId = orderId;
        var invoiceId = InvoiceRepo.Accept(invoice);

        var ivanov = ItemRepo.List(new ItemFilter { EmployeeId = _ivanov });
        Assert.Equal(["Ноутбук HP ProBook 450 G9 i5/16/512"], ivanov.Select(i => i.Name));
        Assert.Equal(ItemStatus.Issued, ivanov[0].Status);
        Assert.Equal(new DateTime(2026, 11, 25), ivanov[0].IssuedDate);
        Assert.Equal(2, ItemRepo.List(new ItemFilter { EmployeeId = _petrov }).Count);
        Assert.Equal(3, ItemRepo.List(new ItemFilter { Status = ItemStatus.InStock }).Count);   // мыши — на склад
        Assert.Equal(5m, ConsumableRepo.List().Single().Quantity);                              // бумага — в расходники

        var order = OrderRepo.Get(orderId)!;
        Assert.Equal((OrderStatus.Received, invoiceId, "ТН-1001"), (order.Status, order.InvoiceId!.Value, order.InvoiceNumber));
        Assert.Equal("СЧ-45", InvoiceRepo.Get(invoiceId)!.OrderNumber);
        Assert.Empty(OrderRepo.OpenForEmployee(_ivanov));
        Assert.Empty(OrderRepo.List());                                   // открытых заказов нет
        Assert.Single(OrderRepo.List(includeClosed: true));

        // Закрытый заказ нельзя закрыть ещё раз, изменить или удалить.
        invoice.Number = "ТН-1002";
        Assert.Throws<ArgumentException>(() => InvoiceRepo.Accept(invoice));
        Assert.Throws<ArgumentException>(() => OrderRepo.Save(OrderDraft(), orderId));
        Assert.Throws<ArgumentException>(() => OrderRepo.Delete(orderId));
    }

    [Fact]
    public void Receive_FiredRecipient_IsRejected()
    {
        var orderId = OrderRepo.Save(OrderDraft());
        EmployeeRepo.SetActive(_petrov, false);
        var draft = OrderRepo.ToDraft(orderId);
        draft.OrderId = orderId;
        var ex = Assert.Throws<ArgumentException>(() => InvoiceRepo.Accept(draft));
        Assert.Contains("Петров", ex.Message);
        Assert.Equal(OrderStatus.AwaitingPayment, OrderRepo.Get(orderId)!.Status);   // ничего не изменилось
        Assert.Empty(ItemRepo.List(new ItemFilter()));
    }

    [Fact]
    public void Cancel_Reopen_Delete()
    {
        var id = OrderRepo.Save(OrderDraft());
        OrderRepo.Cancel(id);
        Assert.Empty(OrderRepo.List());
        Assert.Throws<ArgumentException>(() => OrderRepo.SetPaid(id, DateTime.Today));
        OrderRepo.Reopen(id);
        Assert.Equal(OrderStatus.AwaitingPayment, OrderRepo.Get(id)!.Status);
        OrderRepo.Delete(id);
        Assert.Null(OrderRepo.Get(id));
    }

    /// <summary>Случай из жизни: в счёте «Клавиатура × 2» — одна Иванову, другая Петрову.</summary>
    [Fact]
    public void TwoKeyboards_ForTwoPeople_SplitInOrder_AndInInvoice()
    {
        var keyboard = new InvoiceLine { Name = "Клавиатура Attack Shark X98 [Механическая, беспроводная]", Category = "Периферия", Quantity = 2, Price = 24990 };
        Assert.True(InvoiceDrafts.CanSplit(keyboard));
        var parts = InvoiceDrafts.SplitByOne(keyboard);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal((1m, 24990m), (p.Quantity, p.Price!.Value)));
        parts[0].EmployeeId = _ivanov;
        parts[1].EmployeeId = _petrov;
        Assert.False(InvoiceDrafts.CanSplit(new InvoiceLine { Name = "Кабель", Quantity = 305, ToConsumables = true }));
        Assert.False(InvoiceDrafts.CanSplit(new InvoiceLine { Name = "x", Quantity = 1 }));

        var orderId = OrderRepo.Save(new InvoiceDraft { Number = "1001/1", SupplierName = "ТОО Поставщик", Lines = parts });

        // В накладной — снова одна строка × 2.
        var invoice = new InvoiceDraft
        {
            Number = "ТН-77", Date = DateTime.Today, SupplierName = "ТОО Поставщик", OrderId = orderId,
            Lines = [new InvoiceLine { Name = "Клавиатура Attack Shark X98", Quantity = 2, Price = 24990 }],
        };
        Assert.Equal(2, OrderMatcher.Apply(invoice.Lines, OrderRepo.Lines(orderId)));
        Assert.Equal(2, invoice.Lines.Count);
        InvoiceRepo.Accept(invoice);

        Assert.Single(ItemRepo.List(new ItemFilter { EmployeeId = _ivanov }));
        Assert.Single(ItemRepo.List(new ItemFilter { EmployeeId = _petrov }));
    }

    [Fact]
    public void Matcher_PartialQuantity_RestGoesToStock()
    {
        // В счёте 2 мыши Иванову, а в накладной пришло 3 — лишняя идёт без получателя (на склад).
        var order = new List<InvoiceLine> { new() { Name = "Мышь Logitech", Quantity = 2, EmployeeId = 7 } };
        var invoice = new List<InvoiceLine> { new() { Name = "Мышь Logitech M185", Quantity = 3 } };
        Assert.Equal(2, OrderMatcher.Apply(invoice, order));
        Assert.Equal([(2m, (long?)7), (1m, (long?)null)], invoice.Select(l => (l.Quantity, l.EmployeeId)));
    }

    [Fact]
    public void Matcher_FallsBackToOrder_WhenNamesDiffer()
    {
        var order = new List<InvoiceLine>
        {
            new() { Name = "Системный блок", EmployeeId = 1 },
            new() { Name = "ИБП", EmployeeId = 2 },
        };
        var invoice = new List<InvoiceLine> { new() { Name = "ПК Офисный Pro" }, new() { Name = "APC Back-UPS 650" } };
        Assert.Equal(2, OrderMatcher.Apply(invoice, order));            // строк поровну — по порядку
        Assert.Equal((1L, 2L), (invoice[0].EmployeeId!.Value, invoice[1].EmployeeId!.Value));

        Assert.True(OrderMatcher.Similarity("Ноутбук HP ProBook 450", "HP ProBook 450 G9 ноутбук") > 0.8);
        Assert.True(OrderMatcher.Similarity("Монитор Dell", "Клавиатура A4Tech") < OrderMatcher.MinSimilarity);
    }
}
