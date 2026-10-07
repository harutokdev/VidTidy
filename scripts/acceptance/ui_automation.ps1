param(
    [Parameter(Mandatory = $true)][string]$ApplicationPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$ExpectedSelectedMediaFileId,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$results = [System.Collections.Generic.List[object]]::new()
$process = $null
$script:searchLatencyMilliseconds = $null
$script:searchClearLatencyMilliseconds = $null

function Wait-Until {
    param([scriptblock]$Condition, [string]$Description, [int]$Seconds = $TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { if (& $Condition) { return $true } } catch { }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}

function Get-ElementByName {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name))
}

function Get-ElementByAutomationId {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$AutomationId)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId))
}

function Get-DataItem {
    param([System.Windows.Automation.AutomationElement]$Grid, [string]$NameFragment)
    $elements = $Grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem -and $element.Current.Name -like "*$NameFragment*") { return $element }
    }
    return $null
}

function Invoke-UiElement {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Select-UiDataItem {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
}

function Set-UiValue {
    param([System.Windows.Automation.AutomationElement]$Element, [string]$Value)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}

function Add-Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $true; Detail = 'PASS' })
    }
    catch {
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Detail = $_.Exception.Message })
    }
}

try {
    $app = (Resolve-Path $ApplicationPath).Path
    $started = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $app -WorkingDirectory (Split-Path $app) -PassThru
    Wait-Until -Description 'Local Media AI main window' -Condition {
        $process.Refresh()
        $process.MainWindowHandle -ne 0 -and -not $process.HasExited
    }
    $windowReadyMs = [Math]::Round($started.Elapsed.TotalMilliseconds, 3)
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    Wait-Until -Description 'media grid' -Condition { (Get-ElementByName $window 'メディア一覧') -ne $null }
    $grid = Get-ElementByName $window 'メディア一覧'
    $search = Get-ElementByName $window 'メディア検索'
    $next = Get-ElementByName $window '次のページ'
    $previous = Get-ElementByName $window '前のページ'
    $refresh = Get-ElementByName $window '一覧とジョブを更新'
    $details = Get-ElementByName $window '選択メディアの詳細'
    $jobs = Get-ElementByName $window '最近のジョブとエラー'
    $workerButton = Get-ElementByName $window 'Python Workerを診断'
    $pageStatus = Get-ElementByAutomationId $window 'PageStatusText'

    Add-Check 'MainWindow and primary accessible controls' {
        if ($window.Current.Name -ne 'Local Media AI メインウィンドウ') { throw "Unexpected window name: $($window.Current.Name)" }
        foreach ($required in @($grid, $search, $next, $previous, $refresh, $details, $jobs, $workerButton, $pageStatus)) { if ($null -eq $required) { throw 'A required named control was not found.' } }
    }

    Add-Check '10k initial page and virtualized first-row availability' {
        Wait-Until -Description 'initial first row' -Condition { (Get-DataItem $grid 'row-00000.mp4') -ne $null }
        if ($pageStatus.Current.Name -notmatch '10,000件') { throw "Unexpected page status: $($pageStatus.Current.Name)" }
        $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem }
        if (@($rows).Count -gt 100) { throw "Visible DataItem count exceeded page size: $(@($rows).Count)" }
    }

    Add-Check 'Thumbnail and failure placeholder affordances' {
        Wait-Until -Description 'generated thumbnail' -Condition { (Get-ElementByName $window '生成済みサムネイル') -ne $null }
        Wait-Until -Description 'failure placeholder' -Condition { (Get-ElementByName $window 'サムネイル未生成または生成失敗のplaceholder') -ne $null }
        $failed = Get-DataItem $grid 'row-00001.mp4'
        if ($null -eq $failed) { throw 'Failure fixture row was not visible.' }
        Select-UiDataItem $failed
        Wait-Until -Description 'failed row details' -Condition { (Get-ElementByName $window 'TEST_THUMBNAIL_FAILED') -ne $null }
    }

    Add-Check 'Row selection and stable MediaFileId details' {
        $first = Get-DataItem $grid 'row-00000.mp4'
        if ($null -eq $first) { throw 'First fixture row is not visible.' }
        Select-UiDataItem $first
        Wait-Until -Description 'selected MediaFileId detail' -Condition { (Get-ElementByName $window $ExpectedSelectedMediaFileId) -ne $null }
    }

    Add-Check 'Refresh preserves visible MediaFileId selection' {
        Invoke-UiElement $refresh
        Wait-Until -Description 'selection after refresh' -Condition { (Get-ElementByName $window $ExpectedSelectedMediaFileId) -ne $null }
    }

    Add-Check 'DataGrid scroll operation' {
        $scrollPattern = [System.Windows.Automation.ScrollPattern]$grid.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $scrollPattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
        Start-Sleep -Milliseconds 300
        $scrollPattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    }

    Add-Check 'Paging clears selection rather than retargeting it by row index' {
        Invoke-UiElement $next
        Wait-Until -Description 'second page first row' -Condition { (Get-DataItem $grid 'row-00100.mp4') -ne $null }
        if ((Get-ElementByName $window $ExpectedSelectedMediaFileId) -ne $null) { throw 'Selection persisted across a page change.' }
        Invoke-UiElement $previous
        Wait-Until -Description 'first page restored' -Condition { (Get-DataItem $grid 'row-00000.mp4') -ne $null }
    }

    Add-Check 'Search change clears selection and search-clear does not stale-retarget MediaFileId' {
        $first = Get-DataItem $grid 'row-00000.mp4'
        if ($null -eq $first) { throw 'First fixture row is not visible before search.' }
        Select-UiDataItem $first
        Wait-Until -Description 'selection before search' -Condition { (Get-ElementByName $window $ExpectedSelectedMediaFileId) -ne $null }
        $searchStopwatch = [Diagnostics.Stopwatch]::StartNew()
        Set-UiValue $search 'row-00999.mp4'
        Wait-Until -Description 'search result row-00999' -Condition { (Get-DataItem $grid 'row-00999.mp4') -ne $null }
        $script:searchLatencyMilliseconds = [Math]::Round($searchStopwatch.Elapsed.TotalMilliseconds, 3)
        if ((Get-ElementByName $window $ExpectedSelectedMediaFileId) -ne $null) { throw 'Selection was not cleared after the filter changed.' }
        $clearStopwatch = [Diagnostics.Stopwatch]::StartNew()
        Set-UiValue $search ''
        Wait-Until -Description 'search cleared to first row' -Condition { (Get-DataItem $grid 'row-00000.mp4') -ne $null }
        $script:searchClearLatencyMilliseconds = [Math]::Round($clearStopwatch.Elapsed.TotalMilliseconds, 3)
        if ((Get-ElementByName $window $ExpectedSelectedMediaFileId) -ne $null) { throw 'Search-clear stale-retargeted a previously selected MediaFileId.' }
    }

    Add-Check 'Keyboard navigation and focus order' {
        $search.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
        Wait-Until -Description 'Tab focus after search' -Condition { -not [string]::IsNullOrWhiteSpace([System.Windows.Automation.AutomationElement]::FocusedElement.Current.Name) }
        [System.Windows.Forms.SendKeys]::SendWait('+{TAB}')
        Wait-Until -Description 'Shift+Tab focus returns to search' -Condition { [System.Windows.Automation.AutomationElement]::FocusedElement.Current.Name -eq 'メディア検索' }
        $grid.SetFocus()
        Wait-Until -Description 'DataGrid keyboard focus' -Condition { [System.Windows.Automation.AutomationElement]::FocusedElement.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataGrid }
        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}{DOWN}{UP}{RIGHT}{LEFT}{ENTER}{ESC}')
        Start-Sleep -Milliseconds 300
        $focusName = [System.Windows.Automation.AutomationElement]::FocusedElement.Current.Name
        if ([string]::IsNullOrWhiteSpace($focusName)) { throw 'Keyboard focus was lost after navigation keys.' }
    }

    Add-Check 'Jobs Errors and Worker status regions are discoverable' {
        if ($jobs.Current.Name -ne '最近のジョブとエラー') { throw 'Jobs/Errors list name missing.' }
        $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        if ((@($all | Where-Object { $_.Current.Name -match '^Worker:' })).Count -eq 0) { throw 'Worker status value missing.' }
        if ((@($all | Where-Object { $_.Current.Name -match 'TEST_THUMBNAIL_FAILED' })).Count -eq 0) { throw 'Failure code not available to UI Automation.' }
    }

    $process.Refresh()
    $summary = [pscustomobject]@{
        process_id = $process.Id
        app_window_ready_milliseconds = $windowReadyMs
        app_working_set_bytes = $process.WorkingSet64
        app_private_memory_bytes = $process.PrivateMemorySize64
        ui_search_milliseconds = $script:searchLatencyMilliseconds
        ui_search_clear_milliseconds = $script:searchClearLatencyMilliseconds
        passed_checks = (@($results | Where-Object { $_.Passed })).Count
        failed_checks = (@($results | Where-Object { -not $_.Passed })).Count
        checks = @($results)
        narrator_executed = $false
        narrator_reason = "Narrator is evaluated separately to avoid treating a UIA tree as spoken-output verification."
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputPath -Encoding utf8
    $summary | ConvertTo-Json -Depth 6
    if ($summary.failed_checks -gt 0) { exit 1 }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not ($process.WaitForExit(5000))) { $process.Kill($true) }
    }
}
