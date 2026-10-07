[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$PythonEmbedArchive,
    [string]$BuildPython = 'python',
    [string]$RuntimeRoot = 'runtime',
    [string]$WorkerSourceRoot = 'worker\python'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runtimeRootPath = if ([IO.Path]::IsPathRooted($RuntimeRoot)) { [IO.Path]::GetFullPath($RuntimeRoot) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $RuntimeRoot)) }
$workerSourcePath = if ([IO.Path]::IsPathRooted($WorkerSourceRoot)) { [IO.Path]::GetFullPath($WorkerSourceRoot) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $WorkerSourceRoot)) }
$pythonRoot = Join-Path $runtimeRootPath 'python'
$workerRuntimeRoot = Join-Path $runtimeRootPath 'worker'
$buildVenv = Join-Path $projectRoot '.tooling\worker-codegen-venv'

if (Test-Path $pythonRoot) { Remove-Item $pythonRoot -Recurse -Force }
New-Item -ItemType Directory -Path $pythonRoot -Force | Out-Null
Expand-Archive -Path $PythonEmbedArchive -DestinationPath $pythonRoot -Force
$embeddedPython = Join-Path $pythonRoot 'python.exe'
if (-not (Test-Path $embeddedPython)) { throw "python.exe was not found in embed archive: $PythonEmbedArchive" }

$pth = Get-ChildItem $pythonRoot -Filter 'python*._pth' | Select-Object -First 1
if ($null -eq $pth) { throw 'Embedded Python ._pth file was not found.' }
$lines = Get-Content $pth.FullName
$lines = @($lines | Where-Object { $_ -notmatch '^\.\\Lib\\site-packages$' -and $_ -notmatch '^\.\.\\worker$' -and $_ -notmatch '^#?import site$' })
$lines += '.\\Lib\\site-packages'
$lines += '..\\worker'
Set-Content -Path $pth.FullName -Value $lines -Encoding ascii

if (Test-Path $buildVenv) { Remove-Item $buildVenv -Recurse -Force }
& $BuildPython -m venv $buildVenv
$venvPython = Join-Path $buildVenv 'Scripts\python.exe'
& $venvPython -m pip install --disable-pip-version-check --upgrade pip
& $venvPython -m pip install --disable-pip-version-check -r (Join-Path $workerSourcePath 'requirements-runtime.txt')

$sitePackages = Join-Path $pythonRoot 'Lib\site-packages'
New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null
& $venvPython -m pip install --disable-pip-version-check --no-compile --target $sitePackages -r (Join-Path $workerSourcePath 'requirements-runtime.txt')
if ($LASTEXITCODE -ne 0) { throw 'Worker runtime dependency installation failed.' }

if (Test-Path $workerRuntimeRoot) { Remove-Item $workerRuntimeRoot -Recurse -Force }
New-Item -ItemType Directory -Path $workerRuntimeRoot -Force | Out-Null
& $venvPython -m grpc_tools.protoc "-I$workerSourcePath" "--python_out=$workerRuntimeRoot" "--grpc_python_out=$workerRuntimeRoot" (Join-Path $workerSourcePath 'local_media_ai_worker.proto')
if ($LASTEXITCODE -ne 0) { throw 'Worker protobuf generation failed.' }
Copy-Item (Join-Path $workerSourcePath 'worker.py') (Join-Path $workerRuntimeRoot 'local_media_ai_worker.py') -Force

Write-Output "Worker runtime provisioned: $runtimeRootPath"
Write-Output "Python: $embeddedPython"
Write-Output "Worker: $(Join-Path $workerRuntimeRoot 'local_media_ai_worker.py')"
