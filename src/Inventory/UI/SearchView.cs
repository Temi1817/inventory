using Inventory.Data;
using Inventory.Models;
using Inventory.UI.Employees;
using Inventory.UI.Warehouse;

namespace Inventory.UI;

/// <summary>Результаты глобального поиска.</summary>
public sealed class SearchView : UserControl, IView
{
    private readonly DataGridView _grid = new();
    private readonly Label _title = Ui.Title("Поиск");
    private string _query = "";

    public SearchView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);
        Ui.StyleGrid(_grid);
        _grid.Columns.AddRange(
            Ui.Col(nameof(SearchHit.Kind), "Тип", 50),
            Ui.Col(nameof(SearchHit.Title), "Найдено", 200),
            Ui.Col(nameof(SearchHit.Details), "Подробно", 200));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };

        Controls.Add(_grid);
        Controls.Add(new Label
        {
            Text = "Двойной клик — открыть", Dock = DockStyle.Top, Height = 24, ForeColor = Ui.Muted,
        });
        Controls.Add(_title);
    }

    public void Search(string query)
    {
        _query = query.Trim();
        RefreshData();
    }

    public void RefreshData() => Ui.Safe(FindForm(), () =>
    {
        var hits = SearchRepo.Find(_query);
        _grid.DataSource = hits;
        _title.Text = hits.Count == 0 ? $"Поиск «{_query}»: ничего не найдено" : $"Поиск «{_query}»: {hits.Count}";
    }, "Ошибка поиска");

    private void OpenSelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is not SearchHit hit) return;
        var owner = FindForm();
        switch (hit.Kind)
        {
            case "Вещь":
                WarehouseView.OpenItem(owner, hit.Id);
                break;
            case "Сотрудник":
                EmployeeCard.Open(owner, hit.Id);
                break;
            case "Накладная":
                Invoices.InvoiceCard.Open(owner, hit.Id);
                break;
        }
        RefreshData();
    }
}
