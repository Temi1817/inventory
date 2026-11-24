using Dapper;
using Inventory.Models;

namespace Inventory.Data;

/// <summary>Компания + сколько раз она используется (для справочника).</summary>
public sealed class CompanyUsage
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long Employees { get; set; }
    public long Items { get; set; }
    public long Invoices { get; set; }
    public bool IsUsed => Employees + Items + Invoices > 0;
}

public static class CompanyRepo
{
    public static List<Company> All()
    {
        using var c = Db.Open();
        return c.Query<Company>("SELECT * FROM companies ORDER BY name").ToList();
    }

    public static List<CompanyUsage> WithUsage()
    {
        using var c = Db.Open();
        return c.Query<CompanyUsage>("""
            SELECT co.id, co.name,
                   (SELECT COUNT(*) FROM employees e WHERE e.company_id = co.id) AS employees,
                   (SELECT COUNT(*) FROM items i WHERE i.company_id = co.id) AS items,
                   (SELECT COUNT(*) FROM invoices v WHERE v.company_id = co.id) AS invoices
            FROM companies co ORDER BY co.name
            """).ToList();
    }

    /// <summary>Найти компанию по имени (без учёта регистра) или создать. Пустое имя → null.</summary>
    public static long? GetOrCreate(string? name)
    {
        name = Db.NullIfEmpty(name);
        if (name == null) return null;
        using var c = Db.Open();
        var id = c.ExecuteScalar<long?>("SELECT id FROM companies WHERE ulower(name) = @n", new { n = name.ToLowerInvariant() });
        return id ?? c.ExecuteScalar<long>("INSERT INTO companies(name) VALUES (@name) RETURNING id", new { name });
    }

    public static long Add(string name)
    {
        name = Validate(name, 0);
        using var c = Db.Open();
        return c.ExecuteScalar<long>("INSERT INTO companies(name) VALUES (@name) RETURNING id", new { name });
    }

    /// <summary>Переименование — меняется везде сразу (сотрудники, вещи, накладные ссылаются по id).</summary>
    public static void Rename(long id, string name)
    {
        name = Validate(name, id);
        using var c = Db.Open();
        c.Execute("UPDATE companies SET name = @name WHERE id = @id", new { id, name });
    }

    /// <summary>Удалить можно только неиспользуемую компанию.</summary>
    public static void Delete(long id)
    {
        var usage = WithUsage().FirstOrDefault(u => u.Id == id) ?? throw new ArgumentException("Компания не найдена.");
        if (usage.IsUsed)
            throw new ArgumentException(
                $"Компания «{usage.Name}» используется: сотрудников {usage.Employees}, вещей {usage.Items}, накладных {usage.Invoices}.\n" +
                "Удалить можно только неиспользуемую. Если нужно другое название — переименуйте.");
        using var c = Db.Open();
        c.Execute("DELETE FROM companies WHERE id = @id", new { id });
    }

    private static string Validate(string? name, long exceptId)
    {
        name = Db.NullIfEmpty(name) ?? throw new ArgumentException("Введите название компании.");
        if (name.Length > 200) throw new ArgumentException("Слишком длинное название.");
        using var c = Db.Open();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM companies WHERE ulower(name) = @n AND id <> @exceptId",
                new { n = name.ToLowerInvariant(), exceptId }) > 0)
            throw new ArgumentException($"Компания «{name}» уже есть.");
        return name;
    }
}
