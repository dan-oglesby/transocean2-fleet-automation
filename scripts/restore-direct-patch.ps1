param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\TransOcean2"
)

$ErrorActionPreference = "Stop"
$Managed = Join-Path $GameRoot "TransOcean2_Data\Managed"
$AssemblyPath = Join-Path $Managed "Assembly-CSharp.dll"
$BackupPath = Join-Path $Managed "Assembly-CSharp.dll.to2fa-original"

$running = Get-Process -Name TransOcean2 -ErrorAction SilentlyContinue
if ($running) {
    throw "TransOcean 2 is running. Close the game before restoring Assembly-CSharp.dll."
}

if (!(Test-Path $BackupPath)) {
    throw "Backup not found: $BackupPath"
}

Copy-Item -LiteralPath $BackupPath -Destination $AssemblyPath -Force
Write-Host "Restored Assembly-CSharp.dll from $BackupPath"
