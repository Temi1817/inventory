using Inventory.Data;
using Inventory.Models;

namespace Inventory.UI.Moves;

/// <summary>
/// Действия над вещами: проверка статусов → диалог → запись в БД.
/// Возвращают true, если что-то изменилось (нужно обновить экран).
/// </summary>
public static class ItemActions
{
    public static bool Issue(IWin32Window? owner, IReadOnlyList<Item> items)
    {
        if (!Check(owner, items, [ItemStatus.InStock], "Выдать можно только вещи со статусом «На складе».\nВыданную вещь — «Передать другому», из ремонта — сначала «Вернуть на склад».")) return false;
        using var d = new MoveDialog("Выдача", Describe(items), "Выдать", chooseEmployee: true);
        if (d.ShowDialog(owner) != DialogResult.OK) return false;
        return Run(owner, () => MovementRepo.Issue(Ids(items), d.EmployeeId!.Value, d.Date, d.Comment));
    }

    /// <summary>«Выдать ещё» со страницы сотрудника: выбор вещей со склада.</summary>
    public static bool IssueTo(IWin32Window? owner, Employee employee)
    {
        if (!employee.IsActive) { Ui.Warn(owner, "Сотрудник отмечен как уволенный — выдавать ему нельзя."); return false; }
        using var picker = new ItemPickerDialog($"Выдать: {employee.FullName} — выберите вещи со склада");
        if (picker.ShowDialog(owner) != DialogResult.OK) return false;
        var items = picker.SelectedItems;
        using var d = new MoveDialog("Выдача", Describe(items), "Выдать", fixedEmployee: employee);
        if (d.ShowDialog(owner) != DialogResult.OK) return false;
        return Run(owner, () => MovementRepo.Issue(Ids(items), employee.Id, d.Date, d.Comment));
    }

    public static bool Transfer(IWin32Window? owner, IReadOnlyList<Item> items)
    {
        if (!Check(owner, items, [ItemStatus.Issued], "Передать можно только выданные вещи.")) return false;
        var holders = items.Select(i => i.EmployeeId).Distinct().ToList();
        var exclude = holders.Count == 1 ? holders[0] : null;
        var from = holders.Count == 1 ? $"\nСейчас у: {items[0].EmployeeName}" : "";
        using var d = new MoveDialog("Передача другому сотруднику", Describe(items) + from, "Передать",
            chooseEmployee: true, excludeEmployeeId: exclude);
        if (d.ShowDialog(owner) != DialogResult.OK) return false;
        return Run(owner, () => MovementRepo.Transfer(Ids(items), d.EmployeeId!.Value, d.Date, d.Comment));
    }

    public static bool Return(IWin32Window? owner, IReadOnlyList<Item> items)
    {
        if (!Check(owner, items, [ItemStatus.Issued, ItemStatus.Repair], "Вернуть на склад можно выданные вещи или вещи из ремонта.")) return false;
        using var d = new MoveDialog("Возврат на склад", Describe(items), "Вернуть");
        if (d.ShowDialog(owner) != DialogResult.OK) return false;
        return Run(owner, () => MovementRepo.Return(Ids(items), d.Date, d.Comment));
    }

    public static bool Repair(IWin32Window? owner, IReadOnlyList<Item> items)
    {
        if (!Check(owner, items, [ItemStatus.InStock, ItemStatus.Issued], "В ремонт можно отправить вещь со склада или выданную.")) return false;
        using var d = new MoveDialog("Отправка в ремонт", Describe(items), "В ремонт", commentLabel: "Неисправность");
        if (d.ShowDialog(owner) != DialogResult.OK) return false;
        return Run(owner, () => MovementRepo.Repair(Ids(items), d.Date, d.Comment));
    }

    public static bool WriteOff(IWin32Window? owner, IReadOnlyList<Item> items)
    {
        if (!Check(owner, items, [ItemStatus.InStock, ItemStatus.Issued, ItemStatus.Repair], "Вещь уже списана.")) return false;
        using var d = new MoveDialog("Списание", Describe(items), "Списать", commentLabel: "Причина");
        if (d.ShowDialog(owner) != DialogResult.OK) return false;
        if (!Ui.Confirm(owner, $"Списать {Count(items)}?\nСписанную вещь нельзя выдать, она останется только в истории."))
            return false;
        return Run(owner, () => MovementRepo.WriteOff(Ids(items), d.Date, d.Comment));
    }

    private static bool Check(IWin32Window? owner, IReadOnlyList<Item> items, string[] allowed, string message)
    {
        if (items.Count == 0) { Ui.Warn(owner, "Сначала выберите вещи в таблице."); return false; }
        var bad = items.Where(i => !allowed.Contains(i.Status)).ToList();
        if (bad.Count == 0) return true;
        var list = string.Join("\n", bad.Take(8).Select(i => $"• {i.Name} — {i.Status}"));
        if (bad.Count > 8) list += $"\n… и ещё {bad.Count - 8}";
        Ui.Warn(owner, $"{message}\n\nНе подходят:\n{list}");
        return false;
    }

    private static bool Run(IWin32Window? owner, Action action)
    {
        var ok = false;
        Ui.Safe(owner, () => { action(); ok = true; }, "Не удалось выполнить операцию");
        return ok;
    }

    private static List<long> Ids(IEnumerable<Item> items) => items.Select(i => i.Id).ToList();

    private static string Count(IReadOnlyList<Item> items) =>
        items.Count == 1 ? $"«{items[0].Name}»" : $"{items.Count} шт.";

    /// <summary>Короткое описание: одна вещь — с инв. номером, несколько — первые три.</summary>
    internal static string Describe(IReadOnlyList<Item> items)
    {
        static string One(Item i) => i.Name + (string.IsNullOrEmpty(i.InventoryNumber) ? "" : $" (инв. {i.InventoryNumber})");
        if (items.Count == 1) return One(items[0]);
        var head = string.Join("\n", items.Take(3).Select(i => "• " + One(i)));
        return $"{items.Count} шт.:\n{head}" + (items.Count > 3 ? $"\n… и ещё {items.Count - 3}" : "");
    }
}
