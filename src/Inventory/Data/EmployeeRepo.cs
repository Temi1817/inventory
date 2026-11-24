using Dapper;
using Inventory.Models;

namespace Inventory.Data;

public static class EmployeeRepo
{
    private const string Select = """
        SELECT e.*, c.name AS company_name,
               (SELECT COUNT(*) FROM items i WHERE i.employee_id = e.id) AS item_count
        FROM employees e
        LEFT JOIN companies c ON c.id = e.company_id
        """;

    public static List<Employee> List(string? search = null, bool includeInactive = false)
    {
        var sql = Select + " WHERE 1=1";
        if (!includeInactive) sql += " AND e.is_active = 1";
        if (!string.IsNullOrWhiteSpace(search))
            sql += """
                 AND (ulower(e.full_name) LIKE @q ESCAPE '\' OR ulower(e.department) LIKE @q ESCAPE '\'
                   OR ulower(e.position) LIKE @q ESCAPE '\' OR ulower(e.phone) LIKE @q ESCAPE '\'
                   OR ulower(e.workplace) LIKE @q ESCAPE '\' OR ulower(c.name) LIKE @q ESCAPE '\')
                """;
        sql += " ORDER BY e.is_active DESC, e.full_name";
        using var c = Db.Open();
        return c.Query<Employee>(sql, new { q = Db.Like(search ?? "") }).ToList();
    }

    public static Employee? Get(long id)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<Employee>(Select + " WHERE e.id = @id", new { id });
    }

    public static Employee? FindByName(string fullName)
    {
        using var c = Db.Open();
        return c.QueryFirstOrDefault<Employee>(Select + " WHERE ulower(e.full_name) = @n",
            new { n = fullName.Trim().ToLowerInvariant() });
    }

    public static long Insert(Employee e)
    {
        Normalize(e);
        e.CreatedAt = DateTime.Now;
        using var c = Db.Open();
        e.Id = c.ExecuteScalar<long>("""
            INSERT INTO employees(full_name, company_id, department, position, phone, workplace, notes, is_active, created_at)
            VALUES (@FullName, @CompanyId, @Department, @Position, @Phone, @Workplace, @Notes, @IsActive, @CreatedAt)
            RETURNING id
            """, e);
        return e.Id;
    }

    public static void Update(Employee e)
    {
        Normalize(e);
        using var c = Db.Open();
        c.Execute("""
            UPDATE employees SET full_name=@FullName, company_id=@CompanyId, department=@Department,
                   position=@Position, phone=@Phone, workplace=@Workplace, notes=@Notes, is_active=@IsActive
            WHERE id=@Id
            """, e);
    }

    public static void SetActive(long id, bool active)
    {
        using var c = Db.Open();
        c.Execute("UPDATE employees SET is_active=@active WHERE id=@id", new { id, active });
    }

    public static List<string> Departments() => Distinct("department");
    public static List<string> Positions() => Distinct("position");

    private static List<string> Distinct(string column)
    {
        using var c = Db.Open();
        return c.Query<string>(
            $"SELECT DISTINCT {column} FROM employees WHERE {column} IS NOT NULL AND {column} <> '' ORDER BY {column}")
            .ToList();
    }

    private static void Normalize(Employee e)
    {
        e.FullName = e.FullName.Trim();
        if (e.FullName.Length == 0) throw new ArgumentException("ФИО обязательно.");
        e.Department = Db.NullIfEmpty(e.Department);
        e.Position = Db.NullIfEmpty(e.Position);
        e.Phone = Db.NullIfEmpty(e.Phone);
        e.Workplace = Db.NullIfEmpty(e.Workplace);
        e.Notes = Db.NullIfEmpty(e.Notes);
    }
}
