<#
.SYNOPSIS
  Скачивает наборы правил строго по tools/geo.lock.json и проверяет SHA-256.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/fetch-geo.ps1
#>
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'geo')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'geo.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($lock.schema -ne 1) { throw "Неизвестная версия lock-файла: $($lock.schema)" }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

foreach ($f in $lock.files) {
    if ($f.name -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') { throw "Недопустимое имя: $($f.name)" }
    if ($f.url -notmatch '^https://raw\.githubusercontent\.com/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/[0-9a-f]{40}/') {
        throw "URL должен указывать на конкретный коммит raw.githubusercontent.com: $($f.url)"
    }
    $target = Join-Path $Destination ($f.name + '.srs')
    if ((Test-Path -LiteralPath $target) -and ((Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant() -eq $f.sha256)) {
        Write-Host "[ok] $($f.name)"
        continue
    }
    $tmp = "$target.download"
    Invoke-WebRequest -Uri $f.url -OutFile $tmp -UseBasicParsing
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $tmp).Hash.ToLowerInvariant()
    if ($hash -ne $f.sha256) {
        Remove-Item -LiteralPath $tmp -Force
        throw "ХЭШ НЕ СОВПАЛ для $($f.name): ожидался $($f.sha256), получен $hash"
    }
    Move-Item -LiteralPath $tmp -Destination $target -Force
    Write-Host "[new] $($f.name) $hash"
}
Write-Host "Готово: $Destination"
