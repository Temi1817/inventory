using System.Diagnostics;
using System.Globalization;
using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;

namespace Inventory.UI.Warehouse;

/// <summary>Карточка вещи: поля, фото, ссылка на накладную, история движений.</summary>
public sealed partial class ItemForm : Form
{
    private readonly Item _original;
    private readonly bool _isNew;

    private readonly TextBox _name = new();
    private readonly ComboBox _category;
    private readonly TextBox _serial = new();
    private readonly TextBox _invNumber = new();
    private readonly TextBox _price = new();
    private readonly DateTimePicker _warranty = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true };
    private readonly ComboBox _company;
    private readonly Label _status = new() { AutoSize = false, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Font = Ui.BoldFont };
    private readonly Label _holder = new() { AutoSize = false, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DateTimePicker _arrival = new() { Format = DateTimePickerFormat.Short };
    private readonly TextBox _notes = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly LinkLabel _invoiceLink = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 6, 0, 6) };
    private readonly DataGridView _history = new();

    public ItemForm(Item? item = null)
    {
        _isNew = item == null;
        _original = item?.Clone() ?? new Item();
        Text = _isNew ? "Новая вещь" : $"Карточка: {item!.Name}";
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 600);
        ClientSize = new Size(940, _isNew ? 560 : 770);
        KeyPreview = true;
        AllowDrop = true;

        _category = FieldTable.AutoCombo(ItemRepo.Categories());
        _company = FieldTable.AutoCombo(CompanyRepo.All().Select(c => (object)c.Name));

        BuildLayout();
        LoadValues();
        SetupPhoto();
    }

    /// <summary>Id сохранённой вещи (после «Сохранить»).</summary>
    public long SavedId => _original.Id;

    private void BuildLayout()
    {
        var fields = new FieldTable { Dock = DockStyle.Fill, AutoSize = false };
        fields.Add("Наименование *", _name);
        fields.Add("Категория", _category);
        fields.Add("Серийный номер", _serial);
        fields.Add("Инв. номер", _invNumber);
        fields.Add("Цена", _price);
        fields.Add("Гарантия до", _warranty);
        fields.Add("Компания", _company);
        if (_isNew) fields.Add("Дата прихода", _arrival);
        else
        {
            fields.Add("Статус", _status);
            fields.Add("У кого", _holder);
        }
        fields.Add("Заметки", _notes, height: 90);
        _invoiceLink.LinkClicked += (_, _) => OpenInvoiceFile();
        fields.Add("", _invoiceLink);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 12, 12, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        top.Controls.Add(fields, 0, 0);
        top.Controls.Add(BuildPhotoPanel(), 1, 0);

        if (!_isNew)
        {
            Ui.StyleGrid(_history);
            _history.Columns.AddRange(
                Ui.Col(nameof(HistoryEntry.Date), "Дата", 70, "dd.MM.yyyy"),
                Ui.Col(nameof(HistoryEntry.Action), "Действие", 70),
                Ui.Col(nameof(HistoryEntry.FromName), "От кого", 100),
                Ui.Col(nameof(HistoryEntry.ToName), "Кому", 100),
                Ui.Col(nameof(HistoryEntry.Comment), "Комментарий", 200));
            var historyPanel = new Panel { Dock = DockStyle.Bottom, Height = 250, Padding = new Padding(12, 0, 12, 0) };
            historyPanel.Controls.Add(_history);
            historyPanel.Controls.Add(new Label
            {
                Text = "История движений", Font = Ui.BoldFont, Dock = DockStyle.Top, Height = 26,
                TextAlign = ContentAlignment.BottomLeft,
            });
            historyPanel.Controls.Add(BuildActions());
            Controls.Add(top);
            Controls.Add(historyPanel);
        }
        else Controls.Add(top);

        Controls.Add(FieldTable.DialogButtons(this, "Сохранить", (_, _) => Save()));
    }

    private void LoadValues()
    {
        var i = _original;
        _name.Text = i.Name;
        _category.Text = i.Category ?? "";
        _serial.Text = i.SerialNumber ?? "";
        _invNumber.Text = i.InventoryNumber ?? "";
        _price.Text = i.Price?.ToString("#,0.##", CultureInfo.GetCultureInfo("ru-RU")) ?? "";
        _warranty.Checked = i.WarrantyUntil != null;
        if (i.WarrantyUntil != null) _warranty.Value = i.WarrantyUntil.Value;
        _company.Text = i.CompanyName ?? "";
        _notes.Text = i.Notes ?? "";
        _arrival.Value = DateTime.Today;
        _status.Text = i.Status;
        _holder.Text = i.EmployeeName ?? "—";

        if (i.InvoiceId != null)
        {
            _invoiceLink.Text = $"Накладная № {i.InvoiceNumber ?? "—"}" +
                                (i.InvoiceDate != null ? $" от {i.InvoiceDate:dd.MM.yyyy}" : "");
            _invoiceLink.Visible = true;
        }
        if (!_isNew) _history.DataSource = HistoryRepo.ForItem(i.Id);
    }

    private readonly Dictionary<string, Button> _actionButtons = new();

    private FlowLayoutPanel BuildActions()
    {
        var bar = Ui.Toolbar();
        bar.Padding = new Padding(0, 8, 0, 0);
        void Add(string text, Func<IWin32Window, IReadOnlyList<Item>, bool> action, params string[] enabledFor)
        {
            var b = Ui.Button(text, (_, _) => DoAction(action));
            b.Tag = enabledFor;
            _actionButtons[text] = b;
            bar.Controls.Add(b);
        }
        Add("Выдать", Moves.ItemActions.Issue, ItemStatus.InStock);
        Add("Вернуть на склад", Moves.ItemActions.Return, ItemStatus.Issued, ItemStatus.Repair);
        Add("Передать другому", Moves.ItemActions.Transfer, ItemStatus.Issued);
        Add("В ремонт", Moves.ItemActions.Repair, ItemStatus.InStock, ItemStatus.Issued);
        Add("Списать", Moves.ItemActions.WriteOff, ItemStatus.InStock, ItemStatus.Issued, ItemStatus.Repair);
        bar.Controls.Add(new Label { Width = 16 });
        bar.Controls.Add(Ui.Button("Печать QR", (_, _) => PrintQr()));
        UpdateActionButtons();
        return bar;
    }

    private void PrintQr()
    {
        var saved = ItemRepo.Get(_original.Id);
        if (saved == null) return;
        if ((saved.InventoryNumber ?? "") != _invNumber.Text.Trim())
        {
            Ui.Warn(this, "Инвентарный номер изменён, но не сохранён. Сначала нажмите «Сохранить».");
            return;
        }
        Docs.PrintQr(this, [saved]);
    }

    private void UpdateActionButtons()
    {
        foreach (var b in _actionButtons.Values)
            b.Enabled = ((string[])b.Tag!).Contains(_original.Status);
    }

    /// <summary>Выполнить движение и обновить статус, «у кого» и историю (несохранённые правки полей не трогаем).</summary>
    private void DoAction(Func<IWin32Window, IReadOnlyList<Item>, bool> action)
    {
        var fresh = ItemRepo.Get(_original.Id);
        if (fresh == null) { Ui.Warn(this, "Вещь не найдена — возможно, удалена."); return; }
        if (!action(this, [fresh])) return;

        var after = ItemRepo.Get(_original.Id)!;
        _original.Status = after.Status;
        _original.EmployeeId = after.EmployeeId;
        _original.EmployeeName = after.EmployeeName;
        _status.Text = after.Status;
        _holder.Text = after.EmployeeName ?? "—";
        _history.DataSource = HistoryRepo.ForItem(after.Id);
        UpdateActionButtons();
    }

    private void OpenInvoiceFile()
    {
        var rel = _original.InvoiceFilePath;
        if (string.IsNullOrWhiteSpace(rel) || !File.Exists(AppPaths.ToAbsolute(rel)))
        {
            Ui.Warn(this, "Файл накладной не найден.");
            return;
        }
        Ui.Safe(this, () => Process.Start(new ProcessStartInfo(AppPaths.ToAbsolute(rel)) { UseShellExecute = true }));
    }

    private static bool TryParsePrice(string text, out decimal? price)
    {
        price = null;
        var t = text.Replace(" ", "").Replace(" ", "").Replace(".", ",").Trim();
        if (t.Length == 0) return true;
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.GetCultureInfo("ru-RU"), out var p) || p < 0)
            return false;
        price = p;
        return true;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { Ui.Warn(this, "Укажите наименование."); _name.Focus(); return; }
        if (!TryParsePrice(_price.Text, out var price)) { Ui.Warn(this, "Цена указана неверно."); _price.Focus(); return; }
        if (ItemRepo.InventoryNumberTaken(_invNumber.Text, _original.Id))
        {
            Ui.Warn(this, $"Инвентарный номер «{_invNumber.Text.Trim()}» уже присвоен другой вещи.");
            _invNumber.Focus();
            return;
        }

        Ui.Safe(this, () =>
        {
            var item = _original.Clone();
            item.Name = _name.Text;
            item.Category = _category.Text;
            item.SerialNumber = _serial.Text;
            item.InventoryNumber = _invNumber.Text;
            item.Price = price;
            item.WarrantyUntil = _warranty.Checked ? _warranty.Value.Date : null;
            item.CompanyId = CompanyRepo.GetOrCreate(_company.Text);
            item.CompanyName = _company.Val();
            item.Notes = _notes.Text;
            item.PhotoPath = CommitPhoto(item.PhotoPath);

            if (_isNew)
            {
                item.Status = ItemStatus.InStock;
                _original.Id = ItemRepo.Insert(item, _arrival.Value, "Заведено вручную");
            }
            else ItemRepo.Update(item, DescribeChanges(_original, item));

            DialogResult = DialogResult.OK;
            Close();
        }, "Не удалось сохранить карточку");
    }

    /// <summary>Описание изменений для истории: «Серийник: A → B; …».</summary>
    internal static string? DescribeChanges(Item a, Item b)
    {
        var changes = new List<string>();
        void Cmp(string label, string? x, string? y)
        {
            x = string.IsNullOrWhiteSpace(x) ? null : x.Trim();
            y = string.IsNullOrWhiteSpace(y) ? null : y.Trim();
            if (x != y) changes.Add($"{label}: {x ?? "—"} → {y ?? "—"}");
        }
        Cmp("Наименование", a.Name, b.Name);
        Cmp("Категория", a.Category, b.Category);
        Cmp("Серийник", a.SerialNumber, b.SerialNumber);
        Cmp("Инв. номер", a.InventoryNumber, b.InventoryNumber);
        Cmp("Цена", a.Price?.ToString("0.##"), b.Price?.ToString("0.##"));
        Cmp("Гарантия", a.WarrantyUntil?.ToString("dd.MM.yyyy"), b.WarrantyUntil?.ToString("dd.MM.yyyy"));
        Cmp("Компания", a.CompanyName, b.CompanyName);
        if ((a.Notes ?? "").Trim() != (b.Notes ?? "").Trim()) changes.Add("Заметки изменены");
        if (a.PhotoPath != b.PhotoPath) changes.Add("Фото изменено");
        return changes.Count == 0 ? null : string.Join("; ", changes);
    }
}
