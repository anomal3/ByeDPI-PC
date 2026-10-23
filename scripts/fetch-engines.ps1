<#
  Скачивает движки из официальных релизов в engine\<rid>\ и проверяет SHA256.
    byedpi (ciadpi)  — https://github.com/hufrea/byedpi        (MIT)
    sing-box         — https://github.com/SagerNet/sing-box    (GPL-3.0-or-later)
  Запуск: powershell -ExecutionPolicy Bypass -File scripts\fetch-engines.ps1 [-Rid win-x64]
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

. "$PSScriptRoot\engines.ps1"

$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root "engine\$Rid"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("byedpi-pc-engines-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dest, $tmp | Out-Null

function Get-Checked($url, $file, $sha) {
    Write-Host "Скачиваю $url"
    Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha -and $actual -ne $sha) { throw "SHA256 не совпадает для $url`nожидалось $sha`nполучено  $actual" }
}

try {
    $b = $Engines[$Rid].byedpi
    Get-Checked $b.url "$tmp\byedpi.zip" $b.sha256
    Expand-Archive "$tmp\byedpi.zip" "$tmp\byedpi" -Force
    Copy-Item (Get-ChildItem "$tmp\byedpi" -Recurse -Filter ciadpi.exe | Select-Object -First 1).FullName $dest -Force

    $s = $Engines[$Rid].singbox
    Get-Checked $s.url "$tmp\sing-box.zip" $s.sha256
    Expand-Archive "$tmp\sing-box.zip" "$tmp\sing-box" -Force
    Copy-Item (Get-ChildItem "$tmp\sing-box" -Recurse -Filter sing-box.exe | Select-Object -First 1).FullName $dest -Force

    New-Item -ItemType Directory -Force -Path "$dest\LICENSES" | Out-Null
    Invoke-WebRequest "https://raw.githubusercontent.com/hufrea/byedpi/main/LICENSE" -OutFile "$dest\LICENSES\byedpi-MIT.txt" -UseBasicParsing
    Copy-Item (Get-ChildItem "$tmp\sing-box" -Recurse -Filter LICENSE | Select-Object -First 1).FullName "$dest\LICENSES\sing-box-GPL-3.0.txt" -Force
    @"
byedpi $($Engines.ByedpiVersion) — https://github.com/hufrea/byedpi (MIT)
sing-box $($Engines.SingboxVersion) — https://github.com/SagerNet/sing-box (GPL-3.0-or-later), включает wintun (https://www.wintun.net)
"@ | Set-Content "$dest\LICENSES\README.txt" -Encoding UTF8

    Write-Host "Готово: $dest" -ForegroundColor Green
    Get-ChildItem $dest | Format-Table Name, Length
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
