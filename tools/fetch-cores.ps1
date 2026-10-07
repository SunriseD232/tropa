<#
.SYNOPSIS
  Скачивает ядра строго по tools/cores.lock.json и проверяет их хэши.

.DESCRIPTION
  1. Скачивает архив по URL из lock-файла.
  2. Сверяет SHA-256 архива. Не совпало — остановка, ничего не распаковывается.
  3. Достаёт из архива только перечисленные файлы, сверяет SHA-256 каждого.
  4. Кладёт их в каталог назначения (по умолчанию <repo>/cores). Уже лежащие файлы
     с правильным хэшем не перекачиваются, файлы с неправильным хэшем заменяются.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/fetch-cores.ps1
#>
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'cores')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$lockPath = Join-Path $PSScriptRoot 'cores.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($lock.schema -ne 1) { throw "Неизвестная версия lock-файла: $($lock.schema)" }

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Assert-SafeName([string]$Name) {
    # Целевое имя — только имя файла, без каталогов: защита от записи за пределы каталога назначения.
    if ($Name -ne [IO.Path]::GetFileName($Name) -or $Name -match '[\\/:]' -or $Name.StartsWith('.')) {
        throw "Недопустимое имя целевого файла в lock-файле: $Name"
    }
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ("tropa-cores-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    foreach ($core in $lock.cores) {
        $pending = @()
        foreach ($f in $core.files) {
            Assert-SafeName $f.target
            $target = Join-Path $Destination $f.target
            if ((Test-Path -LiteralPath $target) -and ((Get-Sha256 $target) -eq $f.sha256)) {
                Write-Host "[ok] $($f.target) $($core.version) уже на месте"
            } else {
                $pending += $f
            }
        }
        if ($pending.Count -eq 0) { continue }

        if (-not $core.url.StartsWith('https://github.com/')) { throw "URL вне github.com: $($core.url)" }
        $zip = Join-Path $work ([IO.Path]::GetFileName($core.url))
        Write-Host "Скачиваю $($core.name) $($core.version)…"
        Invoke-WebRequest -Uri $core.url -OutFile $zip -UseBasicParsing
        $zipSha = Get-Sha256 $zip
        if ($zipSha -ne $core.archiveSha256) {
            throw "ХЭШ АРХИВА НЕ СОВПАЛ для $($core.name): ожидался $($core.archiveSha256), получен $zipSha. Ничего не установлено."
        }

        $z = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            foreach ($f in $pending) {
                $entry = $z.Entries | Where-Object { $_.FullName -eq $f.entry }
                if (-not $entry) { throw "В архиве $($core.name) нет $($f.entry)" }
                $tmp = Join-Path $work ([Guid]::NewGuid().ToString('N'))
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmp)
                $sha = Get-Sha256 $tmp
                if ($sha -ne $f.sha256) { throw "ХЭШ ФАЙЛА НЕ СОВПАЛ: $($f.entry): ожидался $($f.sha256), получен $sha" }
                Move-Item -LiteralPath $tmp -Destination (Join-Path $Destination $f.target) -Force
                Write-Host "[new] $($f.target) $($core.version) sha256 $sha"
            }
        } finally { $z.Dispose() }
    }
    Write-Host "Готово: $Destination"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
