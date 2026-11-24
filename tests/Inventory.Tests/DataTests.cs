using Dapper;
using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services;
using Inventory.UI.Warehouse;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Inventory.Tests;

public sealed class DataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-tests-" + Guid.NewGuid().ToString("N"));

    public DataTests()
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

    [Fact]
    public void Migrations_AreIdempotent_AndStartWithoutCompanies()
    {
        Migrations.Run();
        using var c = Db.Open();
        Assert.Equal(Migrations.LatestVersion, c.ExecuteScalar<int>("SELECT MAX(version) FROM schema_version"));
        Assert.Empty(CompanyRepo.All());   // компании пользователь заводит сам
    }

    [Fact]
    public void Company_GetOrCreate_IsCaseInsensitive_ForCyrillic()
    {
        var id1 = CompanyRepo.GetOrCreate("альфа-трейд");
        var id2 = CompanyRepo.GetOrCreate("АЛЬФА-ТРЕЙД");
        Assert.Equal(id1, id2);
        Assert.Null(CompanyRepo.GetOrCreate("  "));
        CompanyRepo.GetOrCreate("Новая");
        Assert.Equal(2, CompanyRepo.All().Count);
    }

    [Fact]
    public void Companies_AddRenameDelete()
    {
        var id = CompanyRepo.Add("  ТОО Новая  ");
        Assert.Equal("ТОО Новая", CompanyRepo.All().Single(c => c.Id == id).Name);
        Assert.Throws<ArgumentException>(() => CompanyRepo.Add("тоо новая"));     // дубль без учёта регистра
        Assert.Throws<ArgumentException>(() => CompanyRepo.Add("   "));

        EmployeeRepo.Insert(new Employee { FullName = "Иванов", CompanyId = id });
        CompanyRepo.Rename(id, "ТОО Новая Плюс");
        Assert.Equal("ТОО Новая Плюс", EmployeeRepo.List()[0].CompanyName);       // переименование видно везде
        CompanyRepo.Add("ТОО Бета");
        Assert.Throws<ArgumentException>(() => CompanyRepo.Rename(id, "тоо бета"));    // занято

        Assert.Throws<ArgumentException>(() => CompanyRepo.Delete(id));             // используется — нельзя
        var unused = CompanyRepo.Add("Временная");
        CompanyRepo.Delete(unused);
        Assert.DoesNotContain(CompanyRepo.All(), c => c.Id == unused);
        Assert.Equal(1, CompanyRepo.WithUsage().Single(c => c.Id == id).Employees);
    }

    [Fact]
    public void Employee_Search_IsCaseInsensitive_AndHidesFired()
    {
        var id = EmployeeRepo.Insert(new Employee { FullName = "Иванов Иван", Department = "ИТ", CompanyId = CompanyRepo.GetOrCreate("ТОО Бета") });
        EmployeeRepo.Insert(new Employee { FullName = "Петров Пётр" });

        var found = Assert.Single(EmployeeRepo.List("иванов"));
        Assert.Equal("ТОО Бета", found.CompanyName);
        Assert.True(found.IsActive);
        Assert.NotEqual(default, found.CreatedAt);

        EmployeeRepo.SetActive(id, false);
        Assert.Empty(EmployeeRepo.List("иванов"));
        Assert.Single(EmployeeRepo.List("иванов", includeInactive: true));
        Assert.Equal(["ИТ"], EmployeeRepo.Departments());
    }

    [Fact]
    public void Item_Insert_WritesArrivalHistory_AndRoundTripsTypes()
    {
        var arrival = new DateTime(2024, 3, 15);
        var id = ItemRepo.Insert(new Item
        {
            Name = "Ноутбук Lenovo", Category = "Ноутбук", SerialNumber = "SN1", InventoryNumber = "  ",
            Price = 350000.50m, WarrantyUntil = new DateTime(2026, 3, 15),
        }, arrival, "вручную");

        var item = ItemRepo.Get(id)!;
        Assert.Null(item.InventoryNumber);
        Assert.Equal(350000.50m, item.Price);
        Assert.Equal(new DateTime(2026, 3, 15), item.WarrantyUntil);
        Assert.Equal(arrival, item.ArrivalDate);
        Assert.Equal(ItemStatus.InStock, item.Status);

        var h = Assert.Single(HistoryRepo.ForItem(id));
        Assert.Equal(HistoryAction.Arrival, h.Action);
        Assert.Equal(arrival, h.Date);
    }

    [Fact]
    public void InventoryNumber_UniqueWhenFilled_EmptyAllowedManyTimes()
    {
        ItemRepo.Insert(new Item { Name = "A" }, DateTime.Today);
        ItemRepo.Insert(new Item { Name = "B" }, DateTime.Today);
        var id = ItemRepo.Insert(new Item { Name = "C", InventoryNumber = "INV-001" }, DateTime.Today);

        Assert.True(ItemRepo.InventoryNumberTaken("INV-001"));
        Assert.False(ItemRepo.InventoryNumberTaken("INV-001", excludeId: id));
        Assert.ThrowsAny<Exception>(() => ItemRepo.Insert(new Item { Name = "D", InventoryNumber = "INV-001" }, DateTime.Today));
    }

    [Fact]
    public void Item_Filters_And_Update_History()
    {
        var id = ItemRepo.Insert(new Item { Name = "Монитор Dell", Category = "Монитор" }, DateTime.Today);
        ItemRepo.Insert(new Item { Name = "Мышь", SerialNumber = "M1", InventoryNumber = "I-2" }, DateTime.Today);

        Assert.Single(ItemRepo.List(new ItemFilter { Text = "МОНИТОР" }));
        Assert.Single(ItemRepo.List(new ItemFilter { NoSerial = true }));
        Assert.Single(ItemRepo.List(new ItemFilter { Category = "Монитор" }));
        Assert.Equal(2, ItemRepo.List(new ItemFilter { Status = ItemStatus.InStock }).Count);

        var before = ItemRepo.Get(id)!;
        var after = before.Clone();
        after.SerialNumber = "XYZ";
        var desc = ItemForm.DescribeChanges(before, after);
        Assert.Equal("Серийник: — → XYZ", desc);
        ItemRepo.Update(after, desc);
        Assert.Equal(2, HistoryRepo.ForItem(id).Count);
        Assert.True(ItemRepo.CanDelete(id));
        ItemRepo.Delete(id);
        Assert.Null(ItemRepo.Get(id));
    }

    [Fact]
    public void GlobalSearch_FindsItemsAndEmployees()
    {
        EmployeeRepo.Insert(new Employee { FullName = "Сидоров Олег" });
        ItemRepo.Insert(new Item { Name = "Принтер HP", SerialNumber = "CN-777" }, DateTime.Today);

        Assert.Equal("Сотрудник", Assert.Single(SearchRepo.Find("сидор")).Kind);
        Assert.Equal("Вещь", Assert.Single(SearchRepo.Find("cn-777")).Kind);
        Assert.Empty(SearchRepo.Find("100%"));
    }

    [Fact]
    public void EmployeeCsv_RoundTrip_UpdatesExisting()
    {
        EmployeeRepo.Insert(new Employee { FullName = "Иванов Иван", Notes = "строка1\nстрока; 2 \"цитата\"" });
        var file = Path.Combine(_root, "emp.csv");
        EmployeeCsv.Export(file, EmployeeRepo.List());

        File.AppendAllText(file, "Новиков Н.;ТОО Бета;Бухгалтерия;Бухгалтер;+7 700;каб. 5;;да\n");
        var (added, updated, _) = EmployeeCsv.Import(file);

        Assert.Equal(1, added);
        Assert.Equal(1, updated);
        Assert.Equal("строка1\nстрока; 2 \"цитата\"", EmployeeRepo.FindByName("иванов иван")!.Notes);
        Assert.Equal("ТОО Бета", EmployeeRepo.FindByName("Новиков Н.")!.CompanyName);
    }
}
