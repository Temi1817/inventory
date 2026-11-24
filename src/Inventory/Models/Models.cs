namespace Inventory.Models;

public static class ItemStatus
{
    public const string InStock = "На складе";
    public const string Issued = "Выдан";
    public const string Repair = "В ремонте";
    public const string WrittenOff = "Списан";

    public static readonly string[] All = [InStock, Issued, Repair, WrittenOff];
}

public static class HistoryAction
{
    public const string Arrival = "Приход";
    public const string Issue = "Выдача";
    public const string Return = "Возврат";
    public const string Transfer = "Передача";
    public const string Repair = "Ремонт";
    public const string WriteOff = "Списание";
    public const string Change = "Изменение";
}

public class Company
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

public class Employee
{
    public long Id { get; set; }
    public string FullName { get; set; } = "";
    public long? CompanyId { get; set; }
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Phone { get; set; }
    public string? Workplace { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    // Для отображения (JOIN)
    public string? CompanyName { get; set; }
    public long ItemCount { get; set; }

    public override string ToString() => FullName;
}

public class Supplier
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Bin { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
}

public class Invoice
{
    public long Id { get; set; }
    public string? Number { get; set; }
    public DateTime? Date { get; set; }
    public long? SupplierId { get; set; }
    public long? CompanyId { get; set; }
    public decimal? Total { get; set; }
    public string? FilePath { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }

    public string? SupplierName { get; set; }
    public string? CompanyName { get; set; }
}

public class Item
{
    public long Id { get; set; }
    public long? InvoiceId { get; set; }
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public string? SerialNumber { get; set; }
    public string? InventoryNumber { get; set; }
    public decimal? Price { get; set; }
    public DateTime? WarrantyUntil { get; set; }
    public string? PhotoPath { get; set; }
    public string Status { get; set; } = ItemStatus.InStock;
    public long? EmployeeId { get; set; }
    public long? CompanyId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }

    // Для отображения (JOIN)
    public string? EmployeeName { get; set; }
    public string? CompanyName { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public DateTime? IssuedDate { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTime? InvoiceDate { get; set; }
    public string? InvoiceFilePath { get; set; }

    public Item Clone() => (Item)MemberwiseClone();
}

public class Consumable
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public decimal Quantity { get; set; }
    public decimal MinQuantity { get; set; }
    public string? Unit { get; set; }
    public string? Notes { get; set; }
}

public class HistoryEntry
{
    public long Id { get; set; }
    public long ItemId { get; set; }
    public DateTime Date { get; set; }
    public string Action { get; set; } = "";
    public long? FromEmployeeId { get; set; }
    public long? ToEmployeeId { get; set; }
    public string? Comment { get; set; }

    public string? FromName { get; set; }
    public string? ToName { get; set; }
}

/// <summary>Фильтр для таблицы склада.</summary>
public class ItemFilter
{
    public string? Status { get; set; }
    public string? Category { get; set; }
    public long? CompanyId { get; set; }
    public long? EmployeeId { get; set; }
    public long? InvoiceId { get; set; }
    public bool NoInventoryNumber { get; set; }
    public bool NoSerial { get; set; }
    public string? Text { get; set; }
}

/// <summary>Результат глобального поиска.</summary>
public class SearchHit
{
    public string Kind { get; set; } = "";   // Вещь | Сотрудник | Накладная
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Details { get; set; }
}
