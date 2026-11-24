using System.IO.Compression;
using Dapper;
using Inventory.Data;
using Inventory.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Inventory.Services;

/// <summary>
/// Бэкап всей папки данных в zip и восстановление из zip.
/// База копируется через SQLite Backup API — копия целая, даже если база открыта.
/// </summary>
public static class BackupService
{
    public const string BackupsFolder = "backups";
    public const int AutoBackupsToKeep = 14;

    // Не кладём в архив: сами бэкапы (иначе архив растёт сам в себя), логи, временные файлы БД.
    private static readonly string[] SkipFolders = [BackupsFolder, "logs"];
    private static readonly string[] SkipFiles = ["inventory.db", "inventory.db-wal", "inventory.db-shm", "settings.json.tmp"];

    public static string BackupsPath => Path.Combine(AppPaths.Root, BackupsFolder);

    public static string DefaultFileName() => $"Inventory-backup-{DateTime.Now:yyyy-MM-dd_HHmm}.zip";

    /// <summary>Создать архив. Возвращает размер в байтах.</summary>
    public static long Create(string zipPath)
    {
        var tmpDb = Path.Combine(Path.GetTempPath(), $"inventory-backup-{Guid.NewGuid():N}.db");
        var tmpZip = zipPath + ".tmp";
        try
        {
            using (var source = Db.Open())
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmpDb, Pooling = false }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
            }

            if (File.Exists(tmpZip)) File.Delete(tmpZip);
            using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tmpDb, "inventory.db", CompressionLevel.Optimal);
                foreach (var file in Directory.EnumerateFiles(AppPaths.Root, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(AppPaths.Root, file);
                    var top = rel.Split(Path.DirectorySeparatorChar)[0];
                    if (SkipFolders.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
                    if (SkipFiles.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
                    // Фото и PDF уже сжаты — не тратим время.
                    zip.CreateEntryFromFile(file, rel.Replace('\\', '/'), CompressionLevel.Fastest);
                }
            }
            File.Move(tmpZip, zipPath, overwrite: true);
            var size = new FileInfo(zipPath).Length;
            Log.Info($"Бэкап создан: {zipPath} ({size / 1024} КБ)");
            return size;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tmpDb);
            TryDelete(tmpZip);
        }
    }

    /// <summary>Автобэкап раз в сутки в backups\, храним последние 14.</summary>
    public static string? AutoBackupIfDue()
    {
        Directory.CreateDirectory(BackupsPath);
        var autos = Directory.GetFiles(BackupsPath, "auto-*.zip").OrderByDescending(File.GetLastWriteTime).ToList();
        if (autos.Count > 0 && File.GetLastWriteTime(autos[0]) > DateTime.Now.AddHours(-20)) return null;

        var path = Path.Combine(BackupsPath, $"auto-{DateTime.Now:yyyy-MM-dd_HHmm}.zip");
        Create(path);
        foreach (var old in Directory.GetFiles(BackupsPath, "auto-*.zip").OrderByDescending(File.GetLastWriteTime).Skip(AutoBackupsToKeep))
            TryDelete(old);
        return path;
    }

    /// <summary>Проверить архив до восстановления: есть база и её схема не новее программы.</summary>
    public static (int schema, int items, int employees) Inspect(string zipPath)
    {
        var dir = ExtractToTemp(zipPath);
        try { return InspectDb(Path.Combine(dir, "inventory.db")); }
        finally { TryDeleteDir(dir); }
    }

    /// <summary>
    /// Восстановить данные из архива. Перед вызовом — остановить фоновые задачи; после — перезапустить программу.
    /// Текущие данные сначала сохраняются в backups\before-restore-*.zip.
    /// </summary>
    public static string Restore(string zipPath)
    {
        var dir = ExtractToTemp(zipPath);
        try
        {
            InspectDb(Path.Combine(dir, "inventory.db"));

            var safety = Path.Combine(BackupsPath, $"before-restore-{DateTime.Now:yyyy-MM-dd_HHmmss}.zip");
            Directory.CreateDirectory(BackupsPath);
            Create(safety);

            Db.ReleaseAll();
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // Удаляем текущие данные (кроме бэкапов и логов) и кладём содержимое архива.
            foreach (var entry in Directory.EnumerateFileSystemEntries(AppPaths.Root))
            {
                var name = Path.GetFileName(entry);
                if (SkipFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(AppPaths.Root, Path.GetRelativePath(dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            AppPaths.EnsureCreated();
            Log.Info($"Данные восстановлены из {zipPath}. Предыдущие данные: {safety}");
            return safety;
        }
        finally { TryDeleteDir(dir); }
    }

    private static string ExtractToTemp(string zipPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"inventory-restore-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, dir);   // защищено от путей вида ..\ — выходит ошибкой
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDeleteDir(dir);
            throw new ArgumentException("Файл не похож на бэкап программы (не открывается как zip).", ex);
        }
        if (!File.Exists(Path.Combine(dir, "inventory.db")))
        {
            TryDeleteDir(dir);
            throw new ArgumentException("В архиве нет базы inventory.db — это не бэкап программы.");
        }
        return dir;
    }

    private static (int schema, int items, int employees) InspectDb(string dbPath)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            var schema = c.ExecuteScalar<int?>("SELECT MAX(version) FROM schema_version") ?? 0;
            if (schema > Migrations.LatestVersion)
                throw new ArgumentException($"Бэкап сделан более новой версией программы (схема v{schema}). Сначала обновите программу.");
            return (schema, c.ExecuteScalar<int>("SELECT COUNT(*) FROM items"), c.ExecuteScalar<int>("SELECT COUNT(*) FROM employees"));
        }
        catch (SqliteException ex)
        {
            throw new ArgumentException("База в архиве повреждена или это не база программы.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* не критично */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* не критично */ }
    }
}
