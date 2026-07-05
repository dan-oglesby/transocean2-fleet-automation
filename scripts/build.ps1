param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\TransOcean2"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$OutDir = Join-Path $RepoRoot "dist"
$OutDll = Join-Path $OutDir "TransOcean2FleetAutomationProbe.dll"
$Source = Join-Path $RepoRoot "src\FleetAutomationProbePlugin.cs"
$Csc = "C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe"
$Managed = Join-Path $GameRoot "TransOcean2_Data\Managed"
$BepInExCore = Join-Path $GameRoot "BepInEx\core"

if (!(Test-Path $Csc)) { throw "C# 3.5 compiler not found: $Csc" }
if (!(Test-Path (Join-Path $Managed "Assembly-CSharp.dll"))) { throw "Game assembly not found under $Managed" }
if (!(Test-Path (Join-Path $BepInExCore "BepInEx.dll"))) { throw "BepInEx.dll not found. Install BepInEx first, then rerun build." }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

& $Csc /nologo /target:library /optimize+ /out:$OutDll `
    /reference:"$BepInExCore\BepInEx.dll" `
    /reference:"$BepInExCore\0Harmony.dll" `
    /reference:"$Managed\UnityEngine.dll" `
    $Source

if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "Built $OutDll"
