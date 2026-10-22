# Windows 上でビルド→試験→インストーラー作成まで行う。
# 必要: .NET 8 SDK (無償) / Inno Setup 6 (無償) 。ビルド時だけインターネットが必要(NuGet)。完成したインストーラーの利用にはインターネット不要。
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Remove-Item "$root\dist" -Recurse -Force -ErrorAction SilentlyContinue

dotnet test "$root\tests\Kiroku.Tests" -c Release
if ($LASTEXITCODE -ne 0) { throw "試験に失敗しました。インストーラーは作成しません。" }

dotnet publish "$root\src\Kiroku.App"    -c Release -r win-x64 --self-contained -o "$root\dist\app"
dotnet publish "$root\src\Kiroku.Server" -c Release -r win-x64 --self-contained -o "$root\dist\server"

$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 が見つかりません。https://jrsoftware.org/isinfo.php からインストールしてください。" }
& $iscc "$root\installer\Kiroku.iss" "/DAppVersion=$Version"

$out = Get-ChildItem "$root\dist\installer\*.exe" | Select-Object -First 1
Get-FileHash $out.FullName -Algorithm SHA256 | Format-List
Write-Host "完成: $($out.FullName)"
