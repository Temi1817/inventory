using System.Diagnostics;
using Inventory.Data;
using Inventory.Infrastructure;

namespace Inventory.Services.Recognition;

/// <summary>
/// Фоновое распознавание очереди «На проверке»: по одному файлу, интерфейс не блокируется.
/// События приходят из фонового потока — подписчик сам переходит в UI-поток.
/// </summary>
public sealed class QueueProcessor
{
    public static QueueProcessor Instance { get; } = new();

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly List<double> _durations = [];

    /// <summary>Что-то в очереди изменилось (статус файла, ход работы).</summary>
    public event Action? Changed;

    /// <summary>Подмена выбора распознавателя (для тестов).</summary>
    internal Func<CancellationToken, Task<(IInvoiceRecognizer?, string?)>>? RecognizerFactory { get; set; }

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public string? CurrentFile { get; private set; }
    public string? EngineNote { get; private set; }

    /// <summary>Среднее время на файл за этот запуск (для оценки «осталось ~N мин»).</summary>
    public double? AverageSeconds { get { lock (_sync) return _durations.Count == 0 ? null : _durations.Average(); } }

    public void Start()
    {
        lock (_sync)
        {
            IsPaused = false;
            if (IsRunning) return;
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => LoopAsync(ct));
        }
        Changed?.Invoke();
    }

    /// <summary>Пауза: текущий файл дораспознаётся, следующий не начнётся.</summary>
    public void Pause()
    {
        IsPaused = true;
        Changed?.Invoke();
    }

    /// <summary>Остановить при выходе из программы (текущий файл вернётся в «Ожидает»).</summary>
    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* выходим */ }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        IInvoiceRecognizer? recognizer = null;
        try
        {
            ImportQueueRepo.ResetStuck();
            while (!ct.IsCancellationRequested && !IsPaused)
            {
                var entry = ImportQueueRepo.NextPending();
                if (entry == null) break;

                if (recognizer == null)
                {
                    (recognizer, EngineNote) = RecognizerFactory != null
                        ? await RecognizerFactory(ct)
                        : await Recognizers.CreateAsync(AppSettings.Load(), ct);
                    if (recognizer == null)
                    {
                        ImportQueueRepo.SetError(entry.Id, EngineNote ?? "Распознавание недоступно.");
                        break;
                    }
                }

                ImportQueueRepo.SetProcessing(entry.Id);
                CurrentFile = entry.OriginalName;
                Changed?.Invoke();
                var watch = Stopwatch.StartNew();
                try
                {
                    var result = await recognizer.RecognizeAsync(entry.AbsolutePath, ct);
                    ImportQueueRepo.SetResult(entry.Id, result, recognizer.Name);
                    lock (_sync) _durations.Add(watch.Elapsed.TotalSeconds);
                    Log.Info($"Очередь: {entry.OriginalName} распознан за {watch.Elapsed.TotalSeconds:0.0} с, позиций {result.Items.Count}");
                }
                catch (OperationCanceledException)
                {
                    ImportQueueRepo.Requeue(entry.Id);
                    break;
                }
                catch (RecognitionException ex)
                {
                    ImportQueueRepo.SetError(entry.Id, ex.Message);
                }
                catch (Exception ex)
                {
                    Log.Error("Очередь: ошибка распознавания " + entry.OriginalName, ex);
                    ImportQueueRepo.SetError(entry.Id, string.IsNullOrWhiteSpace(ex.Message) ? "Непредвиденная ошибка (см. лог)" : "Ошибка: " + ex.Message);
                }
                CurrentFile = null;
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Очередь: сбой фонового распознавания", ex);
        }
        finally
        {
            lock (_sync)
            {
                IsRunning = false;
                CurrentFile = null;
                _durations.Clear();
            }
            Changed?.Invoke();
        }
    }
}
