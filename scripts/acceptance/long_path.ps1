param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [int]$MinimumPathLength = 280
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ("LocalMediaAI-Acceptance-LongPath-" + [Guid]::NewGuid().ToString('N'))
$result = [ordered]@{
    os = [Environment]::OSVersion.VersionString
    long_paths_enabled = $null
    application_manifest_long_path_aware = $true
    target_length = $MinimumPathLength
    created_path_length = $null
    create_succeeded = $false
    read_succeeded = $false
    hash_succeeded = $false
    blocked_by = $null
    error_type = $null
    error_message = $null
    cleanup_succeeded = $false
}
try {
    $value = Get-ItemPropertyValue -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction Stop
    $result.long_paths_enabled = ([int]$value -eq 1)
    $path = $root
    $segment = 'acceptance-long-path-segment-0123456789'
    while($path.Length + 1 + $segment.Length + 16 -le $MinimumPathLength){
        $path = Join-Path $path $segment
    }
    $path = Join-Path $path ('media-' + ('x' * 24) + '.mp4')
    $result.created_path_length = $path.Length
    try {
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        [IO.File]::WriteAllBytes($path, [byte[]](1,2,3,4,5,6,7,8))
        $result.create_succeeded = $true
        $bytes = [IO.File]::ReadAllBytes($path)
        $result.read_succeeded = ($bytes.Length -eq 8)
        $result.hash_succeeded = -not [string]::IsNullOrWhiteSpace((Get-FileHash -Algorithm SHA256 $path).Hash)
    }
    catch {
        $result.blocked_by = if($result.long_paths_enabled -eq $false){'LongPathsEnabled=0'}else{'Filesystem/API failure'}
        $result.error_type = $_.Exception.GetType().FullName
        $result.error_message = $_.Exception.Message
    }
}
finally {
    try { if(Test-Path $root){Remove-Item $root -Recurse -Force}; $result.cleanup_succeeded = -not (Test-Path $root) } catch { $result.cleanup_succeeded = $false }
}
[pscustomobject]$result | ConvertTo-Json -Depth 5 | Set-Content -Path $OutputPath -Encoding utf8
[pscustomobject]$result | ConvertTo-Json -Depth 5
if(-not $result.create_succeeded -and $result.long_paths_enabled -eq $true){exit 1}
