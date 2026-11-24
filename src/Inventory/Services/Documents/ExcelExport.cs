using ClosedXML.Excel;
using Inventory.Data;
using Inventory.Models;

namespace Inventory.Services.Documents;

/// <summary>Экспорт таблиц в Excel (.xlsx).</summary>
public static class ExcelExport
{
    public sealed record Column<T>(string Header, Func<T, object?> Value, string? Format = null);

    public static void Write<T>(string path, string sheet, string title, IReadOnlyList<Column<T>> columns, IEnumerable<T> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheet);
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Сформировано {DateTime.Now:dd.MM.yyyy HH:mm}";
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        const int headerRow = 4;
        for (var c = 0; c < columns.Count; c++) ws.Cell(headerRow, c + 1).Value = columns[c].Header;

        var r = headerRow;
        foreach (var row in rows)
        {
            r++;
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = ws.Cell(r, c + 1);
                cell.Value = ToCell(columns[c].Value(row));
                if (columns[c].Format != null) cell.Style.NumberFormat.Format = columns[c].Format;
            }
        }

        // Строка-заголовок + данные как «умная таблица» с фильтрами (нужна хотя бы одна строка).
        var range = ws.Range(headerRow, 1, Math.Max(r, headerRow + 1), columns.Count);
        range.CreateTable(sheet.Replace(' ', '_')).Theme = XLTableTheme.TableStyleLight9;
        ws.Columns(1, columns.Count).AdjustToContents(headerRow, r);
        foreach (var col in ws.Columns(1, columns.Count)) if (col.Width > 60) col.Width = 60;
        ws.SheetView.FreezeRows(headerRow);
        wb.SaveAs(path);
    }

    private static XLCellValue ToCell(object? v) => v switch
    {
        null => Blank.Value,
        string s => s,
        DateTime d => d,
        decimal m => (double)m,
        double d => d,
        int i => i,
        long l => l,
        bool b => b ? "да" : "нет",
        _ => v.ToString() ?? "",
    };

    public static void Items(string path, string title, IEnumerable<Item> items) =>
        Write(path, "Склад", title,
        [
            new("Инв. номер", i => i.InventoryNumber),
            new("Наименование", i => i.Name),
            new("Категория", i => i.Category),
            new("Серийный номер", i => i.SerialNumber),
            new("Статус", i => i.Status),
            new("У кого", i => i.EmployeeName),
            new("Компания", i => i.CompanyName),
            new("Цена", i => i.Price, "#,##0.00"),
            new("Дата прихода", i => i.ArrivalDate, "dd.mm.yyyy"),
            new("Гарантия до", i => i.WarrantyUntil, "dd.mm.yyyy"),
            new("Накладная", i => i.InvoiceNumber),
            new("Заметки", i => i.Notes),
        ], items);

    public static void EmployeeItems(string path, Employee e, IEnumerable<Item> items) =>
        Write(path, "Вещи", $"Имущество за сотрудником: {e.FullName}",
        [
            new("Инв. номер", i => i.InventoryNumber),
            new("Наименование", i => i.Name),
            new("Категория", i => i.Category),
            new("Серийный номер", i => i.SerialNumber),
            new("Дата выдачи", i => i.IssuedDate, "dd.mm.yyyy"),
            new("Цена", i => i.Price, "#,##0.00"),
        ], items);

    public static void Consumables(string path, IEnumerable<Consumable> items) =>
        Write(path, "Расходники", "Расходники",
        [
            new("Наименование", c => c.Name),
            new("Категория", c => c.Category),
            new("Остаток", c => c.Quantity, "0.##"),
            new("Минимум", c => c.MinQuantity, "0.##"),
            new("Ед.", c => c.Unit),
            new("Заметки", c => c.Notes),
        ], items);
}
