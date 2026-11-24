using Inventory.Data;
using Inventory.Models;
using Inventory.Services.Documents;

namespace Inventory.UI.Consumables;

/// <summary>Раздел «Расходники»: остатки, приход/выдача, журнал движений.</summary>
public sealed class ConsumablesView : UserControl, IView
{
    private readonly DataGridView _grid = new();
    private readonly DataGridView _moves = new();
    private readonly Label _movesTitle = new() { Font = Ui.BoldFont, Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.BottomLeft };
    private readonly TextBox _filter = new() { Width = 240, PlaceholderText = "Поиск по названию, категории…" };
    private readonly Label _lowCount = new() { AutoSize = true, ForeColor = Ui.Danger, Padding = new Padding(8, 7, 0, 0) };
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 250 };

    public ConsumablesView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);

        Ui.StyleGrid(_grid);
        _grid.Columns.AddRange(
            Ui.Col(nameof(Consumable.Name), "Наименование", 220),
            Ui.Col(nameof(Consumable.Category), "Категория", 100),
            Ui.Col(nameof(Consumable.Quantity), "Остаток", 60, "#,0.##"),
            Ui.Col(nameof(Consumable.MinQuantity), "Минимум", 60, "#,0.##"),
            Ui.Col(nameof(Consumable.Unit), "Ед.", 40),
            Ui.Col(nameof(Consumable.Notes), "Заметки", 140));
        foreach (var n in new[] { nameof(Consumable.Quantity), nameof(Consumable.MinQuantity) })
            _grid.Columns[n]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Edit(); };
        _grid.SelectionChanged += (_, _) => LoadMoves();

        Ui.StyleGrid(_moves);
        _moves.Columns.AddRange(
            Ui.Col(nameof(ConsumableMove.Date), "Дата", 60, "dd.MM.yyyy"),
            Ui.Col(nameof(ConsumableMove.Kind), "Движение", 50),
            Ui.Col(nameof(ConsumableMove.Amount), "Кол-во", 45, "#,0.##"),
            Ui.Col(nameof(ConsumableMove.EmployeeName), "Кому", 120),
            Ui.Col(nameof(ConsumableMove.Comment), "Комментарий", 200));

        var toolbar = Ui.Toolbar();
        toolbar.Controls.AddRange([
            Ui.Button("+ Приход", (_, _) => MoveStock(income: true), primary: true),
            Ui.Button("− Выдал", (_, _) => MoveStock(income: false), primary: true),
            new Label { Width = 16 },
            Ui.Button("Добавить", (_, _) => Add()),
            Ui.Button("Изменить", (_, _) => Edit()),
            Ui.Button("Удалить", (_, _) => Delete()),
            Ui.Button("Экспорт в Excel", (_, _) => Export()),
            new Label { Width = 16 },
            _filter, _lowCount,
        ]);
        _delay.Tick += (_, _) => { _delay.Stop(); RefreshData(); };
        _filter.TextChanged += (_, _) => { _delay.Stop(); _delay.Start(); };

        var movesPanel = new Panel { Dock = DockStyle.Bottom, Height = 220 };
        movesPanel.Controls.Add(_moves);
        movesPanel.Controls.Add(_movesTitle);

        Controls.Add(_grid);
        Controls.Add(movesPanel);
        Controls.Add(toolbar);
        Controls.Add(Ui.Title("Расходники"));
    }

    private Consumable? Selected => _grid.CurrentRow?.DataBoundItem as Consumable;

    public void RefreshData() => Ui.Safe(FindForm(), () =>
    {
        var keepId = Selected?.Id;
        var list = ConsumableRepo.List(_filter.Text);
        _grid.DataSource = list;
        if (keepId != null)
            foreach (DataGridViewRow r in _grid.Rows)
                if (((Consumable)r.DataBoundItem!).Id == keepId) { _grid.CurrentCell = r.Cells[0]; break; }
        var low = list.Count(IsLow);
        _lowCount.Text = low > 0 ? $"Заканчивается: {low}" : "";
        LoadMoves();
    }, "Не удалось загрузить расходники");

    private static bool IsLow(Consumable c) => c.MinQuantity > 0 && c.Quantity < c.MinQuantity;

    private void LoadMoves()
    {
        if (Selected is not { } c) { _moves.DataSource = null; _movesTitle.Text = "Движения"; return; }
        _movesTitle.Text = $"Движения: {c.Name}";
        _moves.DataSource = ConsumableRepo.Moves(c.Id);
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not Consumable c || !IsLow(c)) return;
        e.CellStyle!.BackColor = Color.FromArgb(254, 226, 226);
        e.CellStyle.SelectionBackColor = Color.FromArgb(252, 165, 165);
        if (_grid.Columns[e.ColumnIndex].Name == nameof(Consumable.Quantity))
        {
            e.CellStyle.ForeColor = Ui.Danger;
            e.CellStyle.Font = Ui.BoldFont;
        }
    }

    private void MoveStock(bool income)
    {
        if (Selected is not { } c) { Ui.Warn(FindForm(), "Выберите расходник в таблице."); return; }
        var fresh = ConsumableRepo.Get(c.Id);
        if (fresh == null) { RefreshData(); return; }
        if (!income && fresh.Quantity <= 0) { Ui.Warn(FindForm(), $"«{fresh.Name}» закончился — сначала оприходуйте."); return; }
        using var d = new ConsumableMoveDialog(fresh, income);
        if (d.ShowDialog(FindForm()) == DialogResult.OK) RefreshData();
    }

    private void Add()
    {
        using var f = new ConsumableForm();
        if (f.ShowDialog(FindForm()) == DialogResult.OK) RefreshData();
    }

    private void Edit()
    {
        if (Selected is not { } c) return;
        using var f = new ConsumableForm(ConsumableRepo.Get(c.Id));
        if (f.ShowDialog(FindForm()) == DialogResult.OK) RefreshData();
    }

    private void Delete()
    {
        if (Selected is not { } c) return;
        var moves = ConsumableRepo.Moves(c.Id).Count;
        var msg = $"Удалить расходник «{c.Name}»?" + (moves > 0 ? $"\nВместе с ним удалится журнал движений ({moves} записей)." : "");
        if (!Ui.Confirm(FindForm(), msg)) return;
        Ui.Safe(FindForm(), () => ConsumableRepo.Delete(c.Id), "Не удалось удалить");
        RefreshData();
    }

    private void Export()
    {
        if (_grid.DataSource is not List<Consumable> list || list.Count == 0) { Ui.Info(FindForm(), "Список пуст."); return; }
        Docs.SaveAndOpen(FindForm(), $"Расходники_{DateTime.Now:yyyy-MM-dd}.xlsx", Docs.ExcelFilter,
            path => ExcelExport.Consumables(path, list));
    }
}
