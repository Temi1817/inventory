# Сборка установщика: тесты → publish (self-contained, .NET ставить не нужно) → Inno Setup → dist\Inventory-Setup-1.0.0.exe
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host '1/4 Тесты…'
dotnet test --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли — установщик не собран.' }

Write-Host '2/4 Публикация (self-contained win-x64)…'
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src/Inventory -c Release -r win-x64 --self-contained true -o publish --nologo -v q `
    -p:DebugType=none -p:SatelliteResourceLanguages=ru
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой.' }

Write-Host '3/4 Поиск Inno Setup…'
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup не найден. Установите: winget install JRSoftware.InnoSetup' }

Write-Host '4/4 Сборка установщика…'
New-Item -ItemType Directory -Force dist | Out-Null
& $iscc /Q installer\Inventory.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup завершился с ошибкой.' }

$setup = Get-Item dist\Inventory-Setup-*.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("Готово: {0} ({1:N1} МБ)" -f $setup.FullName, ($setup.Length / 1MB))
