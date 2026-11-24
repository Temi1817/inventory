using System.Drawing.Imaging;

namespace Inventory.Infrastructure;

/// <summary>
/// Хранилище файлов: files\ГГГГ\ММ\. В БД пишется только относительный путь.
/// </summary>
public static class FileStore
{
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Скопировать файл в хранилище, вернуть относительный путь.</summary>
    public static string Import(string sourcePath)
    {
        var (relative, absolute) = NewPath(Path.GetExtension(sourcePath));
        File.Copy(sourcePath, absolute);
        return relative;
    }

    /// <summary>Сохранить картинку (например, из буфера обмена) в PNG.</summary>
    public static string SaveImage(Image image)
    {
        var (relative, absolute) = NewPath(".png");
        image.Save(absolute, ImageFormat.Png);
        return relative;
    }

    /// <summary>Загрузить картинку без блокировки файла.</summary>
    public static Image? LoadImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var path = AppPaths.ToAbsolute(relativePath);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        using var img = Image.FromStream(stream);
        return new Bitmap(img);
    }

    private static (string relative, string absolute) NewPath(string extension)
    {
        var now = DateTime.Now;
        var relDir = Path.Combine("files", now.ToString("yyyy"), now.ToString("MM"));
        Directory.CreateDirectory(Path.Combine(AppPaths.Root, relDir));
        var name = $"{now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}{extension.ToLowerInvariant()}";
        var relative = Path.Combine(relDir, name);
        return (relative, Path.Combine(AppPaths.Root, relative));
    }
}
