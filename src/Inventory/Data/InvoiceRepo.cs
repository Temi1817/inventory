using System.Data;
using Dapper;
using Inventory.Infrastructure;
using Inventory.Models;

namespace Inventory.Data;

/// <summary>Накладные и поставщики. Главное — приём накладной (Accept).</summary>
public static class InvoiceRepo
{
    public const int MaxItemsPerLine = 1000;

    private const string Select = """
        SELECT inv.id, inv.number, inv.date, inv.total, inv.file_path, inv.notes,
               s.name AS supplier_name, s.bin AS supplier_bin, c.name AS company_name,
               (SELECT COUNT(*) FROM items i WHERE i.invoice_id = inv.id) AS item_count,
               (SELECT o.number FROM orders o WHERE o.invoice_id = inv.id LIMIT 1) AS order_number
        FROM invoices inv
        LEFT JOIN suppliers s ON s.id = inv.supplier_id
        LEFT JOIN companies c ON c.id = inv.company_id
        """;

    public static List<InvoiceRow> List(string? text = null)
    {
        var sql = Select;
        if (!string.IsNullOrWhiteSpace(text))
            sql += " WHERE ulower(inv.number) LIKE @q ESCAPE '\\' OR ulower(s.name) LIKE @q ESCAPE '\\' OR s.bin LIKE @q ESCAPE '\\'";
        sql += " ORDER BY inv.date DESC, inv.id DESC";
        using var c = Db.Open();
        return c.Query<InvoiceRow>(sql, new { q = Db.Like(text ?? "") }).ToList();
    }

