using Inventory.Data;
using Inventory.Models;
using Inventory.UI.Moves;
using Inventory.UI.Warehouse;

namespace Inventory.UI.Employees;

/// <summary>Страница сотрудника: данные + всё, что за ним сейчас числится.</summary>
public sealed class EmployeeCard : Form
{
    private Employee _employee;
    private readonly Label _name = new() { Font = Ui.TitleFont, AutoSize = true };
    private readonly Label _fired = new() { Text = "УВОЛЕН", ForeColor = Ui.Danger, Font = Ui.BoldFont, AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
    private readonly Label _info = new() { AutoSize = true, ForeColor = Color.FromArgb(51, 65, 85), Padding = new Padding(0, 4, 0, 0) };
    private readonly Label _notes = new() { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(900, 0), Padding = new Padding(0, 4, 0, 0) };
    private readonly Label _ordered = new() { AutoSize = true, ForeColor = Ui.Accent, MaximumSize = new Size(1000, 0), Padding = new Padding(0, 6, 0, 0) };
    private readonly Label _count = new() { Dock = DockStyle.Top, Height = 34, TextAlign = ContentAlignment.BottomLeft, Font = Ui.BoldFont, Padding = new Padding(0, 10, 0, 4) };
    private readonly DataGridView _grid = new();

    public EmployeeCard(Employee employee)
    {
        _employee = employee;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 520);
        ClientSize = new Size(1120, 660);
        BackColor = Color.White;

        Ui.StyleGrid(_grid);
        _grid.MultiSelect = true;
        _grid.Columns.AddRange(
            Ui.Col(nameof(Item.InventoryNumber), "Инв. номер", 70),
            Ui.Col(nameof(Item.Name), "Что", 220),
            Ui.Col(nameof(Item.SerialNumber), "Серийник", 110),
            Ui.Col(nameof(Item.Category), "Категория", 80),
            Ui.Col(nameof(Item.IssuedDate), "Дата выдачи", 70, "dd.MM.yyyy"));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenItem(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenItem(); e.Handled = true; } };

        var nameRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
        var edit = new LinkLabel { Text = "изменить данные", AutoSize = true, Padding = new Padding(12, 9, 0, 0) };
        edit.LinkClicked += (_, _) => EditEmployee();
        nameRow.Controls.AddRange([_name, _fired, edit]);

        var header = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
        var headerStack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        headerStack.Controls.AddRange([nameRow, _info, _notes, _ordered]);
        header.Controls.Add(headerStack);

        var actions = Ui.Toolbar();
        actions.Padding = new Padding(0, 8, 0, 0);
        actions.Controls.AddRange([
            Ui.Button("Выдать ещё", (_, _) => Act(() => ItemActions.IssueTo(this, _employee)), primary: true),
            Ui.Button("Вернуть выбранные", (_, _) => Act(() => ItemActions.Return(this, Selected()))),
            Ui.Button("Вернуть всё на склад", (_, _) => ActAll(ItemActions.Return)),
            Ui.Button("Передать всё другому", (_, _) => ActAll(ItemActions.Transfer)),
            new Label { Width = 16 },
            Ui.Button("Акт выдачи (PDF)", (_, _) => MakeAct()),
            Ui.Button("Экспорт в Excel", (_, _) => ExportExcel()),
            Ui.Button("QR-наклейки", (_, _) => Docs.PrintQr(this, ActItems())),
        ]);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) };
        body.Controls.Add(_grid);
        body.Controls.Add(_count);
        body.Controls.Add(actions);
        body.Controls.Add(header);
        Controls.Add(body);

        Shown += (_, _) => RefreshData();
    }

    private List<Item> Items => _grid.DataSource as List<Item> ?? [];

    private List<Item> Selected() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (Item)r.DataBoundItem!).ToList();

    private void RefreshData() => Ui.Safe(this, () =>
    {
        _employee = EmployeeRepo.Get(_employee.Id) ?? _employee;
        var e = _employee;
        Text = "Сотрудник: " + e.FullName;
        _name.Text = e.FullName;
        _fired.Visible = !e.IsActive;
        _info.Text = string.Join("   ·   ", new[]
        {
            e.CompanyName, e.Department, e.Position,
            e.Workplace == null ? null : "Раб. место: " + e.Workplace,
            e.Phone == null ? null : "Тел.: " + e.Phone,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _notes.Text = e.Notes ?? "";
        _notes.Visible = e.Notes != null;

        var ordered = OrderRepo.OpenForEmployee(e.Id);
        _ordered.Visible = ordered.Count > 0;
        _ordered.Text = ordered.Count == 0 ? "" : "Заказано, ещё не пришло: " + string.Join(";  ", ordered.Select(o =>
            $"{o.Name}{(o.Quantity != 1 ? $" ×{o.Quantity:0.##}" : "")} (счёт № {o.OrderNumber ?? "—"}, {o.OrderStatus?.ToLowerInvariant()})"));
        var items = ItemRepo.List(new ItemFilter { EmployeeId = e.Id, Status = ItemStatus.Issued })
            .OrderBy(i => i.Name).ToList();
        _grid.DataSource = items;
        _count.Text = items.Count == 0 ? "За сотрудником ничего не числится" : $"Сейчас за сотрудником: {items.Count} шт.";
    }, "Не удалось загрузить данные сотрудника");

    private void Act(Func<bool> action)
    {
        if (action()) RefreshData();
    }

    private void ActAll(Func<IWin32Window?, IReadOnlyList<Item>, bool> action)
    {
        if (Items.Count == 0) { Ui.Info(this, "За сотрудником ничего не числится."); return; }
        if (action(this, Items)) RefreshData();
    }

    private void OpenItem()
    {
        if (_grid.CurrentRow?.DataBoundItem is not Item item) return;
        WarehouseView.OpenItem(this, item.Id);
        RefreshData();
    }

    /// <summary>Для акта: выделено несколько строк — только они, иначе всё, что числится.</summary>
    private List<Item> ActItems()
    {
        var selected = Selected();
        return selected.Count > 1 ? selected.OrderBy(i => i.Name).ToList() : Items;
    }

    private void MakeAct()
    {
        var items = ActItems();
        if (items.Count == 0) { Ui.Info(this, "За сотрудником ничего не числится — акт не нужен."); return; }
        var company = _employee.CompanyName ?? items.Select(i => i.CompanyName).FirstOrDefault(c => c != null);
        Docs.SaveAndOpen(this, $"Акт_выдачи_{_employee.FullName}_{DateTime.Now:yyyy-MM-dd}.pdf", Docs.PdfFilter,
            path => Services.Documents.ActPdf.Generate(path, _employee, items, DateTime.Today, company, Infrastructure.AppSettings.Load().IssuerName));
    }

    private void ExportExcel()
    {
        if (Items.Count == 0) { Ui.Info(this, "За сотрудником ничего не числится."); return; }
        Docs.SaveAndOpen(this, $"Вещи_{_employee.FullName}_{DateTime.Now:yyyy-MM-dd}.xlsx", Docs.ExcelFilter,
            path => Services.Documents.ExcelExport.EmployeeItems(path, _employee, Items));
    }

    private void EditEmployee()
    {
        using var f = new EmployeeForm(_employee);
        if (f.ShowDialog(this) == DialogResult.OK) RefreshData();
    }

    /// <summary>Открыть страницу сотрудника по id.</summary>
    public static void Open(IWin32Window? owner, long id)
    {
        var e = EmployeeRepo.Get(id);
        if (e == null) { Ui.Warn(owner, "Сотрудник не найден."); return; }
        using var f = new EmployeeCard(e);
        f.ShowDialog(owner);
    }
}
