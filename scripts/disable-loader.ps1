param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\TransOcean2"
)

$ErrorActionPreference = "Stop"
$Config = Join-Path $GameRoot "doorstop_config.ini"

if (!(Test-Path $Config)) { throw "Doorstop config not found: $Config" }

(Get-Content -LiteralPath $Config) |
    ForEach-Object {
        if ($_ -match "^\s*enabled\s*=") { "enabled = false" } else { $_ }
    } |
    Set-Content -LiteralPath $Config -Encoding ASCII

Write-Host "Disabled BepInEx/Doorstop in $Config"
