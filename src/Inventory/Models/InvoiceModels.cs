namespace Inventory.Models;

/// <summary>Что вернул Claude (поля могут быть null, если не видны на документе).</summary>
public sealed class RecognizedInvoice
{
    public string? Number { get; set; }
    public string? Date { get; set; }
    public string? Supplier { get; set; }
    public string? SupplierBin { get; set; }
    public decimal? Total { get; set; }
    public List<RecognizedLine> Items { get; set; } = [];
}

public sealed class RecognizedLine
{
    public string? Name { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? Price { get; set; }
    public string? Category { get; set; }
}

/// <summary>Проверенная пользователем накладная — готова к приёму.</summary>
public sealed class InvoiceDraft
{
    public string? Number { get; set; }
    public DateTime Date { get; set; } = DateTime.Today;
    public string? SupplierName { get; set; }
    public string? SupplierBin { get; set; }
    public string? CompanyName { get; set; }
    public decimal? Total { get; set; }
    public string? Notes { get; set; }
    /// <summary>Исходный файл накладной (будет скопирован в хранилище).</summary>
    public string? SourceFile { get; set; }
    public List<InvoiceLine> Lines { get; set; } = [];

    /// <summary>Заказ (счёт на оплату), который закрывает эта накладная.</summary>
    public long? OrderId { get; set; }

    /// <summary>Статус для всех вещей при приёме: «На складе» или «Выдан» (тогда нужен сотрудник).
    /// Если у строки указан свой сотрудник (EmployeeId) — он важнее.</summary>
    public string ItemStatus { get; set; } = Models.ItemStatus.InStock;
    public long? EmployeeId { get; set; }
}

public sealed class InvoiceLine
{
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string? Unit { get; set; } = "шт";
    public decimal? Price { get; set; }
    /// <summary>Не заводить карточки, а добавить в остаток расходников.</summary>
    public bool ToConsumables { get; set; }

    /// <summary>Кому выдать при приёме / для кого заказано. null — на склад.</summary>
    public long? EmployeeId { get; set; }

    /// <summary>То же для привязки к выпадающему списку в таблице (0 — никому).</summary>
    public long EmployeeKey
    {
        get => EmployeeId ?? 0;
        set => EmployeeId = value == 0 ? null : value;
    }

    public decimal? Sum => Price * Quantity;

    public InvoiceLine Clone() => (InvoiceLine)MemberwiseClone();
}

/// <summary>Строка списка накладных.</summary>
public sealed class InvoiceRow
{
    public long Id { get; set; }
    public string? Number { get; set; }
    public DateTime? Date { get; set; }
    public string? SupplierName { get; set; }
    public string? SupplierBin { get; set; }
    public string? CompanyName { get; set; }
    public decimal? Total { get; set; }
    public string? FilePath { get; set; }
    public string? Notes { get; set; }
    public long ItemCount { get; set; }
    public string? OrderNumber { get; set; }
    public bool HasFile => !string.IsNullOrEmpty(FilePath);
    public string FileMark => HasFile ? "есть" : "";
}

public static class OrderStatus
{
    public const string AwaitingPayment = "Ждёт оплаты";
    public const string Paid = "Оплачен";
    public const string Received = "Получен";
    public const string Cancelled = "Отменён";

    public static bool IsOpen(string status) => status is AwaitingPayment or Paid;
}

/// <summary>Заказ (счёт на оплату) для списка.</summary>
public sealed class OrderRow
{
    public long Id { get; set; }
    public string? Number { get; set; }
    public DateTime? Date { get; set; }
    public string? SupplierName { get; set; }
    public string? SupplierBin { get; set; }
    public string? CompanyName { get; set; }
    public decimal? Total { get; set; }
    public string? FilePath { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = OrderStatus.AwaitingPayment;
    public DateTime? PaidAt { get; set; }
    public long? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public long LineCount { get; set; }
    public string? Recipients { get; set; }
    public decimal? LinesSum { get; set; }

    public decimal? Amount => Total ?? LinesSum;
    public string? RecipientsText => Recipients?.Replace(",", ", ");
    public string Title => $"№ {Number ?? "—"}" + (Date != null ? $" от {Date:dd.MM.yyyy}" : "");
}

/// <summary>Строка заказа для показа (с именем получателя).</summary>
public sealed class OrderLineRow
{
    public long OrderId { get; set; }
    public string Name { get; set; } = "";
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? Price { get; set; }
    public string? EmployeeName { get; set; }
    public string? OrderNumber { get; set; }
    public DateTime? OrderDate { get; set; }
    public string? OrderStatus { get; set; }
    public decimal? Sum => Price * Quantity;
    public string Recipient => EmployeeName ?? "на склад";
}