using Inventory.Data;
using Inventory.Models;

namespace Inventory.UI.Warehouse;

/// <summary>Раздел «Склад»: все вещи с фильтрами.</summary>
public sealed class WarehouseView : UserControl, IView
{
    private const string All = "Все";

    private readonly DataGridView _grid = new();
    private readonly ComboBox _status = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly ComboBox _company = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly CheckBox _noInv = new() { Text = "Без инв. номера", AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
    private readonly CheckBox _noSerial = new() { Text = "Без серийника", AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
    private readonly TextBox _text = new() { Width = 170, PlaceholderText = "Фильтр по тексту…" };
    private readonly Label _count = new() { AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(8, 7, 0, 0) };
    private readonly System.Windows.Forms.Timer _textDelay = new() { Interval = 250 };
    private bool _loadingFilters;

    public WarehouseView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);

        Ui.StyleGrid(_grid);
        _grid.MultiSelect = true;
        _grid.Columns.AddRange(
            Ui.Col(nameof(Item.InventoryNumber), "Инв. номер", 70),
            Ui.Col(nameof(Item.Name), "Наименование", 200),
            Ui.Col(nameof(Item.SerialNumber), "Серийник", 100),
            Ui.Col(nameof(Item.Status), "Статус", 65),
            Ui.Col(nameof(Item.EmployeeName), "У кого", 120),
            Ui.Col(nameof(Item.CompanyName), "Компания", 80),
            Ui.Col(nameof(Item.ArrivalDate), "Дата прихода", 90, "dd.MM.yyyy"));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        _grid.CellFormatting += OnCellFormatting;
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };

        var actions = Ui.Toolbar();
        actions.Controls.AddRange([
            Ui.Button("Добавить", (_, _) => Add(), primary: true),
            Ui.Button("Открыть", (_, _) => OpenSelected()),
            Ui.Button("Удалить", (_, _) => DeleteSelected()),
            new Label { Width = 16 },
            Ui.Button("Выдать", (_, _) => Act(Moves.ItemActions.Issue)),
            Ui.Button("Вернуть на склад", (_, _) => Act(Moves.ItemActions.Return)),
            Ui.Button("Передать", (_, _) => Act(Moves.ItemActions.Transfer)),
            Ui.Button("В ремонт", (_, _) => Act(Moves.ItemActions.Repair)),
            Ui.Button("Списать", (_, _) => Act(Moves.ItemActions.WriteOff)),
            new Label { Width = 16 },
            Ui.Button("QR-наклейки", (_, _) => Docs.PrintQr(FindForm(), SelectedItems())),
            Ui.Button("Экспорт в Excel", (_, _) => ExportExcel()),
            _count,
        ]);

        var filters = Ui.Toolbar();
        filters.Controls.AddRange([
            Caption("Статус"), _status, Caption("Категория"), _category, Caption("Компания"), _company,
            _text, _noInv, _noSerial,
        ]);

        foreach (var cb in new[] { _status, _category, _company })
            cb.SelectedIndexChanged += (_, _) => { if (!_loadingFilters) LoadItems(); };
        _noInv.CheckedChanged += (_, _) => LoadItems();
        _noSerial.CheckedChanged += (_, _) => LoadItems();
        _textDelay.Tick += (_, _) => { _textDelay.Stop(); LoadItems(); };
        _text.TextChanged += (_, _) => { _textDelay.Stop(); _textDelay.Start(); };

