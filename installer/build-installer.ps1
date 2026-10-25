# Gera o instalador do Whiteboard Simulator em installer\output.
# Uso:  powershell -File installer\build-installer.ps1 [-Version 0.1.0]
# Requer o Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version = "0.1.0")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup" }

# O app vai SEM o .NET dentro (o instalador cuida do runtime): arquivo único, bem menor.
$staging = Join-Path $root "publish-fd"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
dotnet publish "$root\WhiteboardSimulator.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=None -p:Version=$Version -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou" }

& $iscc "/DAppVersion=$Version" "$PSScriptRoot\WhiteboardSimulator.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou" }

Get-ChildItem "$PSScriptRoot\output" -Filter "*.exe" | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
