param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\TransOcean2"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Managed = Join-Path $GameRoot "TransOcean2_Data\Managed"
$BuildScript = Join-Path $PSScriptRoot "build-direct.ps1"
$DistDll = Join-Path $RepoRoot "dist\TransOcean2FleetAutomation.Direct.dll"
$GameDll = Join-Path $Managed "TransOcean2FleetAutomation.Direct.dll"
$AssemblyPath = Join-Path $Managed "Assembly-CSharp.dll"
$BackupPath = Join-Path $Managed "Assembly-CSharp.dll.to2fa-original"
$CecilPath = Join-Path $GameRoot "BepInEx\core\Mono.Cecil.dll"

$running = Get-Process -Name TransOcean2 -ErrorAction SilentlyContinue
if ($running) {
    throw "TransOcean 2 is running. Close the game before patching Assembly-CSharp.dll."
}

if (!(Test-Path $CecilPath)) {
    throw "Mono.Cecil.dll not found at $CecilPath. The direct patcher currently reuses BepInEx's bundled Mono.Cecil at install time only."
}

& $BuildScript -GameRoot $GameRoot
Copy-Item -LiteralPath $DistDll -Destination $GameDll -Force

if (!(Test-Path $BackupPath)) {
    Copy-Item -LiteralPath $AssemblyPath -Destination $BackupPath
}

Add-Type -Path $CecilPath
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($Managed)
$reader = New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver = $resolver

$module = $null
$modModule = $null
$patchedTemp = $null

try {
    $module = [Mono.Cecil.ModuleDefinition]::ReadModule($AssemblyPath, $reader)
    $modModule = [Mono.Cecil.ModuleDefinition]::ReadModule($GameDll, $reader)
    $targetType = $module.GetType("Cargo.InitGameStart")
    if ($null -eq $targetType) { throw "Cargo.InitGameStart not found" }

    $awake = $targetType.Methods | Where-Object { $_.Name -eq "Awake" } | Select-Object -First 1
    if ($null -eq $awake) { throw "Cargo.InitGameStart.Awake not found" }

    $alreadyPatched = $false
    foreach ($instruction in $awake.Body.Instructions) {
        if ($instruction.OpCode.Code.ToString() -eq "Call" -and
            $instruction.Operand -ne $null -and
            $instruction.Operand.FullName -eq "System.Void TransOcean2FleetAutomation.Direct.Loader::Bootstrap()") {
            $alreadyPatched = $true
        }
    }

    if (!$alreadyPatched) {
        $loaderType = $modModule.GetType("TransOcean2FleetAutomation.Direct.Loader")
        if ($null -eq $loaderType) { throw "Loader type not found in direct mod DLL" }

        $bootstrap = $loaderType.Methods | Where-Object { $_.Name -eq "Bootstrap" } | Select-Object -First 1
        if ($null -eq $bootstrap) { throw "Bootstrap method not found in direct mod DLL" }

        $importedBootstrap = $module.ImportReference($bootstrap)
        $processor = $awake.Body.GetILProcessor()
        $ret = $awake.Body.Instructions | Where-Object { $_.OpCode.Code.ToString() -eq "Ret" } | Select-Object -Last 1
        if ($null -eq $ret) { throw "Could not find ret in Cargo.InitGameStart.Awake" }

        $processor.InsertBefore($ret, $processor.Create([Mono.Cecil.Cil.OpCodes]::Call, $importedBootstrap))
        $patchedTemp = Join-Path $Managed ("Assembly-CSharp.dll.to2fa.patched." + [DateTime]::Now.ToString("yyyyMMdd-HHmmss") + ".tmp")
        $module.Write($patchedTemp)
    }
}
finally {
    if ($module -ne $null) { $module.Dispose() }
    if ($modModule -ne $null) { $modModule.Dispose() }
}

if ($patchedTemp -ne $null) {
    [System.IO.File]::Copy($patchedTemp, $AssemblyPath, $true)
    Write-Host "Patched Cargo.InitGameStart.Awake -> TransOcean2FleetAutomation.Direct.Loader.Bootstrap"
    Write-Host "Patched temp kept for inspection: $patchedTemp"
}
else {
    Write-Host "Assembly-CSharp.dll already contains the direct mod bootstrap call"
}

Write-Host "Backup: $BackupPath"
Write-Host "Runtime DLL: $GameDll"
