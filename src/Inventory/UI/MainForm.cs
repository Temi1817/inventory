using System.Diagnostics;
using Inventory.Infrastructure;
using Inventory.UI.Arrival;
using Inventory.UI.Employees;
using Inventory.UI.Invoices;
using Inventory.UI.Warehouse;

namespace Inventory.UI;

/// <summary>Главное окно: навигация слева, поиск сверху, раздел в центре.</summary>
public sealed class MainForm : Form
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly FlowLayoutPanel _nav = new()
    {
        Dock = DockStyle.Left, Width = 190, BackColor = Ui.NavBack, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, Padding = new Padding(0, 8, 0, 0),
    };
    private readonly TextBox _search = new()
    {
        PlaceholderText = "Поиск: наименование, серийник, инв. номер, ФИО, № накладной — Enter",
        Width = 520, Font = new Font("Segoe UI", 11f),
    };
    private readonly Dictionary<string, Control> _views = new();
    private readonly SearchView _searchView = new();
    private Button? _activeNav;

    public MainForm()
    {
        Text = "Инвентаризация";
        Font = Ui.BaseFont;
        MinimumSize = new Size(1000, 640);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;

        _views["Склад"] = new WarehouseView();
        _views["Сотрудники"] = new EmployeesView();
        _views["Приход"] = new ArrivalView();
        _views["Заказы"] = new Orders.OrdersView();
        _views["Накладные"] = new InvoicesView();
        _views["Расходники"] = new Consumables.ConsumablesView();

        var brand = new Label
        {
            Text = "Инвентаризация", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12.5f),
            Width = _nav.Width, Height = 48, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0),
        };
        _nav.Controls.Add(brand);
        foreach (var name in _views.Keys) _nav.Controls.Add(NavButton(name));
        // Справочник — открывается отдельным окном (раздел не переключается).
        _nav.Controls.Add(new Label { Width = _nav.Width, Height = 1, BackColor = Ui.NavHover, Margin = new Padding(0, 8, 0, 8) });
        var companies = NavButton("Компании");
        companies.Click += (_, _) => OpenCompanies();
        _nav.Controls.Add(companies);

        var top = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 12, 16, 8), BackColor = Color.FromArgb(248, 250, 252) };
        _search.Location = new Point(16, 12);
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunSearch(); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _search.Clear(); }
        };
        top.Controls.Add(_search);

        Controls.Add(_content);
        Controls.Add(top);
        Controls.Add(_nav);
        Controls.Add(BuildMenu());

        Shown += (_, _) =>
        {
            ShowView("Склад");
            _content.Focus();
            ActiveControl = _content;
            // Продолжить распознавание очереди, начатое в прошлый раз.
            Ui.Safe(this, () =>
            {
                if (Data.ImportQueueRepo.ResetStuck() > 0 || Data.ImportQueueRepo.NextPending() != null)
                    Services.Recognition.QueueProcessor.Instance.Start();
            }, "Не удалось продолжить очередь распознавания");
        };
        // Первый запуск после установки: Ollama есть, а модели нет — предложить скачать.
        Shown += async (_, _) => await ModelDownloadForm.OfferIfMissingAsync(this);
        FormClosing += (_, _) => Services.Recognition.QueueProcessor.Instance.Stop();
        // Автобэкап раз в сутки — в фоне, чтобы не тормозить запуск.
        Shown += (_, _) => Task.Run(() =>
        {
            try { if (Services.BackupService.AutoBackupIfDue() is { } p) Log.Info("Автобэкап: " + p); }
            catch (Exception ex) { Log.Error("Автобэкап не удался", ex); }
        });
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { BackColor = Color.White };
        var service = new ToolStripMenuItem("Сервис");
        service.DropDownItems.Add("Настройки…", null, (_, _) =>
        {
            using var f = new SettingsForm();
            if (f.ShowDialog(this) == DialogResult.OK && _content.Controls.Count > 0) (_content.Controls[0] as IView)?.RefreshData();
        });
        service.DropDownItems.Add("Компании…", null, (_, _) => OpenCompanies());
        service.DropDownItems.Add(new ToolStripSeparator());
        service.DropDownItems.Add("Бэкап…", null, (_, _) => MakeBackup());
        service.DropDownItems.Add("Восстановить из бэкапа…", null, (_, _) => RestoreBackup());
        service.DropDownItems.Add("Открыть папку автобэкапов", null, (_, _) =>
            Ui.Safe(this, () => { Directory.CreateDirectory(Services.BackupService.BackupsPath); Process.Start(new ProcessStartInfo(Services.BackupService.BackupsPath) { UseShellExecute = true }); }));
        service.DropDownItems.Add(new ToolStripSeparator());
        service.DropDownItems.Add("Открыть папку данных", null, (_, _) =>
            Ui.Safe(this, () => Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true })));
        service.DropDownItems.Add("Открыть папку логов", null, (_, _) =>
            Ui.Safe(this, () => Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true })));
        menu.Items.Add(service);
        MainMenuStrip = menu;
        return menu;
    }

    private void OpenCompanies()
    {
        using var f = new CompaniesForm();
        f.ShowDialog(this);
        if (f.Changed && _content.Controls.Count > 0) (_content.Controls[0] as IView)?.RefreshData();
    }

    private void MakeBackup()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Сохранить бэкап", Filter = "Архив zip (*.zip)|*.zip", FileName = Services.BackupService.DefaultFileName(),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Ui.Safe(this, () =>
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                var size = Services.BackupService.Create(dlg.FileName);
                Ui.Info(this, $"Бэкап сохранён ({size / 1024.0 / 1024.0:0.#} МБ):\n{dlg.FileName}\n\nСовет: храните копию на флешке или в облаке, не только на этом ПК.");
            }
            finally { Cursor = Cursors.Default; }
        }, "Не удалось создать бэкап");
    }

    private void RestoreBackup()
    {
        using var dlg = new OpenFileDialog { Title = "Восстановить из бэкапа", Filter = "Архив zip (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var (_, items, employees) = Services.BackupService.Inspect(dlg.FileName);
            if (!Ui.Confirm(this, $"Восстановить данные из «{Path.GetFileName(dlg.FileName)}»?\n\n" +
                                  $"В бэкапе: вещей {items}, сотрудников {employees}.\n\n" +
                                  "ТЕКУЩИЕ данные будут заменены. Перед этим они автоматически сохранятся в папку автобэкапов.\n" +
                                  "После восстановления программа перезапустится."))
                return;

            Cursor = Cursors.WaitCursor;
            Services.Recognition.QueueProcessor.Instance.Stop();
            var safety = Services.BackupService.Restore(dlg.FileName);
            Cursor = Cursors.Default;
            Ui.Info(this, $"Данные восстановлены. Программа перезапустится.\n\nПрежние данные сохранены здесь:\n{safety}");
            Application.Restart();
            Environment.Exit(0);
        }
        catch (ArgumentException ex) { Cursor = Cursors.Default; Ui.Warn(this, ex.Message); }
        catch (Exception ex) { Cursor = Cursors.Default; Ui.Error(this, "Не удалось восстановить данные", ex); }
    }

    private Button NavButton(string name)
    {
        var b = new Button
        {
            Text = name, Tag = name, Width = _nav.Width, Height = 42, FlatStyle = FlatStyle.Flat,
            ForeColor = Ui.NavText, BackColor = Ui.NavBack, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0), Margin = Padding.Empty, Font = new Font("Segoe UI", 10.5f),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Ui.NavHover;
        b.Click += (_, _) => ShowView(name);
        return b;
    }

    public void ShowView(string viewName)
    {
        if (!_views.TryGetValue(viewName, out var view)) return;
        ShowControl(view);
        if (_activeNav != null) _activeNav.BackColor = Ui.NavBack;
        _activeNav = _nav.Controls.OfType<Button>().FirstOrDefault(b => (string)b.Tag! == viewName);
        if (_activeNav != null) _activeNav.BackColor = Ui.Accent;
    }

    private void ShowControl(Control view)
    {
        if (!_content.Controls.Contains(view))
        {
            _content.Controls.Clear();
            _content.Controls.Add(view);
        }
        (view as IView)?.RefreshData();
    }

    private void RunSearch()
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) return;
        if (_activeNav != null) _activeNav.BackColor = Ui.NavBack;
        _activeNav = null;
        _searchView.Search(q);
        ShowControl(_searchView);
    }
}
