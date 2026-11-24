using Inventory.Data;
using Inventory.Models;
using Inventory.Services;

namespace Inventory.UI.Employees;

/// <summary>Раздел «Сотрудники».</summary>
public sealed class EmployeesView : UserControl, IView
{
    private static readonly Font FiredFont = new(Ui.BaseFont, FontStyle.Strikeout);
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = new() { Width = 260, PlaceholderText = "Поиск по ФИО, отделу, телефону…" };
    private readonly CheckBox _showFired = new() { Text = "Показать уволенных", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    private readonly Button _fireButton;
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 250 };

    public EmployeesView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);

        Ui.StyleGrid(_grid);
        _grid.Columns.AddRange(
            Ui.Col(nameof(Employee.FullName), "ФИО", 160),
            Ui.Col(nameof(Employee.CompanyName), "Компания", 90),
            Ui.Col(nameof(Employee.Department), "Отдел", 100),
            Ui.Col(nameof(Employee.Position), "Должность", 100),
            Ui.Col(nameof(Employee.Phone), "Телефон", 80),
            Ui.Col(nameof(Employee.Workplace), "Рабочее место", 90),
            Ui.Col(nameof(Employee.ItemCount), "Вещей", 45));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };
        _grid.CellFormatting += OnCellFormatting;
        _grid.SelectionChanged += (_, _) => UpdateButtons();

        _fireButton = Ui.Button("Уволен", (_, _) => ToggleFired());
        var toolbar = Ui.Toolbar();
        toolbar.Controls.AddRange([
            _search,
            _showFired,
            new Label { Width = 16 },
            Ui.Button("Добавить", (_, _) => Add(), primary: true),
            Ui.Button("Открыть", (_, _) => OpenSelected()),
            Ui.Button("Изменить", (_, _) => EditSelected()),
            _fireButton,
            new Label { Width = 16 },
            Ui.Button("Импорт CSV", (_, _) => ImportCsv()),
            Ui.Button("Экспорт CSV", (_, _) => ExportCsv()),
        ]);

        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); RefreshData(); };
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _showFired.CheckedChanged += (_, _) => RefreshData();

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(Ui.Title("Сотрудники"));
    }

    private Employee? Selected => _grid.CurrentRow?.DataBoundItem as Employee;

    public void RefreshData() => Ui.Safe(FindForm(), () =>
    {
        var keepId = Selected?.Id;
        _grid.DataSource = EmployeeRepo.List(_search.Text, _showFired.Checked);
        if (keepId != null)
            foreach (DataGridViewRow row in _grid.Rows)
                if (((Employee)row.DataBoundItem!).Id == keepId) { _grid.CurrentCell = row.Cells[0]; break; }
        UpdateButtons();
    }, "Не удалось загрузить сотрудников");

    private void UpdateButtons()
    {
        var e = Selected;
        _fireButton.Enabled = e != null;
        _fireButton.Text = e is { IsActive: false } ? "Вернуть в работу" : "Уволен";
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not Employee emp || emp.IsActive) return;
        e.CellStyle!.ForeColor = Color.Gray;
        e.CellStyle.Font = FiredFont;
    }

    private void Add()
    {
        using var f = new EmployeeForm();
        if (f.ShowDialog(FindForm()) == DialogResult.OK) RefreshData();
    }

    private void OpenSelected()
    {
        if (Selected is not { } emp) return;
        EmployeeCard.Open(FindForm(), emp.Id);
        RefreshData();
    }

    private void EditSelected()
    {
        if (Selected is not { } emp) return;
        using var f = new EmployeeForm(EmployeeRepo.Get(emp.Id));
        if (f.ShowDialog(FindForm()) == DialogResult.OK) RefreshData();
    }

    private void ToggleFired()
    {
        if (Selected is not { } emp) return;
        if (!emp.IsActive)
        {
            Ui.Safe(FindForm(), () => EmployeeRepo.SetActive(emp.Id, true));
            RefreshData();
            return;
        }

        var msg = $"Отметить «{emp.FullName}» как уволенного?\nИстория сохранится, в списке он будет скрыт.";
        if (emp.ItemCount > 0)
            msg += $"\n\nВнимание: за сотрудником числится вещей: {emp.ItemCount}. Их лучше сначала вернуть на склад.";
        if (!Ui.Confirm(FindForm(), msg)) return;
        Ui.Safe(FindForm(), () => EmployeeRepo.SetActive(emp.Id, false));
        RefreshData();
    }

    private void ImportCsv()
    {
        using var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv|Все файлы|*.*", Title = "Импорт сотрудников" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        Ui.Safe(FindForm(), () =>
        {
            var (added, updated, skipped) = EmployeeCsv.Import(dlg.FileName);
            RefreshData();
            Ui.Info(FindForm(), $"Импорт завершён.\nДобавлено: {added}\nОбновлено: {updated}\nПропущено пустых строк: {skipped}");
        }, "Ошибка импорта CSV");
    }

    private void ExportCsv()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv", FileName = $"Сотрудники_{DateTime.Now:yyyy-MM-dd}.csv", Title = "Экспорт сотрудников",
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        Ui.Safe(FindForm(), () =>
        {
            EmployeeCsv.Export(dlg.FileName, (List<Employee>)_grid.DataSource!);
            Ui.Info(FindForm(), "Файл сохранён.");
        }, "Ошибка экспорта CSV");
    }
}
