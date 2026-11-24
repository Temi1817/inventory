using Dapper;
using Inventory.Models;

namespace Inventory.Data;

/// <summary>Движение расходника (журнал consumable_moves).</summary>
public sealed class ConsumableMove
{
    public long Id { get; set; }
    public long ConsumableId { get; set; }
    public DateTime Date { get; set; }
    public decimal Quantity { get; set; }
    public long? EmployeeId { get; set; }
    public long? InvoiceId { get; set; }
    public string? Comment { get; set; }
    public string? EmployeeName { get; set; }
    public string Kind => Quantity >= 0 ? "Приход" : "Выдача";
    public decimal Amount => Math.Abs(Quantity);
}

public static class ConsumableRepo
{
    public static List<Consumable> List(string? text = null)
    {
        var sql = "SELECT * FROM consumables";
        if (!string.IsNullOrWhiteSpace(text))
            sql += " WHERE ulower(name) LIKE @q ESCAPE '\\' OR ulower(category) LIKE @q ESCAPE '\\'";
        sql += " ORDER BY (quantity < min_quantity) DESC, name";
        using var c = Db.Open();
        return c.Query<Consumable>(sql, new { q = Db.Like(text ?? "") }).ToList();
    }

    public static Consumable? Get(long id)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<Consumable>("SELECT * FROM consumables WHERE id = @id", new { id });
    }

    public static List<string> Categories()
    {
        using var c = Db.Open();
        return c.Query<string>("SELECT DISTINCT category FROM consumables WHERE category IS NOT NULL AND category <> '' ORDER BY category").ToList();
    }

    /// <summary>Новый расходник; начальный остаток записывается в журнал как приход.</summary>
    public static long Insert(Consumable item, string? comment = "Начальный остаток")
    {
        Normalize(item);
        var initial = item.Quantity;
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM consumables WHERE ulower(name) = @n", new { n = item.Name.ToLowerInvariant() }, tx) > 0)
            throw new ArgumentException($"Расходник «{item.Name}» уже есть — используйте «+ Приход».");
        item.Id = c.ExecuteScalar<long>("""
            INSERT INTO consumables(name, category, quantity, min_quantity, unit, notes)
            VALUES (@Name, @Category, @Quantity, @MinQuantity, @Unit, @Notes) RETURNING id
            """, item, tx);
        if (initial > 0)
            c.Execute("INSERT INTO consumable_moves(consumable_id, date, quantity, comment) VALUES (@Id, @date, @initial, @comment)",
                new { item.Id, date = DateTime.Now, initial, comment }, tx);
        tx.Commit();
        return item.Id;
    }

    /// <summary>Изменить карточку (остаток меняется только через приход/выдачу).</summary>
    public static void Update(Consumable item)
    {
        Normalize(item);
        using var c = Db.Open();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM consumables WHERE ulower(name) = @n AND id <> @Id",
                new { n = item.Name.ToLowerInvariant(), item.Id }) > 0)
            throw new ArgumentException($"Расходник «{item.Name}» уже есть.");
        c.Execute("UPDATE consumables SET name=@Name, category=@Category, min_quantity=@MinQuantity, unit=@Unit, notes=@Notes WHERE id=@Id", item);
    }

    public static void Delete(long id)
    {
        using var c = Db.Open();
        c.Execute("DELETE FROM consumables WHERE id = @id", new { id });   // журнал удаляется каскадом
    }

    /// <summary>Приход (quantity &gt; 0) или выдача (quantity &lt; 0). Остаток не может уйти в минус.</summary>
    public static void Move(long id, decimal quantity, DateTime date, long? employeeId, string? comment)
    {
        if (quantity == 0) throw new ArgumentException("Количество должно быть больше нуля.");
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        var stock = c.ExecuteScalar<decimal?>("SELECT quantity FROM consumables WHERE id = @id", new { id }, tx)
                    ?? throw new ArgumentException("Расходник не найден.");
        if (stock + quantity < 0)
            throw new ArgumentException($"Не хватает остатка: на складе {stock:0.##}, выдаётся {-quantity:0.##}.");
        c.Execute("UPDATE consumables SET quantity = quantity + @quantity WHERE id = @id", new { id, quantity }, tx);
        c.Execute("""
            INSERT INTO consumable_moves(consumable_id, date, quantity, employee_id, comment)
            VALUES (@id, @date, @quantity, @employeeId, @comment)
            """, new { id, date = MovementRepo.Stamp(date), quantity, employeeId, comment = Db.NullIfEmpty(comment) }, tx);
        tx.Commit();
    }

    public static List<ConsumableMove> Moves(long id)
    {
        using var c = Db.Open();
        return c.Query<ConsumableMove>("""
            SELECT m.*, e.full_name AS employee_name FROM consumable_moves m
            LEFT JOIN employees e ON e.id = m.employee_id
            WHERE m.consumable_id = @id ORDER BY m.date DESC, m.id DESC
            """, new { id }).ToList();
    }

    private static void Normalize(Consumable i)
    {
        i.Name = i.Name.Trim();
        if (i.Name.Length == 0) throw new ArgumentException("Укажите наименование.");
        if (i.MinQuantity < 0 || i.Quantity < 0) throw new ArgumentException("Количество не может быть отрицательным.");
        i.Category = Db.NullIfEmpty(i.Category);
        i.Unit = Db.NullIfEmpty(i.Unit) ?? "шт";
        i.Notes = Db.NullIfEmpty(i.Notes);
    }
}
