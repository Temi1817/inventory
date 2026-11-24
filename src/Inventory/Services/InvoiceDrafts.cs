using Inventory.Data;
using Inventory.Models;
using Inventory.Services.Claude;

namespace Inventory.Services;

/// <summary>Превращает ответ распознавания в черновик для проверки.</summary>
public static class InvoiceDrafts
{
    // Единицы «не в штуках» — такие позиции по умолчанию идут в расходники.
    private static readonly string[] MeasureUnits =
        ["м", "м.", "метр", "пм", "п.м", "п.м.", "кг", "г", "л", "упак", "уп", "уп.", "пач", "пачка", "рул", "рулон", "бухта", "компл.м"];

    private static readonly string[] ConsumableCategories = ["Картриджи", "Канцелярия", "Кабели"];

    public static InvoiceDraft FromRecognized(RecognizedInvoice r, string? sourceFile)
    {
        var supplier = InvoiceRepo.FindSupplier(r.Supplier, r.SupplierBin);
        var draft = new InvoiceDraft
        {
            Number = r.Number?.Trim(),
            Date = InvoiceRecognizer.ParseDate(r.Date) ?? DateTime.Today,
            // Известного поставщика показываем так, как он записан в базе.
            SupplierName = supplier?.Name ?? r.Supplier?.Trim(),
            SupplierBin = InvoiceRepo.NormalizeBin(r.SupplierBin) ?? supplier?.Bin,
            Total = r.Total,
            SourceFile = sourceFile,
        };
        foreach (var i in r.Items)
        {
            var line = new InvoiceLine
            {
                Name = i.Name?.Trim() ?? "",
                Category = i.Category?.Trim(),
                Quantity = i.Quantity is > 0 ? i.Quantity.Value : 1,
                Unit = string.IsNullOrWhiteSpace(i.Unit) ? "шт" : i.Unit.Trim(),
                Price = i.Price is >= 0 ? Math.Round(i.Price.Value, 2) : null,
            };
            line.ToConsumables = SuggestConsumable(line);
            draft.Lines.Add(line);
        }
        return draft;
    }

    /// <summary>Можно ли разбить строку по 1 шт. (целое количество больше 1, не расходник).</summary>
    public static bool CanSplit(InvoiceLine l) =>
        !l.ToConsumables && l.Quantity > 1 && l.Quantity == Math.Floor(l.Quantity) && l.Quantity <= InvoiceRepo.MaxItemsPerLine;

    /// <summary>«Клавиатура × 2» → две строки «Клавиатура × 1» (чтобы назначить разных получателей).</summary>
    public static List<InvoiceLine> SplitByOne(InvoiceLine l)
    {
        if (!CanSplit(l)) return [l];
        return Enumerable.Range(0, (int)l.Quantity).Select(_ =>
        {
            var part = l.Clone();
            part.Quantity = 1;
            return part;
        }).ToList();
    }

    /// <summary>Подсказка «в расходники»: дробное количество, единица измерения не штуки или расходная категория.</summary>
    public static bool SuggestConsumable(InvoiceLine l)
    {
        var unit = (l.Unit ?? "").Trim().ToLowerInvariant();
        return l.Quantity != Math.Floor(l.Quantity)
               || MeasureUnits.Contains(unit)
               || l.Quantity > InvoiceRepo.MaxItemsPerLine
               || (l.Category != null && ConsumableCategories.Contains(l.Category, StringComparer.OrdinalIgnoreCase));
    }
}
