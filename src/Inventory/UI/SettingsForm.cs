using Inventory.Infrastructure;
using Inventory.Services.Claude;
using Inventory.Services.Recognition;

namespace Inventory.UI;

/// <summary>Сервис → Настройки: способ распознавания накладных, Ollama, ключ Claude (DPAPI).</summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly RadioButton _ollama = new() { Text = "Локальный ИИ (Ollama) — бесплатно, без интернета, рекомендуется", AutoSize = true };
    private readonly RadioButton _ocr = new() { Text = "Распознавание Windows — бесплатно, быстро, таблицу читает слабо", AutoSize = true };
    private readonly RadioButton _claude = new() { Text = "Claude API — точнее всех, но платно (нужен ключ)", AutoSize = true };
    private readonly TextBox _ollamaModel = new();
    private readonly TextBox _ollamaUrl = new();
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, PlaceholderText = "sk-ant-…" };
    private readonly TextBox _model = new();
    private readonly TextBox _issuer = new() { PlaceholderText = "например: Иванов И.И." };
    private readonly Label _check = new() { AutoSize = true, MaximumSize = new Size(460, 0), Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _checkButton;

    public SettingsForm()
    {
        Text = "Настройки";
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(640, 540);

        _ollama.Checked = _settings.Engine == RecognitionEngine.Ollama;
        _ocr.Checked = _settings.Engine == RecognitionEngine.WindowsOcr;
        _claude.Checked = _settings.Engine == RecognitionEngine.Claude;
        _ollamaModel.Text = _settings.OllamaModel;
        _ollamaUrl.Text = _settings.OllamaUrl;
        _key.Text = _settings.GetApiKey() ?? "";
        _model.Text = _settings.Model;
        _issuer.Text = _settings.IssuerName ?? "";
        _checkButton = Ui.Button("Проверить", async (_, _) => await CheckAsync());

        var engines = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        engines.Controls.AddRange([_ollama, _ocr, _claude]);

        var table = new FieldTable(150) { Padding = new Padding(12, 12, 12, 0) };
        table.Add("Кто выдаёт (в акте)", _issuer);
        table.Add("Распознавание", engines);
        table.Add("", Section("Локальный ИИ (Ollama)"));
        table.Add("Модель", _ollamaModel);
        table.Add("Адрес Ollama", _ollamaUrl);
        table.Add("", Section("Claude API (платно)"));
        table.Add("API-ключ Claude", _key);
        table.Add("Модель Claude", _model);
        var checkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        checkRow.Controls.AddRange([_checkButton, _check]);
        table.Add("", checkRow);
        table.Add("", new Label
        {
            Text = "Ключ Claude хранится зашифрованным (DPAPI). Если Ollama не запущен, программа сама попробует его запустить, " +
                   "а если не выйдет — распознает накладную средствами Windows.",
            AutoSize = true, MaximumSize = new Size(460, 0), ForeColor = Ui.Muted, Padding = new Padding(0, 8, 0, 0),
        });

        Controls.Add(table);
        Controls.Add(FieldTable.DialogButtons(this, "Сохранить", (_, _) => Save()));
    }

    private static Label Section(string text) => new()
    {
        Text = text, Font = Ui.BoldFont, AutoSize = true, Padding = new Padding(0, 10, 0, 0),
    };

    private string SelectedEngine =>
        _claude.Checked ? RecognitionEngine.Claude : _ocr.Checked ? RecognitionEngine.WindowsOcr : RecognitionEngine.Ollama;

    private async Task CheckAsync()
    {
        _checkButton.Enabled = false;
        SetCheck("Проверяю…", Ui.Muted);
        try
        {
            switch (SelectedEngine)
            {
                case RecognitionEngine.Ollama:
                    var (ok, problem) = await OllamaRecognizer.CheckAsync(Url(), OllamaModelOrDefault());
                    SetCheck(ok ? "✓ Ollama запущен, модель на месте." : problem!, ok ? Color.FromArgb(22, 163, 74) : Ui.Danger);
                    if (!ok && await OllamaRecognizer.GetStateAsync(Url(), OllamaModelOrDefault()) == OllamaState.NoModel
                        && Ui.Confirm(this, $"Скачать модель «{OllamaModelOrDefault()}» сейчас (≈3,3 ГБ)?"))
                    {
                        using var f = new ModelDownloadForm(Url(), OllamaModelOrDefault());
                        if (f.ShowDialog(this) == DialogResult.OK) SetCheck("✓ Модель скачана, Ollama готов.", Color.FromArgb(22, 163, 74));
                    }
                    break;
                case RecognitionEngine.WindowsOcr:
                    var available = WindowsOcrRecognizer.IsAvailable;
                    SetCheck(available ? "✓ Распознавание Windows доступно." : "В Windows нет распознавания для русского языка.",
                        available ? Color.FromArgb(22, 163, 74) : Ui.Danger);
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(_key.Text)) { SetCheck("Введите ключ.", Ui.Danger); break; }
                    await new ClaudeClient(_key.Text, ModelOrDefault()).PingAsync();
                    SetCheck("✓ Ключ Claude работает.", Color.FromArgb(22, 163, 74));
                    break;
            }
        }
        catch (RecognitionException ex) { SetCheck(ex.Message, Ui.Danger); }
        catch (Exception ex) { Log.Error("Проверка распознавания", ex); SetCheck("Ошибка: " + ex.Message, Ui.Danger); }
        finally { if (!IsDisposed) _checkButton.Enabled = true; }
    }

    private void SetCheck(string text, Color color)
    {
        if (IsDisposed) return;
        _check.Text = text;
        _check.ForeColor = color;
    }

    private string ModelOrDefault() => string.IsNullOrWhiteSpace(_model.Text) ? AppSettings.DefaultModel : _model.Text.Trim();
    private string OllamaModelOrDefault() => string.IsNullOrWhiteSpace(_ollamaModel.Text) ? AppSettings.DefaultOllamaModel : _ollamaModel.Text.Trim();
    private string Url() => string.IsNullOrWhiteSpace(_ollamaUrl.Text) ? AppSettings.DefaultOllamaUrl : _ollamaUrl.Text.Trim();

    private void Save()
    {
        if (!Uri.TryCreate(Url(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Ui.Warn(this, "Адрес Ollama указан неверно (пример: http://localhost:11434).");
            return;
        }
        Ui.Safe(this, () =>
        {
            _settings.Engine = SelectedEngine;
            _settings.OllamaModel = OllamaModelOrDefault();
            _settings.OllamaUrl = Url();
            _settings.SetApiKey(_key.Text);
            _settings.Model = ModelOrDefault();
            _settings.IssuerName = string.IsNullOrWhiteSpace(_issuer.Text) ? null : _issuer.Text.Trim();
            _settings.Save();
            Log.Info($"Настройки сохранены: распознавание = {_settings.Engine}");
            DialogResult = DialogResult.OK;
            Close();
        }, "Не удалось сохранить настройки");
    }
}
