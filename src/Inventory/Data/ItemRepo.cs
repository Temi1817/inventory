using Dapper;
using Inventory.Models;

namespace Inventory.Data;

public static class ItemRepo
{
    private const string Select = """
        SELECT i.*, e.full_name AS employee_name, c.name AS company_name,
               inv.number AS invoice_number, inv.date AS invoice_date, inv.file_path AS invoice_file_path,
               (SELECT MIN(h.date) FROM history h WHERE h.item_id = i.id AND h.action = 'Приход') AS arrival_date,
               (SELECT MAX(h.date) FROM history h WHERE h.item_id = i.id AND h.to_employee_id = i.employee_id
                   AND h.action IN ('Приход','Выдача','Передача')) AS issued_date
        FROM items i
        LEFT JOIN employees e ON e.id = i.employee_id
        LEFT JOIN companies c ON c.id = i.company_id
        LEFT JOIN invoices inv ON inv.id = i.invoice_id
        """;

    public static List<Item> List(ItemFilter f)
    {
        var sql = Select + " WHERE 1=1";
        if (f.Status != null) sql += " AND i.status = @Status";
        if (f.Category != null) sql += " AND i.category = @Category";
        if (f.CompanyId != null) sql += " AND i.company_id = @CompanyId";
        if (f.EmployeeId != null) sql += " AND i.employee_id = @EmployeeId";
        if (f.InvoiceId != null) sql += " AND i.invoice_id = @InvoiceId";
        if (f.NoInventoryNumber) sql += " AND (i.inventory_number IS NULL OR i.inventory_number = '')";
        if (f.NoSerial) sql += " AND (i.serial_number IS NULL OR i.serial_number = '')";
        if (!string.IsNullOrWhiteSpace(f.Text))
            sql += """
                 AND (ulower(i.name) LIKE @q ESCAPE '\' OR ulower(i.serial_number) LIKE @q ESCAPE '\'
                   OR ulower(i.inventory_number) LIKE @q ESCAPE '\' OR ulower(i.category) LIKE @q ESCAPE '\'
                   OR ulower(e.full_name) LIKE @q ESCAPE '\')
                """;
        sql += " ORDER BY i.id DESC";

        var p = new DynamicParameters(f);
        p.Add("q", Db.Like(f.Text ?? ""));
        using var c = Db.Open();
        return c.Query<Item>(sql, p).ToList();
    }

    public static Item? Get(long id)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<Item>(Select + " WHERE i.id = @id", new { id });
    }

    public static List<string> Categories()
    {
        using var c = Db.Open();
        return c.Query<string>(
            "SELECT DISTINCT category FROM items WHERE category IS NOT NULL AND category <> '' ORDER BY category").ToList();
    }

    /// <summary>Занят ли инв. номер другой вещью.</summary>
    public static bool InventoryNumberTaken(string? number, long excludeId = 0)
    {
        number = Db.NullIfEmpty(number);
        if (number == null) return false;
        using var c = Db.Open();
        return c.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM items WHERE inventory_number = @number AND id <> @excludeId",
            new { number, excludeId }) > 0;
    }

    /// <summary>Создать карточку вручную + запись «Приход» в истории.</summary>
    public static long Insert(Item item, DateTime arrivalDate, string? comment = null)
    {
        Normalize(item);
        item.CreatedAt = DateTime.Now;
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        item.Id = c.ExecuteScalar<long>("""
            INSERT INTO items(invoice_id, name, category, serial_number, inventory_number, price, warranty_until,
                              photo_path, status, employee_id, company_id, notes, created_at)
            VALUES (@InvoiceId, @Name, @Category, @SerialNumber, @InventoryNumber, @Price, @WarrantyUntil,
                    @PhotoPath, @Status, @EmployeeId, @CompanyId, @Notes, @CreatedAt)
            RETURNING id
            """, item, tx);
        HistoryRepo.Add(c, tx, new HistoryEntry
        {
            ItemId = item.Id, Date = arrivalDate.Date, Action = HistoryAction.Arrival,
            ToEmployeeId = item.EmployeeId, Comment = Db.NullIfEmpty(comment),
        });
        tx.Commit();
        return item.Id;
    }

    /// <summary>Сохранить поля карточки. Если что-то изменилось — запись «Изменение» с описанием.</summary>
    public static void Update(Item item, string? changeDescription)
    {
        Normalize(item);
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            UPDATE items SET name=@Name, category=@Category, serial_number=@SerialNumber,
                   inventory_number=@InventoryNumber, price=@Price, warranty_until=@WarrantyUntil,
                   photo_path=@PhotoPath, company_id=@CompanyId, notes=@Notes
            WHERE id=@Id
            """, item, tx);
        if (!string.IsNullOrWhiteSpace(changeDescription))
            HistoryRepo.Add(c, tx, new HistoryEntry
            {
                ItemId = item.Id, Date = DateTime.Now, Action = HistoryAction.Change, Comment = changeDescription,
            });
        tx.Commit();
    }

    /// <summary>
    /// Удалить можно только ошибочно заведённую карточку: на складе и без движений, кроме прихода.
    /// Всё остальное — только «Списан».
    /// </summary>
    public static bool CanDelete(long id)
    {
        using var c = Db.Open();
        var moves = c.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM history WHERE item_id=@id AND action NOT IN ('Приход','Изменение')", new { id });
        var status = c.ExecuteScalar<string>("SELECT status FROM items WHERE id=@id", new { id });
        return moves == 0 && status == ItemStatus.InStock;
    }

    public static void Delete(long id)
    {
        if (!CanDelete(id))
            throw new InvalidOperationException("У вещи есть история движений — её можно только списать.");
        using var c = Db.Open();
        c.Execute("DELETE FROM items WHERE id=@id", new { id });
    }

    private static void Normalize(Item i)
    {
        i.Name = i.Name.Trim();
        if (i.Name.Length == 0) throw new ArgumentException("Наименование обязательно.");
        i.Category = Db.NullIfEmpty(i.Category);
        i.SerialNumber = Db.NullIfEmpty(i.SerialNumber);
        i.InventoryNumber = Db.NullIfEmpty(i.InventoryNumber);
        i.Notes = Db.NullIfEmpty(i.Notes);
        i.WarrantyUntil = i.WarrantyUntil?.Date;
    }
}
