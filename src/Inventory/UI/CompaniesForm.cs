using Inventory.Data;

namespace Inventory.UI;

/// <summary>Сервис → Компании: справочник компаний (добавить, переименовать, удалить неиспользуемую).</summary>
public sealed class CompaniesForm : Form
{
    private readonly DataGridView _grid = new();

    /// <summary>Были ли изменения (чтобы обновить списки в главном окне).</summary>
    public bool Changed { get; private set; }

    public CompaniesForm()
    {
        Text = "Компании";
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 380);
        ClientSize = new Size(640, 440);
        MaximizeBox = MinimizeBox = false;

        Ui.StyleGrid(_grid);
        _grid.Columns.AddRange(
            Ui.Col(nameof(CompanyUsage.Name), "Компания", 200),
            Ui.Col(nameof(CompanyUsage.Employees), "Сотрудников", 60),
            Ui.Col(nameof(CompanyUsage.Items), "Вещей", 50),
            Ui.Col(nameof(CompanyUsage.Invoices), "Накладных", 55));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) RenameSelected(); };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; }
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        };

        var toolbar = Ui.Toolbar();
        toolbar.Controls.AddRange([
            Ui.Button("Добавить", (_, _) => AddCompany(), primary: true),
            Ui.Button("Переименовать", (_, _) => RenameSelected()),
            Ui.Button("Удалить", (_, _) => DeleteSelected()),
        ]);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        body.Controls.Add(_grid);
        body.Controls.Add(toolbar);
        Controls.Add(body);

        var close = Ui.Button("Закрыть", (_, _) => Close());
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(12, 8, 12, 8) };
        bottom.Controls.Add(close);
        bottom.Controls.Add(new Label
        {
            Text = "Двойной клик или F2 — переименовать", AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(0, 7, 16, 0),
        });
        Controls.Add(bottom);
        CancelButton = close;

        Load += (_, _) => RefreshData();
    }

    private CompanyUsage? Selected => _grid.CurrentRow?.DataBoundItem as CompanyUsage;

    private void RefreshData(long? selectId = null) => Ui.Safe(this, () =>
    {
        var keep = selectId ?? Selected?.Id;
        _grid.DataSource = CompanyRepo.WithUsage();
        if (keep != null)
            foreach (DataGridViewRow r in _grid.Rows)
                if (((CompanyUsage)r.DataBoundItem!).Id == keep) { _grid.CurrentCell = r.Cells[0]; break; }
    }, "Не удалось загрузить компании");

    private void AddCompany()
    {
        var name = Prompt("Новая компания", "Название компании:", "");
        if (name == null) return;
        Ui.Safe(this, () =>
        {
            var id = CompanyRepo.Add(name);
            Changed = true;
            RefreshData(id);
        });
    }

    private void RenameSelected()
    {
        if (Selected is not { } c) return;
        var name = Prompt("Переименовать компанию", "Новое название:", c.Name);
        if (name == null || name.Trim() == c.Name) return;
        if (c.IsUsed && !Ui.Confirm(this, $"Название изменится везде: у сотрудников ({c.Employees}), вещей ({c.Items}) и накладных ({c.Invoices}). Продолжить?"))
            return;
        Ui.Safe(this, () =>
        {
            CompanyRepo.Rename(c.Id, name);
            Changed = true;
            RefreshData(c.Id);
        });
    }

    private void DeleteSelected()
    {
        if (Selected is not { } c) return;
        if (c.IsUsed)
        {
            Ui.Warn(this, $"Компания «{c.Name}» используется: сотрудников {c.Employees}, вещей {c.Items}, накладных {c.Invoices}.\n" +
                          "Удалить можно только неиспользуемую. Если нужно другое название — переименуйте.");
            return;
        }
        if (!Ui.Confirm(this, $"Удалить компанию «{c.Name}»?")) return;
        Ui.Safe(this, () =>
        {
            CompanyRepo.Delete(c.Id);
            Changed = true;
            RefreshData();
        });
    }

    /// <summary>Простое окно ввода строки. null — отмена.</summary>
    private string? Prompt(string title, string label, string value)
    {
        using var f = new Form
        {
            Text = title, Font = Ui.BaseFont, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(420, 130),
        };
        var box = new TextBox { Text = value };
        var table = new FieldTable(90) { Padding = new Padding(12, 16, 12, 0) };
        table.Add(label.TrimEnd(':'), box);
        f.Controls.Add(table);
        f.Controls.Add(FieldTable.DialogButtons(f, "OK", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) { Ui.Warn(f, "Введите название."); return; }
            f.DialogResult = DialogResult.OK;
            f.Close();
        }));
        f.AcceptButton = f.Controls.OfType<FlowLayoutPanel>().First().Controls.OfType<Button>().First(b => b.Text == "OK");
        f.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        return f.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
