using Inventory.Infrastructure;

namespace Inventory.UI;

/// <summary>Общие стили, диалоги и обработка ошибок.</summary>
public static class Ui
{
    public static readonly Font BaseFont = new("Segoe UI", 9.75f);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 14f);
    public static readonly Font BoldFont = new("Segoe UI Semibold", 9.75f);

    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color NavBack = Color.FromArgb(30, 41, 59);
    public static readonly Color NavHover = Color.FromArgb(51, 65, 85);
    public static readonly Color NavText = Color.FromArgb(226, 232, 240);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color GridAlt = Color.FromArgb(248, 250, 252);
    public static readonly Color GridSelect = Color.FromArgb(219, 234, 254);

    public static void Error(IWin32Window? owner, string message, Exception? ex = null)
    {
        Log.Error(message, ex);
        var text = ex == null ? message : $"{message}\n\n{ex.Message}";
        MessageBox.Show(owner, text, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public static void Info(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Инвентаризация", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static bool Confirm(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>Выполнить действие; при ошибке — лог и сообщение, без падения.</summary>
    public static void Safe(IWin32Window? owner, Action action, string what = "Не удалось выполнить действие")
    {
        try { action(); }
        catch (ArgumentException ex) { Warn(owner, ex.Message); }
        catch (Exception ex) { Error(owner, what, ex); }
    }

    public static void StyleGrid(DataGridView g)
    {
        g.Dock = DockStyle.Fill;
        g.ReadOnly = true;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.AutoGenerateColumns = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.RowHeadersVisible = false;
        g.BackgroundColor = Color.White;
        g.BorderStyle = BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.GridColor = Color.FromArgb(226, 232, 240);
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 34;
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(51, 65, 85);
        g.ColumnHeadersDefaultCellStyle.Font = BoldFont;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(241, 245, 249);
        g.RowTemplate.Height = 30;
        g.DefaultCellStyle.SelectionBackColor = GridSelect;
        g.DefaultCellStyle.SelectionForeColor = Color.Black;
        g.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        g.AlternatingRowsDefaultCellStyle.BackColor = GridAlt;
        typeof(DataGridView).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(g, true);
    }

    public static DataGridViewTextBoxColumn Col(string property, string header, float weight = 100, string? format = null)
    {
        var c = new DataGridViewTextBoxColumn
        {
            DataPropertyName = property, HeaderText = header, FillWeight = weight, Name = property,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        if (format != null) c.DefaultCellStyle.Format = format;
        return c;
    }

    public static Button Button(string text, EventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, Height = 30, Padding = new Padding(8, 0, 8, 0),
            Margin = new Padding(0, 0, 6, 0), FlatStyle = FlatStyle.System, UseVisualStyleBackColor = true,
        };
        if (primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderSize = 0;
        }
        b.Click += onClick;
        return b;
    }

    /// <summary>Горизонтальная панель инструментов сверху.</summary>
    public static FlowLayoutPanel Toolbar() => new()
    {
        Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
        Padding = new Padding(0, 0, 0, 8), BackColor = Color.Transparent,
    };

    public static Label Title(string text) => new()
    {
        Text = text, Font = TitleFont, Dock = DockStyle.Top, AutoSize = false, Height = 40,
        ForeColor = Color.FromArgb(15, 23, 42),
    };

    /// <summary>Текст контрола без пробелов по краям; пустой → null.</summary>
    public static string? Val(this Control c) => string.IsNullOrWhiteSpace(c.Text) ? null : c.Text.Trim();
}
