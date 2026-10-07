param(
    [Parameter(Mandatory = $true)][string]$ApplicationPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-Until {
    param([scriptblock]$Condition, [string]$Description)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { if (& $Condition) { return } } catch { }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}

function Get-ByName {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name))
}

$process = $null
try {
    $app = (Resolve-Path $ApplicationPath).Path
    $process = Start-Process -FilePath $app -WorkingDirectory (Split-Path $app) -PassThru
    Wait-Until -Description 'main window' -Condition { $process.Refresh(); $process.MainWindowHandle -ne 0 -and -not $process.HasExited }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $button = Get-ByName $window 'Python Workerを診断'
    if($null -eq $button){ throw 'Worker diagnostic button was not found.' }
    $invoke = [System.Windows.Automation.InvokePattern]$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Wait-Until -Description 'WORKER_RUNTIME_NOT_FOUND status' -Condition {
        $all=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        @($all | Where-Object { $_.Current.Name -match 'WORKER_RUNTIME_NOT_FOUND' }).Count -gt 0
    }
    $all=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $matched=@($all | Where-Object { $_.Current.Name -match 'WORKER_RUNTIME_NOT_FOUND' } | ForEach-Object { $_.Current.Name })
    $result=[pscustomobject]@{ Passed=($matched.Count -gt 0); ErrorCode='WORKER_RUNTIME_NOT_FOUND'; MatchedAutomationNames=$matched; ProcessExited=$process.HasExited }
    $result | ConvertTo-Json -Depth 5 | Set-Content -Path $OutputPath -Encoding utf8
    $result | ConvertTo-Json -Depth 5
    if(-not $result.Passed){exit 1}
}
finally {
    if($null -ne $process -and -not $process.HasExited){$process.CloseMainWindow()|Out-Null;if(-not($process.WaitForExit(5000))){$process.Kill($true)}}
}