    public static InvoiceRow? Get(long id)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<InvoiceRow>(Select + " WHERE inv.id = @id", new { id });
    }

    public static List<string> SupplierNames()
    {
        using var c = Db.Open();
        return c.Query<string>("SELECT name FROM suppliers ORDER BY name").ToList();
    }

    /// <summary>Поставщик по БИН (точнее) или по имени.</summary>
    public static Supplier? FindSupplier(string? name, string? bin)
    {
        using var c = Db.Open();
        return FindSupplier(c, null, name, bin);
    }

    private static Supplier? FindSupplier(IDbConnection c, IDbTransaction? tx, string? name, string? bin)
    {
        bin = NormalizeBin(bin);
        if (bin != null)
        {
            var byBin = c.QueryFirstOrDefault<Supplier>("SELECT * FROM suppliers WHERE bin = @bin", new { bin }, tx);
            if (byBin != null) return byBin;
        }
        name = Db.NullIfEmpty(name);
        return name == null ? null : c.QueryFirstOrDefault<Supplier>(
            "SELECT * FROM suppliers WHERE ulower(name) = @n", new { n = name.ToLowerInvariant() }, tx);
    }

    /// <summary>БИН — 12 цифр; всё лишнее (пробелы, «БИН:») убираем.</summary>
    public static string? NormalizeBin(string? bin)
    {
        if (string.IsNullOrWhiteSpace(bin)) return null;
        var digits = new string(bin.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>Уже принятая накладная с тем же номером и поставщиком (проверка дублей).</summary>
    public static InvoiceRow? FindDuplicate(string? number, string? supplierName, string? supplierBin)
    {
        number = Db.NullIfEmpty(number);
        if (number == null) return null;
        var supplier = FindSupplier(supplierName, supplierBin);
        if (supplier == null) return null;
        using var c = Db.Open();
        return c.QueryFirstOrDefault<InvoiceRow>(Select + " WHERE ulower(inv.number) = @n AND inv.supplier_id = @sid",
            new { n = number.ToLowerInvariant(), sid = supplier.Id });
    }

    /// <summary>Проверка черновика перед приёмом. Возвращает текст ошибки или null.</summary>
    public static string? Validate(InvoiceDraft d)
    {
        if (d.Lines.Count == 0) return "Нет ни одной позиции.";
        for (var i = 0; i < d.Lines.Count; i++)
        {
            var l = d.Lines[i];
            var n = $"Строка {i + 1}";
            if (string.IsNullOrWhiteSpace(l.Name)) return $"{n}: не указано наименование.";
            if (l.Quantity <= 0) return $"{n}: количество должно быть больше нуля.";
            if (l.Price < 0) return $"{n}: цена не может быть отрицательной.";
            if (!l.ToConsumables && l.Quantity != Math.Floor(l.Quantity))
                return $"{n} «{l.Name}»: дробное количество ({l.Quantity}) — отметьте «В расходники» или исправьте.";
            if (!l.ToConsumables && l.Quantity > MaxItemsPerLine)
                return $"{n} «{l.Name}»: {l.Quantity} карточек — слишком много. Отметьте «В расходники».";
        }
        if (d.ItemStatus == ItemStatus.Issued && d.EmployeeId == null) return "Выберите сотрудника, которому выдать.";
        if (d.ItemStatus is not (ItemStatus.InStock or ItemStatus.Issued)) return "Статус при приёме: «На складе» или «Выдан».";
        return null;
    }

    /// <summary>
    /// Принять накладную: поставщик (найти/создать), invoice, по карточке на каждую штуку,
    /// «Приход» в истории с датой накладной, расходники — в остаток. Файл копируется в хранилище.
    /// </summary>
    public static long Accept(InvoiceDraft d)
    {
        var error = Validate(d);
        if (error != null) throw new ArgumentException(error);

        var storedFile = d.SourceFile != null && File.Exists(d.SourceFile) ? FileStore.Import(d.SourceFile) : null;
        try
        {
            using var c = Db.Open();
            using var tx = c.BeginTransaction();

            var issuedToAll = d.ItemStatus == ItemStatus.Issued ? d.EmployeeId : null;
            long? Recipient(InvoiceLine l) => l.ToConsumables ? null : l.EmployeeId ?? issuedToAll;
            foreach (var empId in d.Lines.Select(Recipient).OfType<long>().Distinct())
                if (c.ExecuteScalar<long?>("SELECT is_active FROM employees WHERE id=@empId", new { empId }, tx) != 1)
                {
                    var name = c.ExecuteScalar<string?>("SELECT full_name FROM employees WHERE id=@empId", new { empId }, tx);
                    throw new ArgumentException($"Сотрудник «{name ?? "?"}» не найден или уволен — выберите другого получателя.");
                }
            if (d.OrderId is { } orderId)
            {
                var orderStatus = c.ExecuteScalar<string?>("SELECT status FROM orders WHERE id=@orderId", new { orderId }, tx)
                                  ?? throw new ArgumentException("Заказ не найден.");
                if (!OrderStatus.IsOpen(orderStatus)) throw new ArgumentException($"Заказ уже «{orderStatus}» — его нельзя закрыть ещё раз.");
            }

            var supplierId = GetOrCreateSupplier(c, tx, d.SupplierName, d.SupplierBin);
            var companyId = CompanyIdInTx(c, tx, d.CompanyName);
            var date = d.Date.Date;

            var invoiceId = c.ExecuteScalar<long>("""
                INSERT INTO invoices(number, date, supplier_id, company_id, total, file_path, notes, created_at)
                VALUES (@number, @date, @supplierId, @companyId, @total, @file, @notes, @now) RETURNING id
                """, new
            {
                number = Db.NullIfEmpty(d.Number), date, supplierId, companyId, total = d.Total,
                file = storedFile, notes = Db.NullIfEmpty(d.Notes), now = DateTime.Now,
            }, tx);

            foreach (var line in d.Lines)
            {
                if (line.ToConsumables)
                {
                    ConsumableIncome(c, tx, line, date, invoiceId, d.Number);
                    continue;
                }
                var emp = Recipient(line);
                for (var k = 0; k < (int)line.Quantity; k++)
                {
                    var itemId = c.ExecuteScalar<long>("""
                        INSERT INTO items(invoice_id, name, category, price, status, employee_id, company_id, created_at)
                        VALUES (@invoiceId, @name, @category, @price, @status, @emp, @companyId, @now) RETURNING id
                        """, new
                    {
                        invoiceId, name = line.Name.Trim(), category = Db.NullIfEmpty(line.Category), price = line.Price,
                        status = emp != null ? ItemStatus.Issued : ItemStatus.InStock, emp, companyId, now = DateTime.Now,
                    }, tx);
                    HistoryRepo.Add(c, tx, new HistoryEntry
                    {
                        ItemId = itemId, Date = date, Action = HistoryAction.Arrival,
                        ToEmployeeId = emp,
                        Comment = $"Накладная № {d.Number ?? "—"}" + (emp != null ? ", выдано сразу" : ""),
                    });
                }
            }
            if (d.OrderId is { } closeOrder)
                c.Execute("UPDATE orders SET status = @s, invoice_id = @invoiceId WHERE id = @closeOrder",
                    new { s = OrderStatus.Received, invoiceId, closeOrder }, tx);
            tx.Commit();
            return invoiceId;
        }
        catch
        {
            // Не оставляем «осиротевший» файл, если приём не удался.
            if (storedFile != null) try { File.Delete(AppPaths.ToAbsolute(storedFile)); } catch { /* не критично */ }
            throw;
        }
    }

    /// <summary>
    /// Исправить номер, дату, компанию и заметки принятой накладной. Компания ставится и всем вещям по ней. Новая дата переносится в «Приход» всех вещей по ней
    /// (от неё считаются «Дата прихода» и «Дата выдачи») и в движения расходников по этой накладной.
    /// </summary>
    public static void UpdateHeader(long id, string? number, DateTime date, string? notes, string? company)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM invoices WHERE id = @id", new { id }, tx) == 0)
            throw new ArgumentException("Накладная не найдена.");
        number = Db.NullIfEmpty(number);
        var day = date.Date;
        var companyId = CompanyIdInTx(c, tx, company);
        c.Execute("UPDATE invoices SET number = @number, date = @day, notes = @notes, company_id = @companyId WHERE id = @id",
            new { id, number, day, notes = Db.NullIfEmpty(notes), companyId }, tx);
        // Компания накладной = компания всех вещей по ней.
        c.Execute("UPDATE items SET company_id = @companyId WHERE invoice_id = @id", new { id, companyId }, tx);
        c.Execute("""
            UPDATE history SET date = @day,
                   comment = 'Накладная № ' || COALESCE(@number, '—') ||
                             CASE WHEN to_employee_id IS NOT NULL THEN ', выдано сразу' ELSE '' END
            WHERE action = 'Приход' AND item_id IN (SELECT id FROM items WHERE invoice_id = @id)
            """, new { id, day, number }, tx);
        c.Execute("UPDATE consumable_moves SET date = @day WHERE invoice_id = @id", new { id, day }, tx);
        tx.Commit();
    }

    internal static long? GetOrCreateSupplier(IDbConnection c, IDbTransaction tx, string? name, string? bin)
    {
        bin = NormalizeBin(bin);
        name = Db.NullIfEmpty(name);
        var existing = FindSupplier(c, tx, name, bin);
        if (existing != null)
        {
            if (existing.Bin == null && bin != null)
                c.Execute("UPDATE suppliers SET bin=@bin WHERE id=@Id", new { bin, existing.Id }, tx);
            return existing.Id;
        }
        if (name == null && bin == null) return null;
        return c.ExecuteScalar<long>("INSERT INTO suppliers(name, bin) VALUES (@name, @bin) RETURNING id",
            new { name = name ?? $"БИН {bin}", bin }, tx);
    }

    internal static long? CompanyIdInTx(IDbConnection c, IDbTransaction tx, string? name)
    {
        name = Db.NullIfEmpty(name);
        if (name == null) return null;
        return c.ExecuteScalar<long?>("SELECT id FROM companies WHERE ulower(name) = @n", new { n = name.ToLowerInvariant() }, tx)
               ?? c.ExecuteScalar<long>("INSERT INTO companies(name) VALUES (@name) RETURNING id", new { name }, tx);
    }

    private static void ConsumableIncome(IDbConnection c, IDbTransaction tx, InvoiceLine line, DateTime date, long invoiceId, string? number)
    {
        var name = line.Name.Trim();
        var id = c.ExecuteScalar<long?>("SELECT id FROM consumables WHERE ulower(name) = @n", new { n = name.ToLowerInvariant() }, tx)
                 ?? c.ExecuteScalar<long>("INSERT INTO consumables(name, category, quantity, min_quantity, unit) VALUES (@name, @cat, 0, 0, @unit) RETURNING id",
                     new { name, cat = Db.NullIfEmpty(line.Category), unit = Db.NullIfEmpty(line.Unit) }, tx);
        c.Execute("UPDATE consumables SET quantity = quantity + @q WHERE id = @id", new { q = line.Quantity, id }, tx);
        c.Execute("""
            INSERT INTO consumable_moves(consumable_id, date, quantity, invoice_id, comment)
            VALUES (@id, @date, @q, @invoiceId, @comment)
            """, new { id, date, q = line.Quantity, invoiceId, comment = $"Приход по накладной № {number ?? "—"}" }, tx);
    }
}
