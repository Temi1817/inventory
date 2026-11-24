using System.IO.Compression;
using Dapper;
using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services;

namespace Inventory.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _out = Path.Combine(Path.GetTempPath(), "inventory-backups-" + Guid.NewGuid().ToString("N"));

    public BackupTests()
    {
        AppPaths.UseRoot(_root);
        AppPaths.EnsureCreated();
        Db.Init(AppPaths.Database);
        Migrations.Run();
        Directory.CreateDirectory(_out);
    }

    public void Dispose()
    {
        Db.ReleaseAll();
        foreach (var d in new[] { _root, _out }) try { Directory.Delete(d, true); } catch { /* временная папка */ }
    }

    [Fact]
    public void Backup_Then_Restore_ReturnsOriginalData_AndFiles()
    {
        var emp = EmployeeRepo.Insert(new Employee { FullName = "Иванов" });
        var photoSrc = Path.Combine(_out, "photo.png");
        using (var bmp = new Bitmap(10, 10)) bmp.Save(photoSrc);
        var photo = FileStore.Import(photoSrc);
        ItemRepo.Insert(new Item { Name = "Ноутбук", PhotoPath = photo, EmployeeId = null }, DateTime.Today);

        var zip = Path.Combine(_out, "b.zip");
        Assert.True(BackupService.Create(zip) > 0);
        using (var a = ZipFile.OpenRead(zip))
        {
            Assert.Contains(a.Entries, e => e.FullName == "inventory.db");
            Assert.Contains(a.Entries, e => e.FullName == photo.Replace('\\', '/'));
            Assert.DoesNotContain(a.Entries, e => e.FullName.StartsWith("backups/"));
        }
        Assert.Equal((Migrations.LatestVersion, 1, 1), BackupService.Inspect(zip));

        // «Испортили» данные после бэкапа.
        EmployeeRepo.Insert(new Employee { FullName = "Лишний" });
        File.Delete(AppPaths.ToAbsolute(photo));

        var safety = BackupService.Restore(zip);
        Db.Init(AppPaths.Database);

        Assert.Equal(["Иванов"], EmployeeRepo.List().Select(e => e.FullName));
        Assert.True(File.Exists(AppPaths.ToAbsolute(photo)));
        Assert.True(File.Exists(safety));                       // прежние данные сохранены перед восстановлением
        Assert.Equal(2, BackupService.Inspect(safety).employees);
        Assert.Equal(emp, EmployeeRepo.List()[0].Id);
    }

    [Fact]
    public void Restore_RejectsForeignZip_AndNewerSchema()
    {
        var foreign = Path.Combine(_out, "foreign.zip");
        using (var a = ZipFile.Open(foreign, ZipArchiveMode.Create)) a.CreateEntry("readme.txt");
        Assert.Throws<ArgumentException>(() => BackupService.Inspect(foreign));

        var notZip = Path.Combine(_out, "not.zip");
        File.WriteAllText(notZip, "hello");
        Assert.Throws<ArgumentException>(() => BackupService.Inspect(notZip));

        var newer = Path.Combine(_out, "newer.zip");
        BackupService.Create(newer);
        using (var a = ZipFile.Open(newer, ZipArchiveMode.Update))
        {
            var dbFile = Path.Combine(_out, "x.db");
            a.GetEntry("inventory.db")!.ExtractToFile(dbFile);
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile};Pooling=False"))
            {
                c.Open();
                c.Execute("INSERT INTO schema_version(version) VALUES (999)");
            }
            a.GetEntry("inventory.db")!.Delete();
            a.CreateEntryFromFile(dbFile, "inventory.db");
        }
        var ex = Assert.Throws<ArgumentException>(() => BackupService.Inspect(newer));
        Assert.Contains("новее", ex.Message.Replace("более новой", "новее"));
    }

    [Fact]
    public void AutoBackup_OncePerDay_KeepsLast14()
    {
        Assert.NotNull(BackupService.AutoBackupIfDue());
        Assert.Null(BackupService.AutoBackupIfDue());   // второй раз за день — не нужен

        // Старые автобэкапы: оставляем только 14 последних.
        for (var i = 0; i < 20; i++)
        {
            var p = Path.Combine(BackupService.BackupsPath, $"auto-old-{i:D2}.zip");
            File.WriteAllText(p, "x");
            File.SetLastWriteTime(p, DateTime.Now.AddDays(-2 - i));
        }
        foreach (var f in Directory.GetFiles(BackupService.BackupsPath, "auto-2*.zip")) File.SetLastWriteTime(f, DateTime.Now.AddDays(-1));
        Assert.NotNull(BackupService.AutoBackupIfDue());
        Assert.Equal(BackupService.AutoBackupsToKeep, Directory.GetFiles(BackupService.BackupsPath, "auto-*.zip").Length);
    }
}
