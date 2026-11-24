using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;

namespace Inventory.Tests;

public sealed class MovementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-tests-" + Guid.NewGuid().ToString("N"));
    private readonly long _ivanov, _petrov, _laptop, _mouse;

    public MovementTests()
    {
        AppPaths.UseRoot(_root);
        AppPaths.EnsureCreated();
        Db.Init(AppPaths.Database);
        Migrations.Run();
        _ivanov = EmployeeRepo.Insert(new Employee { FullName = "Иванов" });
        _petrov = EmployeeRepo.Insert(new Employee { FullName = "Петров" });
        _laptop = ItemRepo.Insert(new Item { Name = "Ноутбук" }, new DateTime(2024, 1, 10));
        _mouse = ItemRepo.Insert(new Item { Name = "Мышь" }, new DateTime(2024, 1, 10));
    }

    public void Dispose()
    {
        Db.ReleaseAll();
        try { Directory.Delete(_root, true); } catch { /* временная папка */ }
    }

    [Fact]
    public void Issue_Several_SetsHolder_AndIssuedDate()
    {
        MovementRepo.Issue([_laptop, _mouse], _ivanov, new DateTime(2024, 2, 1), "под роспись");

        var mine = ItemRepo.List(new ItemFilter { EmployeeId = _ivanov });
        Assert.Equal(2, mine.Count);
        Assert.All(mine, i =>
        {
            Assert.Equal(ItemStatus.Issued, i.Status);
            Assert.Equal("Иванов", i.EmployeeName);
            Assert.Equal(new DateTime(2024, 2, 1), i.IssuedDate);
        });
        var h = HistoryRepo.ForItem(_laptop)[0];
        Assert.Equal(HistoryAction.Issue, h.Action);
        Assert.Equal("Иванов", h.ToName);
        Assert.Equal("под роспись", h.Comment);
    }

    [Fact]
    public void Transfer_Then_Return_RecordsFromAndTo()
    {
        MovementRepo.Issue([_laptop], _ivanov, new DateTime(2024, 2, 1), null);
        MovementRepo.Transfer([_laptop], _petrov, new DateTime(2024, 3, 1), null);

        var item = ItemRepo.Get(_laptop)!;
        Assert.Equal(_petrov, item.EmployeeId);
        Assert.Equal(new DateTime(2024, 3, 1), item.IssuedDate);
        var t = HistoryRepo.ForItem(_laptop)[0];
        Assert.Equal((HistoryAction.Transfer, "Иванов", "Петров"), (t.Action, t.FromName, t.ToName));

        MovementRepo.Return([_laptop], new DateTime(2024, 4, 1), null);
        item = ItemRepo.Get(_laptop)!;
        Assert.Equal(ItemStatus.InStock, item.Status);
        Assert.Null(item.EmployeeId);
        Assert.Equal("Петров", HistoryRepo.ForItem(_laptop)[0].FromName);
        Assert.Empty(ItemRepo.List(new ItemFilter { EmployeeId = _petrov }));
    }

    [Fact]
    public void InvalidTransitions_AreRejected_WithoutPartialChanges()
    {
        MovementRepo.Issue([_laptop], _ivanov, DateTime.Today, null);

        // Ноутбук уже выдан — повторная выдача вместе с мышью не должна выдать и мышь.
        Assert.Throws<ArgumentException>(() => MovementRepo.Issue([_laptop, _mouse], _petrov, DateTime.Today, null));
        Assert.Equal(ItemStatus.InStock, ItemRepo.Get(_mouse)!.Status);

        Assert.Throws<ArgumentException>(() => MovementRepo.Transfer([_mouse], _petrov, DateTime.Today, null));
        Assert.Throws<ArgumentException>(() => MovementRepo.Transfer([_laptop], _ivanov, DateTime.Today, null));
        Assert.Throws<ArgumentException>(() => MovementRepo.Return([_mouse], DateTime.Today, null));
    }

    [Fact]
    public void Repair_And_WriteOff_Flow()
    {
        MovementRepo.Issue([_laptop], _ivanov, DateTime.Today, null);
        MovementRepo.Repair([_laptop], DateTime.Today, "не включается");
        var item = ItemRepo.Get(_laptop)!;
        Assert.Equal(ItemStatus.Repair, item.Status);
        Assert.Null(item.EmployeeId);
        Assert.Equal("Иванов", HistoryRepo.ForItem(_laptop)[0].FromName);

        MovementRepo.WriteOff([_laptop], DateTime.Today, "не ремонтопригоден");
        Assert.Equal(ItemStatus.WrittenOff, ItemRepo.Get(_laptop)!.Status);
        Assert.Throws<ArgumentException>(() => MovementRepo.WriteOff([_laptop], DateTime.Today, null));
        Assert.Throws<ArgumentException>(() => MovementRepo.Issue([_laptop], _ivanov, DateTime.Today, null));
        Assert.False(ItemRepo.CanDelete(_laptop));
    }

    [Fact]
    public void CannotIssue_ToFiredEmployee()
    {
        EmployeeRepo.SetActive(_petrov, false);
        var ex = Assert.Throws<ArgumentException>(() => MovementRepo.Issue([_mouse], _petrov, DateTime.Today, null));
        Assert.Contains("уволенный", ex.Message);
    }
}
