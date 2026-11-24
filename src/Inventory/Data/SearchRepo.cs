using Dapper;
using Inventory.Models;

namespace Inventory.Data;

/// <summary>Глобальный поиск: вещи, сотрудники, накладные.</summary>
public static class SearchRepo
{
    public static List<SearchHit> Find(string text, int limitPerKind = 200)
    {
        var result = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var p = new { q = Db.Like(text), limit = limitPerKind };
        using var c = Db.Open();

        result.AddRange(c.Query<SearchHit>("""
            SELECT 'Вещь' AS kind, i.id,
                   i.name || COALESCE(' — инв. ' || i.inventory_number, '') AS title,
                   i.status || COALESCE(', ' || e.full_name, '') || COALESCE(', S/N ' || i.serial_number, '') AS details
            FROM items i LEFT JOIN employees e ON e.id = i.employee_id
            WHERE ulower(i.name) LIKE @q ESCAPE '\' OR ulower(i.serial_number) LIKE @q ESCAPE '\'
               OR ulower(i.inventory_number) LIKE @q ESCAPE '\'
            ORDER BY i.name LIMIT @limit
            """, p));

        result.AddRange(c.Query<SearchHit>("""
            SELECT 'Сотрудник' AS kind, e.id, e.full_name AS title,
                   TRIM(COALESCE(c.name, '') || COALESCE(', ' || e.department, '') || COALESCE(', ' || e.position, '')
                        || CASE WHEN e.is_active = 0 THEN ' (уволен)' ELSE '' END, ', ') AS details
            FROM employees e LEFT JOIN companies c ON c.id = e.company_id
            WHERE ulower(e.full_name) LIKE @q ESCAPE '\'
            ORDER BY e.full_name LIMIT @limit
            """, p));

        result.AddRange(c.Query<SearchHit>("""
            SELECT 'Накладная' AS kind, inv.id,
                   '№ ' || COALESCE(inv.number, '—') || COALESCE(' от ' || strftime('%d.%m.%Y', inv.date), '') AS title,
                   s.name AS details
            FROM invoices inv LEFT JOIN suppliers s ON s.id = inv.supplier_id
            WHERE ulower(inv.number) LIKE @q ESCAPE '\'
            ORDER BY inv.date DESC LIMIT @limit
            """, p));

        return result;
    }
}
