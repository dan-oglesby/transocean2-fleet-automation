param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\TransOcean2"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$OutDir = Join-Path $RepoRoot "dist"
$OutDll = Join-Path $OutDir "TransOcean2FleetAutomation.Direct.dll"
$Source = Join-Path $RepoRoot "src\TransOcean2FleetAutomationDirect.cs"
$Csc = "C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe"
$Managed = Join-Path $GameRoot "TransOcean2_Data\Managed"

if (!(Test-Path $Csc)) { throw "C# 3.5 compiler not found: $Csc" }
if (!(Test-Path (Join-Path $Managed "UnityEngine.dll"))) { throw "UnityEngine.dll not found under $Managed" }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

& $Csc /nologo /target:library /optimize+ /out:$OutDll `
    /reference:"$Managed\UnityEngine.dll" `
    $Source

if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "Built $OutDll"
