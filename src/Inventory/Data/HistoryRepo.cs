using System.Data;
using Dapper;
using Inventory.Models;

namespace Inventory.Data;

public static class HistoryRepo
{
    public static List<HistoryEntry> ForItem(long itemId)
    {
        using var c = Db.Open();
        return c.Query<HistoryEntry>("""
            SELECT h.*, f.full_name AS from_name, t.full_name AS to_name
            FROM history h
            LEFT JOIN employees f ON f.id = h.from_employee_id
            LEFT JOIN employees t ON t.id = h.to_employee_id
            WHERE h.item_id = @itemId
            ORDER BY h.date DESC, h.id DESC
            """, new { itemId }).ToList();
    }

    public static void Add(IDbConnection c, IDbTransaction tx, HistoryEntry h) =>
        c.Execute("""
            INSERT INTO history(item_id, date, action, from_employee_id, to_employee_id, comment)
            VALUES (@ItemId, @Date, @Action, @FromEmployeeId, @ToEmployeeId, @Comment)
            """, h, tx);
}
