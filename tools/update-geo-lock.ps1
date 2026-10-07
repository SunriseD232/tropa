<#
.SYNOPSIS
  Закрепляет наборы правил (*.srs) в tools/geo.lock.json по SHA коммита и хэшу содержимого.

.DESCRIPTION
  Запускается ВРУЧНУЮ. Берёт текущую вершину веток с наборами правил, формирует неизменяемые
  ссылки вида raw.githubusercontent.com/<repo>/<commit>/<path>, скачивает файлы и записывает
  их SHA-256. Ежедневные обновления баз у пользователей появятся на этапе 7 через подписанный манифест.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/update-geo-lock.ps1
#>
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Источник — runetfreedom/russia-v2ray-rules-dat (официальный источник российских гео-баз v2rayN), обновляется каждые 6 часов.
$sources = @(
    @{ Name = 'geosite-category-ru'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-category-ru.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geosite-category-gov-ru'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-category-gov-ru.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geosite-category-bank-ru'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-category-bank-ru.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geosite-ru-available-only-inside'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-ru-available-only-inside.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geoip-ru'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geoip/geoip-ru.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geosite-ru-blocked'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-ru-blocked.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geoip-ru-blocked'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geoip/geoip-ru-blocked.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geoip-ru-blocked-community'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geoip/geoip-ru-blocked-community.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geosite-category-ai-noncn'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-category-ai-%21cn.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geosite-youtube'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-youtube.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geosite-discord'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-discord.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geosite-telegram'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geosite/geosite-telegram.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' },
    @{ Name = 'geoip-telegram'; Repo = 'runetfreedom/russia-v2ray-rules-dat'; Branch = 'release'; Path = 'sing-box/rule-set-geoip/geoip-telegram.srs'; License = 'GPL-3.0 (runetfreedom/russia-v2ray-rules-dat)' }
)


$work = Join-Path ([IO.Path]::GetTempPath()) ("tropa-geo-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $commits = @{}
    $files = @()
    foreach ($s in $sources) {
        $key = "$($s.Repo)@$($s.Branch)"
        if (-not $commits.ContainsKey($key)) {
            $c = Invoke-RestMethod -Uri "https://api.github.com/repos/$($s.Repo)/commits/$($s.Branch)" -Headers @{ 'User-Agent' = 'tropa-lock' }
            $commits[$key] = $c.sha
        }
        $sha = $commits[$key]
        $url = "https://raw.githubusercontent.com/$($s.Repo)/$sha/$($s.Path)"
        $out = Join-Path $work ($s.Name + '.srs')
        Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $out).Hash.ToLowerInvariant()
        $files += [ordered]@{ name = $s.Name; url = $url; sha256 = $hash; size = (Get-Item $out).Length; license = $s.License }
        Write-Host "$($s.Name)  $sha  $hash"
    }

    $lock = [ordered]@{ schema = 1; updated = (Get-Date).ToString('yyyy-MM-dd'); files = $files }
    $path = Join-Path $PSScriptRoot 'geo.lock.json'
    [IO.File]::WriteAllText($path, ($lock | ConvertTo-Json -Depth 5) + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "Записано: $path. Проверьте diff перед коммитом."
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
