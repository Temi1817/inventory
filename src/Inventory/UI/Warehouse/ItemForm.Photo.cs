using Inventory.Infrastructure;

namespace Inventory.UI.Warehouse;

/// <summary>Фото в карточке: перетащить, выбрать или вставить Ctrl+V.</summary>
public sealed partial class ItemForm
{
    private readonly PictureBox _photo = new()
    {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(248, 250, 252),
    };

    // Ожидающие изменения фото (применяются при «Сохранить»).
    private string? _pendingPhotoFile;
    private Image? _pendingPhotoImage;
    private bool _photoRemoved;

    private Control BuildPhotoPanel()
    {
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(Ui.Button("Выбрать…", (_, _) => ChoosePhoto()));
        buttons.Controls.Add(Ui.Button("Убрать", (_, _) => RemovePhoto()));

        var hint = new Label
        {
            Text = "Перетащите фото сюда или вставьте Ctrl+V", Dock = DockStyle.Bottom, Height = 22,
            ForeColor = Ui.Muted, TextAlign = ContentAlignment.MiddleCenter,
        };

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 0, 0) };
        panel.Controls.Add(_photo);
        panel.Controls.Add(hint);
        panel.Controls.Add(buttons);
        return panel;
    }

    private void SetupPhoto()
    {
        try { _photo.Image = FileStore.LoadImage(_original.PhotoPath); }
        catch (Exception ex) { Log.Error("Не удалось загрузить фото", ex); }

        DragEnter += (_, e) =>
        {
            if (GetDroppedImageFile(e.Data) != null) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (GetDroppedImageFile(e.Data) is { } file) SetPhotoFromFile(file);
        };
        FormClosed += (_, _) => _photo.Image?.Dispose();
    }

    private static string? GetDroppedImageFile(IDataObject? data) =>
        data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && FileStore.IsImage(files[0])
            ? files[0] : null;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V) && TryPastePhoto()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Вставка из буфера: картинка или скопированный файл-картинка. Текст — обычная вставка.</summary>
    private bool TryPastePhoto()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                SetPhoto(new Bitmap(img), null);
                img.Dispose();
                return true;
            }
            if (Clipboard.ContainsFileDropList())
            {
                var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(FileStore.IsImage);
                if (file != null) { SetPhotoFromFile(file); return true; }
            }
        }
        catch (Exception ex) { Ui.Error(this, "Не удалось вставить фото из буфера", ex); return true; }
        return false;
    }

    private void ChoosePhoto()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Фото вещи",
            Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|Все файлы|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetPhotoFromFile(dlg.FileName);
    }

    private void SetPhotoFromFile(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var img = Image.FromStream(stream);
            SetPhoto(new Bitmap(img), file);
        }
        catch (Exception ex) { Ui.Error(this, "Файл не похож на изображение", ex); }
    }

    private void SetPhoto(Image preview, string? sourceFile)
    {
        _photo.Image?.Dispose();
        _photo.Image = preview;
        _pendingPhotoFile = sourceFile;
        _pendingPhotoImage = sourceFile == null ? preview : null;
        _photoRemoved = false;
    }

    private void RemovePhoto()
    {
        _photo.Image?.Dispose();
        _photo.Image = null;
        _pendingPhotoFile = null;
        _pendingPhotoImage = null;
        _photoRemoved = true;
    }

    /// <summary>Сохранить ожидающее фото в хранилище, вернуть новый относительный путь.</summary>
    private string? CommitPhoto(string? currentPath)
    {
        if (_pendingPhotoFile != null) return FileStore.Import(_pendingPhotoFile);
        if (_pendingPhotoImage != null) return FileStore.SaveImage(_pendingPhotoImage);
        return _photoRemoved ? null : currentPath;
    }
}
