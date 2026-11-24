using Dapper;
using Inventory.Infrastructure;
using Inventory.Models;

namespace Inventory.Data;

/// <summary>Заказы по счетам на оплату: что купить и для кого. Закрываются накладной (InvoiceRepo.Accept с OrderId).</summary>
public static class OrderRepo
{
    private const string Select = """
        SELECT o.id, o.number, o.date, o.total, o.file_path, o.notes, o.status, o.paid_at, o.invoice_id,
               s.name AS supplier_name, s.bin AS supplier_bin, c.name AS company_name, inv.number AS invoice_number,
               (SELECT COUNT(*) FROM order_lines l WHERE l.order_id = o.id) AS line_count,
               (SELECT SUM(l.quantity * l.price) FROM order_lines l WHERE l.order_id = o.id) AS lines_sum,
               (SELECT GROUP_CONCAT(DISTINCT e.full_name) FROM order_lines l JOIN employees e ON e.id = l.employee_id
                WHERE l.order_id = o.id) AS recipients
        FROM orders o
        LEFT JOIN suppliers s ON s.id = o.supplier_id
        LEFT JOIN companies c ON c.id = o.company_id
        LEFT JOIN invoices inv ON inv.id = o.invoice_id
        """;

    public static List<OrderRow> List(bool includeClosed = false, string? text = null)
    {
        var sql = Select + " WHERE 1=1";
        if (!includeClosed) sql += " AND o.status IN ('Ждёт оплаты','Оплачен')";
        if (!string.IsNullOrWhiteSpace(text))
            sql += """
                 AND (ulower(o.number) LIKE @q ESCAPE '\' OR ulower(s.name) LIKE @q ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM order_lines l LEFT JOIN employees e ON e.id = l.employee_id
                              WHERE l.order_id = o.id AND (ulower(l.name) LIKE @q ESCAPE '\' OR ulower(e.full_name) LIKE @q ESCAPE '\')))
                """;
        sql += " ORDER BY CASE o.status WHEN 'Ждёт оплаты' THEN 0 WHEN 'Оплачен' THEN 1 ELSE 2 END, o.date DESC, o.id DESC";
        using var c = Db.Open();
        return c.Query<OrderRow>(sql, new { q = Db.Like(text ?? "") }).ToList();
    }

