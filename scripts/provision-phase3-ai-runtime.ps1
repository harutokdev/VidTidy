[CmdletBinding()]
param(
    [string]$BuildPython = 'python',
    [string]$RuntimeRoot = 'runtime',
    [string]$WorkerSourceRoot = 'worker\python',
    [switch]$IncludeModelPreparation,
    [string]$ModelsRoot = 'models',
    [string]$ModelId = 'google/siglip2-base-patch16-224',
    [string]$Revision = '75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
function Resolve-ProjectPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path $projectRoot $path))
}

$runtimeRootPath = Resolve-ProjectPath $RuntimeRoot
$workerSourcePath = Resolve-ProjectPath $WorkerSourceRoot
$modelsRootPath = Resolve-ProjectPath $ModelsRoot
$embeddedPython = Join-Path $runtimeRootPath 'python\python.exe'
$requirements = Join-Path $workerSourcePath 'requirements-ai-runtime.txt'
$prepareScript = Join-Path $workerSourcePath 'prepare_siglip2_model.py'
$capabilityScript = Join-Path $workerSourcePath 'print_runtime_capability.py'
$buildVenv = Join-Path $projectRoot '.tooling\phase3-ai-provision-venv'

if (-not (Test-Path $embeddedPython -PathType Leaf)) { throw "Portable Python was not found: $embeddedPython" }
if (-not (Test-Path $requirements -PathType Leaf)) { throw "AI runtime requirements were not found: $requirements" }
if (-not (Test-Path $prepareScript -PathType Leaf)) { throw "Model preparation script was not found: $prepareScript" }
if (-not (Test-Path $capabilityScript -PathType Leaf)) { throw "Runtime capability script was not found: $capabilityScript" }

# Build venv is tooling only. Runtime never invokes pip or an external package source.
if (Test-Path $buildVenv) { Remove-Item $buildVenv -Recurse -Force }
& $BuildPython -m venv $buildVenv
if ($LASTEXITCODE -ne 0) { throw 'Build Python could not create the isolated provisioning environment.' }
$venvPython = Join-Path $buildVenv 'Scripts\python.exe'
& $venvPython -m pip install --disable-pip-version-check --upgrade pip
& $venvPython -m pip install --disable-pip-version-check huggingface_hub==0.28.1
& $venvPython -m pip install --disable-pip-version-check --no-compile --target (Join-Path $runtimeRootPath 'python\Lib\site-packages') -r $requirements
if ($LASTEXITCODE -ne 0) { throw 'Portable Phase 3 AI runtime dependency installation failed.' }

# Report the package/runtime capability using the embedded interpreter, not build Python.
& $embeddedPython $capabilityScript
if ($LASTEXITCODE -ne 0) { throw 'Portable AI runtime import validation failed.' }

if ($IncludeModelPreparation) {
    New-Item -ItemType Directory -Path $modelsRootPath -Force | Out-Null
    & $venvPython $prepareScript --model-id $ModelId --revision $Revision --models-root $modelsRootPath
    if ($LASTEXITCODE -ne 0) { throw 'Explicit SigLIP 2 model preparation failed.' }
}

Write-Output "Phase 3 AI runtime provisioned: $runtimeRootPath"
Write-Output "Runtime Python: $embeddedPython"
Write-Output "Model preparation executed: $($IncludeModelPreparation.IsPresent)"
