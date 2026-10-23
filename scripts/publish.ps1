<#
  Собирает портативный релиз: dist\ByeDPI-PC-<версия>-<rid>.zip (программа + engine\ с движками и лицензиями).
  Запуск: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-Rid win-x64]
#>
param([string]$Rid = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not (Test-Path "engine\$Rid\ciadpi.exe")) {
    & "$PSScriptRoot\fetch-engines.ps1" -Rid $Rid
}

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$out = Join-Path $root "dist\ByeDPI-PC-$version-$Rid"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish src\ByeDpiPc.App -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой" }

Copy-Item LICENSE, README.md $out
$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Готово: $zip" -ForegroundColor Green