        Controls.Add(_grid);
        Controls.Add(filters);
        Controls.Add(actions);
        Controls.Add(Ui.Title("Склад"));
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(6, 7, 0, 0) };

    public void RefreshData()
    {
        Ui.Safe(FindForm(), LoadFilterLists, "Не удалось загрузить фильтры");
        LoadItems();
    }

    private void LoadFilterLists()
    {
        _loadingFilters = true;
        try
        {
            Refill(_status, ItemStatus.All.Cast<object>());
            Refill(_category, ItemRepo.Categories().Cast<object>());
            Refill(_company, CompanyRepo.All().Cast<object>());
        }
        finally { _loadingFilters = false; }
    }

    /// <summary>Перезаполнить список, сохранив выбранное значение.</summary>
    private static void Refill(ComboBox cb, IEnumerable<object> values)
    {
        var selected = cb.SelectedItem?.ToString();
        cb.Items.Clear();
        cb.Items.Add(All);
        cb.Items.AddRange(values.ToArray());
        var index = selected == null ? 0 : cb.Items.Cast<object>().ToList().FindIndex(o => o.ToString() == selected);
        cb.SelectedIndex = Math.Max(0, index);
    }

    private ItemFilter CurrentFilter() => new()
    {
        Status = _status.SelectedItem is string s && s != All ? s : null,
        Category = _category.SelectedItem is string c && c != All ? c : null,
        CompanyId = (_company.SelectedItem as Company)?.Id,
        NoInventoryNumber = _noInv.Checked,
        NoSerial = _noSerial.Checked,
        Text = _text.Text,
    };

    private void LoadItems() => Ui.Safe(FindForm(), () =>
    {
        var keepId = SelectedItems().FirstOrDefault()?.Id;
        var items = ItemRepo.List(CurrentFilter());
        _grid.DataSource = items;
        _count.Text = $"Показано: {items.Count}";
        if (keepId != null)
            foreach (DataGridViewRow row in _grid.Rows)
                if (((Item)row.DataBoundItem!).Id == keepId) { _grid.CurrentCell = row.Cells[0]; break; }
    }, "Не удалось загрузить склад");

    /// <summary>Текущая таблица с учётом фильтров (для экспорта на этапе 5).</summary>
    public List<Item> CurrentItems => _grid.DataSource as List<Item> ?? [];

    public List<Item> SelectedItems() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (Item)r.DataBoundItem!).ToList();

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != nameof(Item.Status)) return;
        e.CellStyle!.ForeColor = (e.Value as string) switch
        {
            ItemStatus.InStock => Color.FromArgb(22, 163, 74),
            ItemStatus.Issued => Ui.Accent,
            ItemStatus.Repair => Color.FromArgb(217, 119, 6),
            ItemStatus.WrittenOff => Color.Gray,
            _ => e.CellStyle.ForeColor,
        };
    }

    /// <summary>Действие над выделенными строками (можно несколько — Ctrl/Shift).</summary>
    private void Act(Func<IWin32Window?, IReadOnlyList<Item>, bool> action)
    {
        var ids = SelectedItems().Select(i => i.Id).ToList();
        // Берём свежие данные из БД — список мог устареть.
        var fresh = ids.Select(ItemRepo.Get).OfType<Item>().ToList();
        if (action(FindForm(), fresh)) RefreshData();
    }

    /// <summary>Экспорт текущей таблицы с учётом фильтров.</summary>
    private void ExportExcel()
    {
        var items = CurrentItems;
        if (items.Count == 0) { Ui.Info(FindForm(), "Таблица пуста — нечего выгружать."); return; }
        var filter = CurrentFilter();
        var parts = new[]
        {
            filter.Status, filter.Category, (_company.SelectedItem as Company)?.Name,
            filter.NoInventoryNumber ? "без инв. номера" : null, filter.NoSerial ? "без серийника" : null,
            string.IsNullOrWhiteSpace(filter.Text) ? null : $"«{filter.Text.Trim()}»",
        }.Where(p => p != null);
        var title = "Склад" + (parts.Any() ? ": " + string.Join(", ", parts) : "");
        Docs.SaveAndOpen(FindForm(), $"Склад_{DateTime.Now:yyyy-MM-dd}.xlsx", Docs.ExcelFilter,
            path => Services.Documents.ExcelExport.Items(path, title, items));
    }

    private void Add()
    {
        using var f = new ItemForm();
        if (f.ShowDialog(FindForm()) == DialogResult.OK) RefreshData();
    }

    private void OpenSelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is not Item sel) return;
        OpenItem(FindForm(), sel.Id);
        RefreshData();
    }

    /// <summary>Открыть карточку вещи по id (используется и из поиска).</summary>
    public static void OpenItem(IWin32Window? owner, long id)
    {
        var item = ItemRepo.Get(id);
        if (item == null) { Ui.Warn(owner, "Вещь не найдена — возможно, удалена."); return; }
        using var f = new ItemForm(item);
        f.ShowDialog(owner);
    }

    private void DeleteSelected()
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        if (items.Count > 1) { Ui.Warn(FindForm(), "Удалять можно только по одной карточке."); return; }
        var item = items[0];
        if (!ItemRepo.CanDelete(item.Id))
        {
            Ui.Warn(FindForm(), "У этой вещи есть движения (выдача, ремонт и т.п.) — удалить нельзя, только списать.");
            return;
        }
        if (!Ui.Confirm(FindForm(), $"Удалить карточку «{item.Name}»?\nЭто действие нельзя отменить.")) return;
        Ui.Safe(FindForm(), () => ItemRepo.Delete(item.Id), "Не удалось удалить");
        RefreshData();
    }
}
