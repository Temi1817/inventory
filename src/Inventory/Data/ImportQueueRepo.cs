using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services.Claude;

namespace Inventory.Data;

public static class QueueStatus
{
    public const string Pending = "Ожидает";
    public const string Processing = "Распознаётся";
    public const string Done = "Распознано";
    public const string Error = "Ошибка";
    public const string Accepted = "Принято";
    public const string Skipped = "Пропущено";
}

public sealed class QueueEntry
{
    public long Id { get; set; }
    public string FilePath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string Status { get; set; } = QueueStatus.Pending;
    public string? Engine { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public string? Number { get; set; }
    public string? Date { get; set; }
    public string? Supplier { get; set; }
    public long? ItemCount { get; set; }
    public long? InvoiceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string AbsolutePath => AppPaths.ToAbsolute(FilePath);
    public string DateText => InvoiceRecognizer.ParseDate(Date)?.ToString("dd.MM.yyyy") ?? Date ?? "";
    public string Note => Status == QueueStatus.Error ? Error ?? "" : Engine ?? "";
}

/// <summary>Очередь «На проверке»: файлы копируются в queue\, состояние — в БД.</summary>
public static class ImportQueueRepo
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Добавить файлы. Уже добавленные раньше (по содержимому) пропускаются.</summary>
    public static (int added, int duplicates) Add(IEnumerable<string> files)
    {
        int added = 0, duplicates = 0;
        foreach (var file in files)
        {
            var hash = Hash(file);
            using var c = Db.Open();
            var exists = c.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM import_queue WHERE file_hash = @hash AND status <> @skipped",
                new { hash, skipped = QueueStatus.Skipped }) > 0;
            if (exists) { duplicates++; continue; }

            var rel = Path.Combine("queue", $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(file).ToLowerInvariant()}");
            File.Copy(file, AppPaths.ToAbsolute(rel));
            var now = DateTime.Now;
            c.Execute("""
                INSERT INTO import_queue(file_path, original_name, file_hash, status, created_at, updated_at)
                VALUES (@rel, @name, @hash, @status, @now, @now)
                """, new { rel, name = Path.GetFileName(file), hash, status = QueueStatus.Pending, now });
            added++;
        }
        return (added, duplicates);
    }

    internal static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static List<QueueEntry> List(bool includeFinished)
    {
        var sql = "SELECT * FROM import_queue";
        if (!includeFinished) sql += " WHERE status NOT IN (@accepted, @skipped)";
        sql += " ORDER BY CASE status WHEN @done THEN 0 WHEN @error THEN 1 WHEN @processing THEN 2 WHEN @pending THEN 3 ELSE 4 END, id";
        using var c = Db.Open();
        return c.Query<QueueEntry>(sql, new
        {
            accepted = QueueStatus.Accepted, skipped = QueueStatus.Skipped, done = QueueStatus.Done,
            error = QueueStatus.Error, processing = QueueStatus.Processing, pending = QueueStatus.Pending,
        }).ToList();
    }

    public static QueueEntry? Get(long id)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<QueueEntry>("SELECT * FROM import_queue WHERE id = @id", new { id });
    }

    public static Dictionary<string, int> Counts()
    {
        using var c = Db.Open();
        return c.Query<(string status, int n)>("SELECT status, COUNT(*) FROM import_queue GROUP BY status")
            .ToDictionary(x => x.status, x => x.n);
    }

    /// <summary>Следующий файл для распознавания (по порядку добавления).</summary>
    public static QueueEntry? NextPending()
    {
        using var c = Db.Open();
        return c.QueryFirstOrDefault<QueueEntry>("SELECT * FROM import_queue WHERE status = @s ORDER BY id LIMIT 1",
            new { s = QueueStatus.Pending });
    }

    /// <summary>После сбоя/закрытия программы «Распознаётся» снова становится «Ожидает».</summary>
    public static int ResetStuck() => SetStatusWhere(QueueStatus.Processing, QueueStatus.Pending);

    public static void SetProcessing(long id) => Update(id, "status = @Status", new { Status = QueueStatus.Processing });

    public static void SetResult(long id, RecognizedInvoice r, string engine) =>
        Update(id, """
            status = @Status, engine = @engine, result_json = @json, error = NULL,
            number = @Number, date = @Date, supplier = @Supplier, item_count = @count
            """, new
        {
            Status = QueueStatus.Done, engine, json = JsonSerializer.Serialize(r, Json),
            r.Number, r.Date, r.Supplier, count = r.Items.Count,
        });

    public static void SetError(long id, string error) =>
        Update(id, "status = @Status, error = @error", new { Status = QueueStatus.Error, error });

    public static void Requeue(long id) =>
        Update(id, "status = @Status, error = NULL", new { Status = QueueStatus.Pending });

    public static void MarkAccepted(long id, long invoiceId)
    {
        Update(id, "status = @Status, invoice_id = @invoiceId", new { Status = QueueStatus.Accepted, invoiceId });
        DeleteFile(id);
    }

    public static void MarkSkipped(long id)
    {
        Update(id, "status = @Status", new { Status = QueueStatus.Skipped });
        DeleteFile(id);
    }

    public static RecognizedInvoice? GetResult(QueueEntry e) =>
        string.IsNullOrEmpty(e.ResultJson) ? null : JsonSerializer.Deserialize<RecognizedInvoice>(e.ResultJson, Json);

    /// <summary>Копия файла после приёма уже лежит в files\ — из queue\ её убираем.</summary>
    private static void DeleteFile(long id)
    {
        var e = Get(id);
        if (e == null) return;
        try { if (File.Exists(e.AbsolutePath)) File.Delete(e.AbsolutePath); }
        catch (Exception ex) { Log.Error("Не удалось удалить файл очереди " + e.FilePath, ex); }
    }

    private static int SetStatusWhere(string from, string to)
    {
        using var c = Db.Open();
        return c.Execute("UPDATE import_queue SET status = @to, updated_at = @now WHERE status = @from",
            new { from, to, now = DateTime.Now });
    }

    private static void Update(long id, string set, object args)
    {
        var p = new DynamicParameters(args);
        p.Add("id", id);
        p.Add("now", DateTime.Now);
        using var c = Db.Open();
        c.Execute($"UPDATE import_queue SET {set}, updated_at = @now WHERE id = @id", p);
    }
}
