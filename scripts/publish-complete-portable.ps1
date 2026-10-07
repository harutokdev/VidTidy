[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\portable\LocalMediaAI')
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appProject = Join-Path $projectRoot 'src\LocalMediaAI.App\LocalMediaAI.App.csproj'
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { [IO.Path]::GetFullPath($Output) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $Output)) }
$allowedOutputRoots = @([IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')), [IO.Path]::GetFullPath((Join-Path $projectRoot '.manus-dev')))
if (-not ($allowedOutputRoots | Where-Object { $outputPath.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })) {
    throw "Portable output must be a test-owned child of artifacts or .manus-dev: $outputPath"
}

function New-FileRecord([string]$Category, [string]$SourcePath, [string]$RelativePath) {
    $source = [IO.Path]::GetFullPath($SourcePath)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required portable payload file is missing: $source" }
    $item = Get-Item -LiteralPath $source -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Portable payload file cannot be a reparse point: $source" }
    [pscustomobject]@{ category=$Category; source_path=$source; relative_path=$RelativePath.Replace('\', '/'); size=$item.Length; sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
}

function Add-ExplicitTree([Collections.Generic.List[object]]$Records, [string]$Category, [string]$SourceRoot, [string]$DestinationPrefix) {
    $root = [IO.Path]::GetFullPath($SourceRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Required portable payload root is missing: $root" }
    if (((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Portable payload root cannot be a reparse point: $root" }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Portable payload tree contains a reparse point: $($file.FullName)" }
        $Records.Add((New-FileRecord $Category $file.FullName (Join-Path $DestinationPrefix ([IO.Path]::GetRelativePath($root, $file.FullName)))))
    }
}

function Add-ProvenanceBundle([Collections.Generic.List[object]]$Records, [string]$SourceRoot, [string]$DestinationPrefix) {
    $root = [IO.Path]::GetFullPath($SourceRoot)
    $provenancePath = Join-Path $root 'provenance.json'
    if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) { throw "Required AI provenance is missing: $provenancePath" }
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    foreach ($entry in $provenance.files) {
        $relative = [string]$entry.path
        if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)') { throw "Unsafe AI manifest path: $relative" }
        $record = New-FileRecord 'ai-model-resource' (Join-Path $root $relative) (Join-Path $DestinationPrefix $relative)
        if ($record.size -ne [long]$entry.bytes -or $record.sha256 -ne ([string]$entry.sha256).ToUpperInvariant()) { throw "AI resource does not match provenance: $relative" }
        $Records.Add($record)
    }
    $Records.Add((New-FileRecord 'ai-model-resource' $provenancePath (Join-Path $DestinationPrefix 'provenance.json')))
}

$payloadRecords = [Collections.Generic.List[object]]::new()
Add-ExplicitTree $payloadRecords 'worker-runtime' (Join-Path $projectRoot 'runtime\python-openclip') 'runtime\python-openclip'
foreach ($relative in @('local_media_ai_worker.py','local_media_ai_worker_pb2.py','local_media_ai_worker_pb2_grpc.py')) {
    $payloadRecords.Add((New-FileRecord 'worker-runtime' (Join-Path $projectRoot "runtime\worker\$relative") "runtime\worker\$relative"))
}
foreach ($relative in @('ffmpeg.exe','ffprobe.exe')) {
    $payloadRecords.Add((New-FileRecord 'worker-runtime' (Join-Path $projectRoot "runtime\ffmpeg\$relative") "runtime\ffmpeg\$relative"))
}
Add-ProvenanceBundle $payloadRecords (Join-Path $projectRoot 'models\llm-jp-clip-vit-base-patch16') 'models\llm-jp-clip-vit-base-patch16'
Add-ProvenanceBundle $payloadRecords (Join-Path $projectRoot 'models\llm-jp-clip-vit-base-patch16-openclip-adapter') 'models\llm-jp-clip-vit-base-patch16-openclip-adapter'
if ($payloadRecords | Group-Object relative_path | Where-Object Count -ne 1) { throw 'Portable payload contains duplicate destination paths.' }

if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$workspaceDotNet = Join-Path $projectRoot '.tooling\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $workspaceDotNet -PathType Leaf) { $workspaceDotNet } else { 'dotnet' }
& $dotnet publish $appProject --configuration $Configuration --runtime $RuntimeIdentifier --self-contained true --no-restore --output $outputPath /p:PublishSingleFile=false /p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "Portable application publish failed with exit code $LASTEXITCODE" }

foreach ($directory in @('runtime','models','data','cache','temp')) { New-Item -ItemType Directory -Force -Path (Join-Path $outputPath $directory) | Out-Null }
foreach ($record in $payloadRecords) {
    $destination = Join-Path $outputPath $record.relative_path
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $record.source_path -Destination $destination
    $copied = Get-Item -LiteralPath $destination
    if ($copied.Length -ne $record.size -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $record.sha256) { throw "Portable payload copy verification failed: $($record.relative_path)" }
}

$portableFlag = Join-Path $outputPath 'portable.flag'
[IO.File]::WriteAllBytes($portableFlag, [byte[]]::new(0))
$payloadRecords.Add((New-FileRecord 'portable-configuration' $portableFlag 'portable.flag'))
$applicationRecords = Get-ChildItem -LiteralPath $outputPath -File -Recurse -Force |
    Where-Object { $_.FullName -notlike (Join-Path $outputPath 'runtime\*') -and $_.FullName -notlike (Join-Path $outputPath 'models\*') -and $_.Name -ne 'portable.flag' } |
    Sort-Object FullName | ForEach-Object { New-FileRecord 'application-binary' $_.FullName ([IO.Path]::GetRelativePath($outputPath, $_.FullName)) }
$allRecords = @($applicationRecords) + @($payloadRecords | Sort-Object relative_path)
$manifest = [ordered]@{
    schema_version=1; purpose='Complete Local Media AI portable candidate'; generated_at_utc=[DateTimeOffset]::UtcNow.ToString('o'); candidate_root=$outputPath
    portable_flag_classification='required application-owned portable configuration; selects app-local data/cache/temp used by AI sampling and durable state'
    files=$allRecords | Select-Object category,relative_path,size,sha256
    category_counts=$allRecords | Group-Object category | ForEach-Object { [pscustomobject]@{ category=$_.Name; files=$_.Count; bytes=($_.Group | Measure-Object size -Sum).Sum } }
}
$manifestPath = Join-Path $outputPath 'complete-portable-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Output "Portable package completed: $outputPath"
Write-Output "Complete manifest: $manifestPath"
