using Inventory.Infrastructure;
using Inventory.Services.Recognition;

namespace Inventory.UI;

/// <summary>Скачивание модели ИИ для Ollama с прогрессом. DialogResult.OK — модель скачана.</summary>
public sealed class ModelDownloadForm : Form
{
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22, Maximum = 1000 };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 44, ForeColor = Ui.Muted, Padding = new Padding(0, 8, 0, 0) };
    private readonly Button _cancel;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _url, _model;

    public ModelDownloadForm(string url, string model)
    {
        _url = url;
        _model = model;
        Text = "Скачивание модели ИИ";
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(520, 170);
        BackColor = Color.White;
        Padding = new Padding(16);

        var title = new Label
        {
            Text = $"Скачивается модель «{model}» (≈3,3 ГБ) — один раз.\nМожно прервать: в следующий раз докачается с места остановки.",
            Dock = DockStyle.Top, Height = 48,
        };
        _cancel = Ui.Button("Прервать", (_, _) => { _cts.Cancel(); _cancel!.Enabled = false; });
        var buttons = Ui.Toolbar();
        buttons.Dock = DockStyle.Bottom;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Controls.Add(_cancel);

        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(title);
        Controls.Add(buttons);

        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, _) => _cts.Cancel();
    }

    private async Task RunAsync()
    {
        var progress = new Progress<(string status, double? percent)>(p =>
        {
            if (IsDisposed) return;
            if (p.percent is { } pct)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = (int)Math.Clamp(pct * 10, 0, 1000);
                _status.Text = $"{pct:0.0} %   ·   {StatusText(p.status)}";
            }
            else
            {
                _bar.Style = ProgressBarStyle.Marquee;
                _status.Text = StatusText(p.status);
            }
        });
        try
        {
            await OllamaRecognizer.PullAsync(_url, _model, progress, _cts.Token);
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) DialogResult = DialogResult.Cancel;
        }
        catch (Exception ex)
        {
            Log.Error("Скачивание модели Ollama", ex);
            if (IsDisposed) return;
            Ui.Warn(this, ex is RecognitionException || ex is HttpRequestException
                ? "Не удалось скачать модель: " + ex.Message + "\n\nПроверьте интернет и попробуйте ещё раз (Сервис → Настройки → «Скачать модель»)."
                : "Не удалось скачать модель: " + ex.Message);
            DialogResult = DialogResult.Cancel;
        }
    }

    private static string StatusText(string status) => status switch
    {
        "pulling manifest" => "Получаю описание модели…",
        "verifying sha256 digest" => "Проверяю скачанное…",
        "writing manifest" => "Сохраняю…",
        var s when s.StartsWith("removing") => "Сохраняю…",
        "success" => "Готово",
        var s when s.StartsWith("pulling") => "Скачиваю…",
        var s => s,
    };

    /// <summary>
    /// При запуске: если выбран Ollama, он установлен, а модели нет — предложить скачать.
    /// Тихо выходит, если Ollama не установлен (тогда работает запасное распознавание Windows).
    /// </summary>
    public static async Task OfferIfMissingAsync(Form owner)
    {
        var s = AppSettings.Load();
        if (s.Engine != RecognitionEngine.Ollama) return;
        OllamaState state;
        try { state = await OllamaRecognizer.GetStateAsync(s.OllamaUrl, s.OllamaModel); }
        catch (Exception ex) { Log.Error("Проверка Ollama при запуске", ex); return; }
        if (state != OllamaState.NoModel || owner.IsDisposed) return;
        if (!Ui.Confirm(owner, $"Для бесплатного распознавания накладных нужно один раз скачать модель ИИ «{s.OllamaModel}» (≈3,3 ГБ, 5–15 минут).\n\nСкачать сейчас?"))
            return;
        using var f = new ModelDownloadForm(s.OllamaUrl, s.OllamaModel);
        if (f.ShowDialog(owner) == DialogResult.OK)
            Ui.Info(owner, "Модель скачана — накладные теперь распознаются бесплатно, без интернета.");
    }
}
