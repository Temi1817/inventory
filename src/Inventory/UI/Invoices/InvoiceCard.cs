using System.Diagnostics;
using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.UI.Warehouse;

namespace Inventory.UI.Invoices;

/// <summary>Просмотр принятой накладной: шапка, вещи по ней, файл.</summary>
public sealed class InvoiceCard : Form
{
    private readonly long _id;
    private readonly Label _title = new() { Font = Ui.TitleFont, AutoSize = true };
    private readonly Label _info = new() { AutoSize = true, ForeColor = Color.FromArgb(51, 65, 85), Padding = new Padding(0, 4, 0, 8) };
    private readonly DataGridView _grid = new();
    private readonly Button _openFile;
    private InvoiceRow? _invoice;

    private InvoiceCard(long id)
    {
        _id = id;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 480);
        ClientSize = new Size(960, 620);
        BackColor = Color.White;

        Ui.StyleGrid(_grid);
        _grid.Columns.AddRange(
            Ui.Col(nameof(Item.InventoryNumber), "Инв. номер", 70),
            Ui.Col(nameof(Item.Name), "Наименование", 220),
            Ui.Col(nameof(Item.SerialNumber), "Серийник", 100),
            Ui.Col(nameof(Item.Price), "Цена", 70, "#,0.##"),
            Ui.Col(nameof(Item.Status), "Статус", 65),
            Ui.Col(nameof(Item.EmployeeName), "У кого", 120));
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not Item item) return;
            WarehouseView.OpenItem(this, item.Id);
            RefreshData();
        };

        _openFile = Ui.Button("Открыть файл накладной", (_, _) => OpenFile(), primary: true);
        var actions = Ui.Toolbar();
        actions.Controls.Add(_openFile);
        actions.Controls.Add(Ui.Button("Изменить номер / дату / компанию", (_, _) => EditHeader()));

        var stack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        stack.Controls.AddRange([_title, _info]);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) };
        body.Controls.Add(_grid);
        body.Controls.Add(actions);
        body.Controls.Add(stack);
        Controls.Add(body);

        Shown += (_, _) => RefreshData();
    }

    private void RefreshData() => Ui.Safe(this, () =>
    {
        _invoice = InvoiceRepo.Get(_id);
        if (_invoice == null) { Close(); return; }
        var inv = _invoice;
        Text = $"Накладная № {inv.Number ?? "—"}";
        _title.Text = $"Накладная № {inv.Number ?? "—"}" + (inv.Date != null ? $" от {inv.Date:dd.MM.yyyy}" : "");
        _info.Text = string.Join("   ·   ", new[]
        {
            inv.SupplierName, inv.SupplierBin == null ? null : "БИН " + inv.SupplierBin, inv.CompanyName,
            inv.Total == null ? null : "Сумма " + inv.Total.Value.ToString("#,0.##"),
            inv.OrderNumber == null ? null : "по счёту № " + inv.OrderNumber,
            inv.Notes,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _openFile.Enabled = inv.HasFile;
        _grid.DataSource = ItemRepo.List(new ItemFilter { InvoiceId = _id }).OrderBy(i => i.Id).ToList();
    }, "Не удалось загрузить накладную");

    private void OpenFile()
    {
        var path = _invoice?.FilePath is { } rel ? AppPaths.ToAbsolute(rel) : null;
        if (path == null || !File.Exists(path)) { Ui.Warn(this, "Файл накладной не найден."); return; }
        Ui.Safe(this, () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
    }

    /// <summary>Исправить номер, дату (переносится в дату прихода/выдачи всех вещей) и заметки.</summary>
    private void EditHeader()
    {
        if (_invoice is not { } inv) return;
        using var f = new Form
        {
            Text = "Накладная: номер и дата", Font = Ui.BaseFont, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(500, 270),
        };
        var number = new TextBox { Text = inv.Number ?? "" };
        var date = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = inv.Date ?? DateTime.Today };
        var notes = new TextBox { Text = inv.Notes ?? "" };
        var company = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Text = inv.CompanyName ?? "" };
        company.Items.AddRange(CompanyRepo.All().Select(x => (object)x.Name).ToArray());
        company.Text = inv.CompanyName ?? "";
        var table = new FieldTable(100) { Padding = new Padding(12, 12, 12, 0) };
        table.Add("Номер", number);
        table.Add("Дата", date);
        table.Add("Компания", company);
        table.Add("Заметки", notes);
        table.Add("", new Label
        {
            Text = $"Дата и компания поменяются у всех {inv.ItemCount} вещей по этой накладной (дата прихода и выдачи).",
            AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Ui.Muted, Padding = new Padding(0, 6, 0, 0),
        });
        f.Controls.Add(table);
        f.Controls.Add(FieldTable.DialogButtons(f, "Сохранить", (_, _) =>
        {
            Ui.Safe(f, () =>
            {
                InvoiceRepo.UpdateHeader(inv.Id, number.Text, date.Value, notes.Text, company.Text);
                Infrastructure.Log.Info($"Накладная id {inv.Id}: номер «{number.Text}», дата {date.Value:dd.MM.yyyy} (было {inv.Date:dd.MM.yyyy}), компания «{company.Text}» (было «{inv.CompanyName}»)");
                f.DialogResult = DialogResult.OK;
                f.Close();
            }, "Не удалось сохранить");
        }));
        if (f.ShowDialog(this) == DialogResult.OK) RefreshData();
    }

    public static void Open(IWin32Window? owner, long id)
    {
        using var f = new InvoiceCard(id);
        f.ShowDialog(owner);
    }
}