    public static OrderRow? Get(long id)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<OrderRow>(Select + " WHERE o.id = @id", new { id });
    }

    public static List<InvoiceLine> Lines(long orderId)
    {
        using var c = Db.Open();
        return c.Query<InvoiceLine>("""
            SELECT name, category, quantity, unit, price, employee_id, to_consumables
            FROM order_lines WHERE order_id = @orderId ORDER BY line_no
            """, new { orderId }).ToList();
    }

    public static List<OrderLineRow> LineRows(long orderId)
    {
        using var c = Db.Open();
        return c.Query<OrderLineRow>("""
            SELECT l.order_id, l.name, l.quantity, l.unit, l.price, e.full_name AS employee_name
            FROM order_lines l LEFT JOIN employees e ON e.id = l.employee_id
            WHERE l.order_id = @orderId ORDER BY l.line_no
            """, new { orderId }).ToList();
    }

    /// <summary>Что заказано для сотрудника и ещё не пришло.</summary>
    public static List<OrderLineRow> OpenForEmployee(long employeeId)
    {
        using var c = Db.Open();
        return c.Query<OrderLineRow>("""
            SELECT l.order_id, l.name, l.quantity, l.unit, l.price, o.number AS order_number, o.date AS order_date, o.status AS order_status
            FROM order_lines l JOIN orders o ON o.id = l.order_id
            WHERE l.employee_id = @employeeId AND o.status IN ('Ждёт оплаты','Оплачен')
            ORDER BY o.date, l.line_no
            """, new { employeeId }).ToList();
    }

    /// <summary>Заказ как черновик (для редактирования или для приёма товара по нему).</summary>
    public static InvoiceDraft ToDraft(long id)
    {
        var o = Get(id) ?? throw new ArgumentException("Заказ не найден.");
        return new InvoiceDraft
        {
            Number = o.Number, Date = o.Date ?? DateTime.Today, SupplierName = o.SupplierName, SupplierBin = o.SupplierBin,
            CompanyName = o.CompanyName, Total = o.Total, Notes = o.Notes,
            SourceFile = o.FilePath == null ? null : AppPaths.ToAbsolute(o.FilePath),
            Lines = Lines(id),
        };
    }

    /// <summary>Создать (orderId = null) или изменить заказ. Файл счёта копируется в хранилище.</summary>
    public static long Save(InvoiceDraft d, long? orderId = null)
    {
        if (d.Lines.Count == 0) throw new ArgumentException("В счёте нет ни одной позиции.");
        for (var i = 0; i < d.Lines.Count; i++)
        {
            var l = d.Lines[i];
            if (string.IsNullOrWhiteSpace(l.Name)) throw new ArgumentException($"Строка {i + 1}: не указано наименование.");
            if (l.Quantity <= 0) throw new ArgumentException($"Строка {i + 1}: количество должно быть больше нуля.");
            if (l.Price < 0) throw new ArgumentException($"Строка {i + 1}: цена не может быть отрицательной.");
        }

        var existing = orderId is { } oid ? Get(oid) ?? throw new ArgumentException("Заказ не найден.") : null;
        if (existing != null && !OrderStatus.IsOpen(existing.Status))
            throw new ArgumentException($"Заказ уже «{existing.Status}» — изменить нельзя.");

        // Файл уже в хранилище (редактирование) — не копируем повторно.
        var storedFile = existing?.FilePath;
        var copied = false;
        if (d.SourceFile != null && File.Exists(d.SourceFile) &&
            (storedFile == null || !Path.GetFullPath(d.SourceFile).Equals(Path.GetFullPath(AppPaths.ToAbsolute(storedFile)), StringComparison.OrdinalIgnoreCase)))
        {
            storedFile = FileStore.Import(d.SourceFile);
            copied = true;
        }

        try
        {
            using var c = Db.Open();
            using var tx = c.BeginTransaction();
            var supplierId = InvoiceRepo.GetOrCreateSupplier(c, tx, d.SupplierName, d.SupplierBin);
            var companyId = InvoiceRepo.CompanyIdInTx(c, tx, d.CompanyName);
            var args = new
            {
                number = Db.NullIfEmpty(d.Number), date = d.Date.Date, supplierId, companyId, total = d.Total,
                file = storedFile, notes = Db.NullIfEmpty(d.Notes), now = DateTime.Now, id = orderId,
            };
            long id;
            if (existing == null)
                id = c.ExecuteScalar<long>("""
                    INSERT INTO orders(number, date, supplier_id, company_id, total, file_path, notes, status, created_at)
                    VALUES (@number, @date, @supplierId, @companyId, @total, @file, @notes, 'Ждёт оплаты', @now) RETURNING id
                    """, args, tx);
            else
            {
                id = existing.Id;
                c.Execute("""
                    UPDATE orders SET number=@number, date=@date, supplier_id=@supplierId, company_id=@companyId,
                           total=@total, file_path=@file, notes=@notes WHERE id=@id
                    """, args, tx);
                c.Execute("DELETE FROM order_lines WHERE order_id = @id", new { id }, tx);
            }

            for (var i = 0; i < d.Lines.Count; i++)
            {
                var l = d.Lines[i];
                c.Execute("""
                    INSERT INTO order_lines(order_id, line_no, name, category, quantity, unit, price, employee_id, to_consumables)
                    VALUES (@id, @no, @name, @category, @quantity, @unit, @price, @emp, @cons)
                    """, new
                {
                    id, no = i + 1, name = l.Name.Trim(), category = Db.NullIfEmpty(l.Category), quantity = l.Quantity,
                    unit = Db.NullIfEmpty(l.Unit), price = l.Price, emp = l.ToConsumables ? null : l.EmployeeId, cons = l.ToConsumables,
                }, tx);
            }
            tx.Commit();
            return id;
        }
        catch
        {
            if (copied && storedFile != null) try { File.Delete(AppPaths.ToAbsolute(storedFile)); } catch { /* не критично */ }
            throw;
        }
    }

    /// <summary>Отметить оплату (paidAt = null — снять отметку).</summary>
    public static void SetPaid(long id, DateTime? paidAt)
    {
        var o = Get(id) ?? throw new ArgumentException("Заказ не найден.");
        if (!OrderStatus.IsOpen(o.Status)) throw new ArgumentException($"Заказ уже «{o.Status}».");
        using var c = Db.Open();
        c.Execute("UPDATE orders SET status = @s, paid_at = @paidAt WHERE id = @id", new
        {
            id, paidAt = paidAt?.Date, s = paidAt == null ? OrderStatus.AwaitingPayment : OrderStatus.Paid,
        });
    }

    public static void Cancel(long id)
    {
        var o = Get(id) ?? throw new ArgumentException("Заказ не найден.");
        if (!OrderStatus.IsOpen(o.Status)) throw new ArgumentException($"Заказ уже «{o.Status}».");
        using var c = Db.Open();
        c.Execute("UPDATE orders SET status = @s WHERE id = @id", new { id, s = OrderStatus.Cancelled });
    }

    /// <summary>Вернуть отменённый заказ в работу.</summary>
    public static void Reopen(long id)
    {
        var o = Get(id) ?? throw new ArgumentException("Заказ не найден.");
        if (o.Status != OrderStatus.Cancelled) throw new ArgumentException("Вернуть в работу можно только отменённый заказ.");
        using var c = Db.Open();
        c.Execute("UPDATE orders SET status = @s WHERE id = @id",
            new { id, s = o.PaidAt != null ? OrderStatus.Paid : OrderStatus.AwaitingPayment });
    }

    /// <summary>Удалить можно любой заказ, кроме полученного (по нему уже есть вещи).</summary>
    public static void Delete(long id)
    {
        var o = Get(id) ?? throw new ArgumentException("Заказ не найден.");
        if (o.Status == OrderStatus.Received) throw new ArgumentException("По заказу уже получен товар — удалить нельзя.");
        using (var c = Db.Open()) c.Execute("DELETE FROM orders WHERE id = @id", new { id });
        if (o.FilePath != null) try { File.Delete(AppPaths.ToAbsolute(o.FilePath)); } catch { /* не критично */ }
    }
}
