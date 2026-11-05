# WindUnifier 构建脚本（UTF-8 with BOM）
# 用法: .\build.ps1            # 只构建
#       .\build.ps1 -Deploy    # 构建并部署到 F: 的 GameData
param(
    [switch]$Deploy,
    [string]$KSP = 'F:\KSP\Kerbal Space Program'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'WindUnifier.csproj'
$managed = Join-Path $KSP 'KSP_x64_Data\Managed'

Write-Host "[build] dotnet build" -ForegroundColor Cyan
& dotnet build $proj -c Release -p:KSPManaged=$managed
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $LASTEXITCODE" }

$dll = Join-Path $here 'bin\Release\WindUnifier.dll'
if (-not (Test-Path $dll)) { throw "output not found: $dll" }
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash
Write-Host ("[build] {0}  {1} bytes  SHA256 {2}" -f $dll, (Get-Item $dll).Length, $hash) -ForegroundColor Green

# Keep the source GameData tree in sync so the release zip always contains the
# DLL that was just built.
$localGd = Join-Path $here 'GameData\WindUnifier'
New-Item -ItemType Directory -Force -Path (Join-Path $localGd 'Plugins') | Out-Null
Copy-Item $dll (Join-Path $localGd 'Plugins\WindUnifier.dll') -Force

if ($Deploy) {
    $dest = Join-Path $KSP 'GameData\WindUnifier'
    New-Item -ItemType Directory -Force -Path (Join-Path $dest 'Plugins') | Out-Null
    Copy-Item $dll (Join-Path $dest 'Plugins\WindUnifier.dll') -Force
    $gd = Join-Path $here 'GameData\WindUnifier'
    Get-ChildItem $gd -File | Copy-Item -Destination $dest -Force
    Write-Host "[deploy] -> $dest" -ForegroundColor Green
}