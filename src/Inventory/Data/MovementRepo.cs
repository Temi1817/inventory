using Dapper;
using Inventory.Models;

namespace Inventory.Data;

/// <summary>
/// Движения вещей: выдача, возврат, передача, ремонт, списание.
/// Меняет status и employee_id и пишет историю — всё в одной транзакции.
/// </summary>
public static class MovementRepo
{
    public static void Issue(IReadOnlyCollection<long> ids, long employeeId, DateTime date, string? comment) =>
        Move(ids, date, comment, HistoryAction.Issue, ItemStatus.Issued, employeeId,
            [ItemStatus.InStock], "выдать можно только вещи, которые на складе");

    public static void Transfer(IReadOnlyCollection<long> ids, long toEmployeeId, DateTime date, string? comment) =>
        Move(ids, date, comment, HistoryAction.Transfer, ItemStatus.Issued, toEmployeeId,
            [ItemStatus.Issued], "передать можно только выданные вещи");

    public static void Return(IReadOnlyCollection<long> ids, DateTime date, string? comment) =>
        Move(ids, date, comment, HistoryAction.Return, ItemStatus.InStock, null,
            [ItemStatus.Issued, ItemStatus.Repair], "вернуть на склад можно выданные вещи или вещи из ремонта");

    public static void Repair(IReadOnlyCollection<long> ids, DateTime date, string? comment) =>
        Move(ids, date, comment, HistoryAction.Repair, ItemStatus.Repair, null,
            [ItemStatus.InStock, ItemStatus.Issued], "в ремонт можно отправить вещь со склада или выданную");

    public static void WriteOff(IReadOnlyCollection<long> ids, DateTime date, string? comment) =>
        Move(ids, date, comment, HistoryAction.WriteOff, ItemStatus.WrittenOff, null,
            [ItemStatus.InStock, ItemStatus.Issued, ItemStatus.Repair], "вещь уже списана");

    /// <summary>Дата движения: сегодня — с текущим временем (для порядка в истории), иначе — сама дата.</summary>
    internal static DateTime Stamp(DateTime date) => date.Date == DateTime.Today ? DateTime.Now : date.Date;

    private static void Move(IReadOnlyCollection<long> ids, DateTime date, string? comment, string action,
        string newStatus, long? toEmployeeId, string[] allowedFrom, string rule)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return;

        using var c = Db.Open();
        using var tx = c.BeginTransaction();

        var items = c.Query<Item>("SELECT id, name, inventory_number, status, employee_id FROM items WHERE id IN @idList",
            new { idList }, tx).ToList();
        if (items.Count != idList.Count)
            throw new InvalidOperationException("Часть вещей не найдена — возможно, они удалены. Обновите список.");

        var bad = items.Where(i => !allowedFrom.Contains(i.Status)).ToList();
        if (bad.Count > 0)
            throw new ArgumentException($"Нельзя: {rule}.\n\n" + List(bad, i => $"{i.Name} — {i.Status}"));

        if (toEmployeeId != null)
        {
            var active = c.ExecuteScalar<long?>("SELECT is_active FROM employees WHERE id=@toEmployeeId", new { toEmployeeId }, tx);
            if (active == null) throw new ArgumentException("Сотрудник не найден.");
            if (active == 0) throw new ArgumentException("Сотрудник отмечен как уволенный — выдавать ему нельзя.");

            var already = items.Where(i => i.EmployeeId == toEmployeeId).ToList();
            if (already.Count > 0)
                throw new ArgumentException("Эти вещи уже числятся за этим сотрудником:\n\n" + List(already, i => i.Name));
        }

        var stamp = Stamp(date);
        foreach (var item in items)
        {
            c.Execute("UPDATE items SET status=@newStatus, employee_id=@toEmployeeId WHERE id=@Id",
                new { newStatus, toEmployeeId, item.Id }, tx);
            HistoryRepo.Add(c, tx, new HistoryEntry
            {
                ItemId = item.Id, Date = stamp, Action = action,
                FromEmployeeId = item.EmployeeId, ToEmployeeId = toEmployeeId, Comment = Db.NullIfEmpty(comment),
            });
        }
        tx.Commit();
    }

    private static string List(List<Item> items, Func<Item, string> line)
    {
        var text = string.Join("\n", items.Take(10).Select(i => "• " + line(i)));
        return items.Count > 10 ? text + $"\n… и ещё {items.Count - 10}" : text;
    }
}
