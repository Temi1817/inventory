; Установщик «Инвентаризация». Собирается скриптом scripts\build-installer.ps1.
; Данные пользователя (%AppData%\Inventory) установщик и деинсталлятор НЕ трогают.

#define AppName "Инвентаризация"
#define AppVersion "1.0.0"
#define AppExe "Inventory.exe"
#define AppPublisher "Аитке Т.Р."

[Setup]
AppId={{8E4C3B1A-6F2D-4C7E-9B5A-1D2E3F4A5B6C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
; Установка в профиль пользователя — права администратора не нужны.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Inventory
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Inventory-Setup-{#AppVersion}
SetupIconFile=..\src\Inventory\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"
; Показывается, только если Ollama ещё не установлен.
Name: "ollama"; Description: "Установить бесплатный ИИ для распознавания накладных (Ollama — скачается из интернета ≈1,8 ГБ, модель ≈3,3 ГБ программа скачает при первом запуске)"; GroupDescription: "Распознавание накладных:"; Check: not OllamaInstalled

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  OllamaUrl = 'https://ollama.com/download/OllamaSetup.exe';

var
  DownloadPage: TDownloadWizardPage;
  OllamaDownloaded: Boolean;

function OllamaInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Programs\Ollama\ollama.exe'));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Скачивание Ollama', 'Скачивается бесплатный ИИ для распознавания накладных. Это займёт несколько минут.', nil);
end;

{ Скачать установщик Ollama после «Установить». Если не вышло — программа всё равно ставится
  (распознавание будет через Windows OCR, Ollama можно поставить позже с ollama.com). }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not WizardIsTaskSelected('ollama') then Exit;
  DownloadPage.Clear;
  DownloadPage.Add(OllamaUrl, 'OllamaSetup.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      OllamaDownloaded := True;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox('Не удалось скачать Ollama: ' + GetExceptionMessage + #13#10#13#10 +
          'Программа установится без него. Ollama можно поставить позже с сайта ollama.com.', mbError, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep <> ssPostInstall) or not OllamaDownloaded then Exit;
  WizardForm.StatusLabel.Caption := 'Устанавливается Ollama…';
  if not Exec(ExpandConstant('{tmp}\OllamaSetup.exe'), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-', '', SW_SHOW,
              ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    SuppressibleMsgBox('Ollama не установился (код ' + IntToStr(ResultCode) + '). Его можно поставить вручную с ollama.com.',
      mbError, MB_OK, IDOK);
end;

[Messages]
ru.FinishedLabel=Установка завершена.%n%nДанные программы хранятся в %%AppData%%\Inventory — при удалении или обновлении программы они сохраняются.
