<#
.SYNOPSIS
  Закрепляет новые версии ядер в tools/cores.lock.json.

.DESCRIPTION
  Запускается ВРУЧНУЮ владельцем после того, как он прочитал changelog новой версии.
  Скачивает архивы, сверяет SHA-256 архива с дайджестом, который публикует GitHub,
  (для Xray — дополнительно с файлом .dgst), вычисляет SHA-256 нужных исполняемых файлов
  и записывает всё в lock-файл. Ничего не устанавливает.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/update-cores-lock.ps1 -SingBox 1.14.2 -Xray 26.9.30
#>
param(
    [Parameter(Mandatory = $true)][string]$SingBox,
    [Parameter(Mandatory = $true)][string]$Xray
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $PSScriptRoot 'cores.lock.json'
$work = Join-Path ([IO.Path]::GetTempPath()) ("tropa-lock-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-ReleaseAsset([string]$Repo, [string]$Tag, [string]$AssetName) {
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers @{ 'User-Agent' = 'tropa-lock' }
    $asset = $rel.assets | Where-Object { $_.name -eq $AssetName }
    if (-not $asset) { throw "В релизе $Repo $Tag нет файла $AssetName" }
    if (-not $asset.digest -or -not $asset.digest.StartsWith('sha256:')) { throw "GitHub не отдал дайджест для $AssetName" }
    return [pscustomobject]@{ Url = $asset.browser_download_url; Digest = $asset.digest.Substring(7).ToLowerInvariant(); Assets = $rel.assets }
}

function Get-Archive([string]$Url, [string]$ExpectedSha) {
    $file = Join-Path $work ([IO.Path]::GetFileName($Url))
    Invoke-WebRequest -Uri $Url -OutFile $file -UseBasicParsing
    $actual = Get-Sha256 $file
    if ($actual -ne $ExpectedSha) { throw "SHA-256 архива $Url не совпал с дайджестом GitHub: $actual != $ExpectedSha" }
    return $file
}

function Get-EntrySha([string]$Zip, [string]$EntryPath) {
    $z = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $entry = $z.Entries | Where-Object { $_.FullName -eq $EntryPath }
        if (-not $entry) { throw "В архиве $Zip нет $EntryPath. Есть: $(($z.Entries | ForEach-Object FullName) -join ', ')" }
        $out = Join-Path $work ([Guid]::NewGuid().ToString('N'))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out)
        return Get-Sha256 $out
    } finally { $z.Dispose() }
}

try {
    # sing-box
    $sbName = "sing-box-$SingBox-windows-amd64.zip"
    $sb = Get-ReleaseAsset 'SagerNet/sing-box' "v$SingBox" $sbName
    $sbZip = Get-Archive $sb.Url $sb.Digest
    $sbExePath = "sing-box-$SingBox-windows-amd64/sing-box.exe"
    $sbExeSha = Get-EntrySha $sbZip $sbExePath

    # Xray
    $xName = 'Xray-windows-64.zip'
    $x = Get-ReleaseAsset 'XTLS/Xray-core' "v$Xray" $xName
    $xZip = Get-Archive $x.Url $x.Digest
    $dgstAsset = $x.Assets | Where-Object { $_.name -eq "$xName.dgst" }
    if (-not $dgstAsset) { throw 'У Xray нет файла .dgst' }
    $dgst = (Invoke-WebRequest -Uri $dgstAsset.browser_download_url -UseBasicParsing).Content
    if ($dgst -is [byte[]]) { $dgst = [Text.Encoding]::UTF8.GetString($dgst) }
    if ($dgst -notmatch ('(?im)^SHA2-256=\s*' + $x.Digest + '\s*$')) { throw "SHA-256 Xray не совпал с файлом .dgst" }
    $xExeSha = Get-EntrySha $xZip 'xray.exe'

    $lock = [ordered]@{
        schema = 1
        updated = (Get-Date).ToString('yyyy-MM-dd')
        cores = @(
            [ordered]@{
                name = 'sing-box'; version = $SingBox
                source = 'https://github.com/SagerNet/sing-box'; license = 'GPL-3.0-or-later'
                url = $sb.Url; archiveSha256 = $sb.Digest
                files = @([ordered]@{ entry = $sbExePath; target = 'sing-box.exe'; sha256 = $sbExeSha })
            },
            [ordered]@{
                name = 'xray'; version = $Xray
                source = 'https://github.com/XTLS/Xray-core'; license = 'MPL-2.0'
                url = $x.Url; archiveSha256 = $x.Digest
                files = @([ordered]@{ entry = 'xray.exe'; target = 'xray.exe'; sha256 = $xExeSha })
            }
        )
    }
    $json = $lock | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($lockPath, $json + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "Записано: $lockPath"
    Write-Host "sing-box $SingBox  exe sha256 $sbExeSha"
    Write-Host "xray     $Xray  exe sha256 $xExeSha"
    Write-Host 'Проверьте diff lock-файла перед коммитом.'
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
