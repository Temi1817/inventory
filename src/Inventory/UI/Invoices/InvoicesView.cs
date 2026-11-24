using Inventory.Data;
using Inventory.Models;

namespace Inventory.UI.Invoices;

/// <summary>Раздел «Накладные»: все принятые накладные.</summary>
public sealed class InvoicesView : UserControl, IView
{
    private readonly DataGridView _grid = new();
    private readonly TextBox _filter = new() { Width = 300, PlaceholderText = "Номер, поставщик или БИН…" };
    private readonly Label _count = new() { AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(8, 7, 0, 0) };
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 250 };

    public InvoicesView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);

        Ui.StyleGrid(_grid);
        _grid.Columns.AddRange(
            Ui.Col(nameof(InvoiceRow.Number), "Номер", 70),
            Ui.Col(nameof(InvoiceRow.Date), "Дата", 60, "dd.MM.yyyy"),
            Ui.Col(nameof(InvoiceRow.SupplierName), "Поставщик", 170),
            Ui.Col(nameof(InvoiceRow.SupplierBin), "БИН", 80),
            Ui.Col(nameof(InvoiceRow.CompanyName), "Компания", 80),
            Ui.Col(nameof(InvoiceRow.Total), "Сумма", 70, "#,0.##"),
            Ui.Col(nameof(InvoiceRow.ItemCount), "Вещей", 40),
            Ui.Col(nameof(InvoiceRow.FileMark), "Файл", 35));
        _grid.Columns[nameof(InvoiceRow.Total)]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };

        var toolbar = Ui.Toolbar();
        toolbar.Controls.AddRange([_filter, Ui.Button("Открыть", (_, _) => OpenSelected()), _count]);
        _delay.Tick += (_, _) => { _delay.Stop(); RefreshData(); };
        _filter.TextChanged += (_, _) => { _delay.Stop(); _delay.Start(); };

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(Ui.Title("Накладные"));
    }

    public void RefreshData() => Ui.Safe(FindForm(), () =>
    {
        var rows = InvoiceRepo.List(_filter.Text);
        _grid.DataSource = rows;
        _count.Text = $"Всего: {rows.Count}";
    }, "Не удалось загрузить накладные");

    private void OpenSelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is not InvoiceRow row) return;
        InvoiceCard.Open(FindForm(), row.Id);
        RefreshData();
    }
}
