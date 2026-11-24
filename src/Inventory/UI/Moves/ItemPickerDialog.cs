using Inventory.Data;
using Inventory.Models;

namespace Inventory.UI.Moves;

/// <summary>Выбор нескольких вещей со склада (статус «На складе»).</summary>
public sealed class ItemPickerDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly TextBox _filter = new() { Width = 300, PlaceholderText = "Фильтр: наименование, серийник, инв. номер…" };
    private readonly Label _selected = new() { AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(12, 7, 0, 0) };
    private readonly HashSet<long> _checked = [];

    public List<Item> SelectedItems { get; } = [];

    public ItemPickerDialog(string title)
    {
        Text = title;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 450);
        ClientSize = new Size(860, 560);

        Ui.StyleGrid(_grid);
        var check = new DataGridViewCheckBoxColumn { HeaderText = "", Width = 36, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Name = "check" };
        _grid.Columns.Add(check);
        _grid.Columns.AddRange(
            Ui.Col(nameof(Item.InventoryNumber), "Инв. номер", 70),
            Ui.Col(nameof(Item.Name), "Наименование", 220),
            Ui.Col(nameof(Item.SerialNumber), "Серийник", 100),
            Ui.Col(nameof(Item.Category), "Категория", 80),
            Ui.Col(nameof(Item.CompanyName), "Компания", 80));
        // Галки — несвязанная колонка: восстанавливаем после каждой перепривязки данных.
        _grid.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewRow r in _grid.Rows)
                r.Cells[0].Value = _checked.Contains(((Item)r.DataBoundItem!).Id);
        };
        _grid.CellContentClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) Toggle(e.RowIndex); };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex > 0) Toggle(e.RowIndex); };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space) return;
            foreach (DataGridViewRow r in _grid.SelectedRows) Toggle(r.Index);
            e.Handled = true;
        };

        var top = Ui.Toolbar();
        top.Padding = new Padding(12, 12, 12, 8);
        top.Controls.AddRange([_filter, _selected]);
        _filter.TextChanged += (_, _) => LoadItems();

        var gridPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        gridPanel.Controls.Add(_grid);
        Controls.Add(gridPanel);
        Controls.Add(top);
        Controls.Add(FieldTable.DialogButtons(this, "Выбрать", (_, _) => Ok()));

        LoadItems();
    }

    private void LoadItems() => Ui.Safe(this, () =>
    {
        _grid.DataSource = ItemRepo.List(new ItemFilter { Status = ItemStatus.InStock, Text = _filter.Text });
        UpdateCounter();
    }, "Не удалось загрузить склад");

    private void Toggle(int rowIndex)
    {
        var row = _grid.Rows[rowIndex];
        var item = (Item)row.DataBoundItem!;
        if (!_checked.Remove(item.Id)) _checked.Add(item.Id);
        row.Cells[0].Value = _checked.Contains(item.Id);
        _grid.EndEdit();
        UpdateCounter();
    }

    private void UpdateCounter() =>
        _selected.Text = $"Отмечено: {_checked.Count}   (клик по галке, двойной клик или пробел)";

    private void Ok()
    {
        if (_checked.Count == 0)
        {
            Ui.Warn(this, "Отметьте галкой хотя бы одну вещь.");
            return;
        }
        SelectedItems.AddRange(_checked.Select(ItemRepo.Get).OfType<Item>());
        DialogResult = DialogResult.OK;
        Close();
    }
}
