param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\TransOcean2"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Dll = Join-Path $RepoRoot "dist\TransOcean2FleetAutomationProbe.dll"
$Plugins = Join-Path $GameRoot "BepInEx\plugins"

if (!(Test-Path $Dll)) { throw "Build output not found: $Dll" }
New-Item -ItemType Directory -Path $Plugins -Force | Out-Null
Copy-Item -LiteralPath $Dll -Destination (Join-Path $Plugins (Split-Path -Leaf $Dll)) -Force
Write-Host "Installed plugin to $Plugins"
