<#
.SYNOPSIS
  Сборка выпуска Тропы: самодостаточная программа win-x64, установщик Inno Setup, SHA256SUMS.

.DESCRIPTION
  1. Скачивает ядра и наборы правил по lock-файлам (с проверкой хэшей).
  2. Прогоняет тесты (можно пропустить -SkipTests, но для выпуска не надо).
  3. Публикует интерфейс и службу в out\publish (самодостаточные, .NET ставить не нужно).
  4. Собирает установщик, если найден Inno Setup 6 (ISCC.exe).
  5. Пишет out\SHA256SUMS.txt.
  Манифест обновлений подписывает владелец отдельно (docs/10-release.md): закрытого ключа здесь нет.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
#>
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo 'out'
$publish = Join-Path $out 'publish'

[xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Encoding UTF8
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Тропа $version"

& (Join-Path $repo 'tools\fetch-cores.ps1')
& (Join-Path $repo 'tools\fetch-geo.ps1')

if (-not $SkipTests) {
    dotnet test (Join-Path $repo 'Tropa.slnx') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли — выпуск отменён.' }
}

# Удаляем только свой каталог сборки.
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

foreach ($project in @('src\Tropa.Service\Tropa.Service.csproj', 'src\Tropa.App\Tropa.App.csproj')) {
    dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -o $publish --nologo -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw "Сборка $project не удалась." }
}

# Отладочные символы в установщик не кладём.
Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -Recurse | Remove-Item -Force
Get-ChildItem -LiteralPath $publish -Filter 'packages.lock.json' | Remove-Item -Force

$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

$artifacts = @()
if ($iscc) {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$publish" "/DOutputDir=$out" (Join-Path $repo 'installer\tropa.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Установщик не собрался.' }
    $artifacts += Join-Path $out "Tropa-$version-setup.exe"
} else {
    Write-Warning 'Inno Setup 6 не найден — установщик не собран. Программа лежит в out\publish.'
}

$sums = foreach ($file in $artifacts) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $file)
}
if ($sums) {
    Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Value $sums -Encoding ASCII
    Write-Host ''
    $sums | ForEach-Object { Write-Host $_ }
}
Write-Host ''
Write-Host 'Дальше — подпись манифеста по docs/10-release.md.'
