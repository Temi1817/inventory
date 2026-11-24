using System.Diagnostics;
using Inventory.Infrastructure;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Inventory.UI.Arrival;

/// <summary>Превью накладной: картинка или PDF (постранично), «вписать» / «100%».</summary>
public sealed class FilePreview : UserControl
{
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(226, 232, 240) };
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
    private readonly Label _info = new() { AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(6, 7, 0, 0) };
    private readonly Button _prev, _next, _zoom;
    private PdfDocument? _pdf;
    private uint _page;
    private bool _actualSize;
    private string? _path;

    public FilePreview()
    {
        Dock = DockStyle.Fill;
        _prev = Ui.Button("◀", async (_, _) => await ShowPage(_page - 1));
        _next = Ui.Button("▶", async (_, _) => await ShowPage(_page + 1));
        _zoom = Ui.Button("100%", (_, _) => ToggleZoom());
        var bar = Ui.Toolbar();
        bar.Padding = new Padding(0, 0, 0, 4);
        bar.Controls.AddRange([_zoom, _prev, _next, Ui.Button("Открыть", (_, _) => OpenExternal()), _info]);

        _scroll.Controls.Add(_picture);
        Controls.Add(_scroll);
        Controls.Add(bar);
        ShowEmpty("Файл не выбран");
    }

    public async Task LoadAsync(string? path)
    {
        _path = path;
        _pdf = null;
        _page = 0;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { ShowEmpty("Файла нет — ручной ввод"); return; }
        try
        {
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
                _pdf = await PdfDocument.LoadFromFileAsync(file);
                await ShowPage(0);
            }
            else
            {
                using var stream = File.OpenRead(path);
                using var img = Image.FromStream(stream);
                Services.Recognition.PageImages.ApplyExifOrientation(img);
                SetImage(new Bitmap(img));
                _info.Text = Path.GetFileName(path);
                _prev.Visible = _next.Visible = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось показать превью " + path, ex);
            ShowEmpty("Превью недоступно — нажмите «Открыть»");
        }
    }

    private async Task ShowPage(uint index)
    {
        if (_pdf == null || index >= _pdf.PageCount) return;
        _page = index;
        using var page = _pdf.GetPage(index);
        using var ras = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = 1600 });
        using var stream = ras.AsStreamForRead();
        using var img = Image.FromStream(stream);
        SetImage(new Bitmap(img));
        _info.Text = $"Стр. {index + 1} из {_pdf.PageCount}";
        _prev.Visible = _next.Visible = _pdf.PageCount > 1;
        _prev.Enabled = index > 0;
        _next.Enabled = index + 1 < _pdf.PageCount;
    }

    private void SetImage(Image img)
    {
        var old = _picture.Image;
        _picture.Image = img;
        old?.Dispose();
        ApplyZoom();
    }

    private void ShowEmpty(string text)
    {
        SetImage(new Bitmap(1, 1));
        _info.Text = text;
        _prev.Visible = _next.Visible = false;
    }

    private void ToggleZoom()
    {
        _actualSize = !_actualSize;
        _zoom.Text = _actualSize ? "Вписать" : "100%";
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_actualSize)
        {
            _picture.Dock = DockStyle.None;
            _picture.SizeMode = PictureBoxSizeMode.AutoSize;
            _picture.Location = Point.Empty;
        }
        else
        {
            _picture.SizeMode = PictureBoxSizeMode.Zoom;
            _picture.Dock = DockStyle.Fill;
        }
    }

    private void OpenExternal()
    {
        if (_path == null || !File.Exists(_path)) return;
        Ui.Safe(FindForm(), () => Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _picture.Image?.Dispose();
        base.Dispose(disposing);
    }
}
