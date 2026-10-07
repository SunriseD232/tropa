<#
.SYNOPSIS
  Установка Тропы для проверки режима «Весь компьютер» (до появления настоящего установщика, этап 7).

.DESCRIPTION
  Запускать из PowerShell ОТ ИМЕНИ АДМИНИСТРАТОРА в корне репозитория.
  1. Собирает интерфейс и службу (Release) во временную папку.
  2. Останавливает и удаляет прежнюю службу Тропы, если она есть (с откатом её изменений).
  3. Копирует файлы в C:\Program Files\Tropa — туда может писать только администратор,
     поэтому служба доверяет только Tropa.exe из этой папки.
  4. Регистрирует и запускает службу «Тропа» (LocalSystem, отложенный автозапуск).

  Удаление: -Uninstall. Служба откатывает свои изменения, затем удаляется вместе с папкой.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/dev-install.ps1
  powershell -ExecutionPolicy Bypass -File tools/dev-install.ps1 -Uninstall
#>
param(
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'Tropa'
$repo = Split-Path -Parent $PSScriptRoot

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Нужны права администратора: откройте PowerShell через «Запуск от имени администратора».'
    exit 5
}

function Remove-ExistingService {
    $svc = Get-Service -Name 'Tropa' -ErrorAction SilentlyContinue
    if ($null -eq $svc) { return }
    $exe = Join-Path $target 'Tropa.Service.exe'
    if (Test-Path -LiteralPath $exe) {
        & $exe --uninstall | Out-Host
    } else {
        Stop-Service -Name 'Tropa' -Force -ErrorAction SilentlyContinue
        sc.exe delete Tropa | Out-Host
    }
}

# Закрываем интерфейс: он держит файлы и канал службы.
Get-Process -Name 'Tropa' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force

if ($Uninstall) {
    Remove-ExistingService
    # Удаляем только нашу папку и только если это действительно она.
    if ((Test-Path -LiteralPath (Join-Path $target 'Tropa.Service.exe')) -and (Test-Path -LiteralPath (Join-Path $target 'Tropa.exe'))) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    Write-Host 'Тропа удалена. Системные настройки, изменённые службой, возвращены.'
    exit 0
}

& (Join-Path $repo 'tools\fetch-cores.ps1')
& (Join-Path $repo 'tools\fetch-geo.ps1')

$stage = Join-Path ([IO.Path]::GetTempPath()) ('tropa-publish-' + [Guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $repo 'src\Tropa.Service\Tropa.Service.csproj') -c Release -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка службы не удалась.' }
dotnet publish (Join-Path $repo 'src\Tropa.App\Tropa.App.csproj') -c Release -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка интерфейса не удалась.' }

Remove-ExistingService
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $stage '*') -Destination $target -Recurse -Force
Remove-Item -LiteralPath $stage -Recurse -Force

& (Join-Path $target 'Tropa.Service.exe') --install
if ($LASTEXITCODE -ne 0) { throw "Служба не установилась (код $LASTEXITCODE)." }

Write-Host ''
Write-Host "Готово. Запускайте интерфейс: $target\Tropa.exe"
Write-Host 'Режим «Весь компьютер» теперь доступен. Удаление: tools/dev-install.ps1 -Uninstall'
