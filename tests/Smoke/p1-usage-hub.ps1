$ErrorActionPreference = 'Stop'

$exePath = $env:STATUSBAR_EXE
if ([string]::IsNullOrWhiteSpace($exePath)) {
    $exePath = Join-Path $PSScriptRoot '..\..\dist\SubscriptionStatus.exe'
}
$exePath = (Resolve-Path -LiteralPath $exePath).Path
$assembly = [Reflection.Assembly]::LoadFrom($exePath)

$usageType = $assembly.GetType('UsageSnapshot')
$providerType = $assembly.GetType('IUsageProvider')
if ($null -eq $usageType -or $null -eq $providerType) { throw 'Usage model/provider boundary is missing' }
foreach ($name in @('ProviderId', 'PlanName', 'Status', 'IsStale', 'LastLiveAt', 'ErrorCode')) {
    if ($null -eq $usageType.GetProperty($name)) { throw "UsageSnapshot property missing: $name" }
}
foreach ($name in @('ProviderId', 'GetUsageAsync', 'GetCredentialDiagnostic', 'GetNetworkDiagnostic')) {
    if ($null -eq $providerType.GetProperty($name) -and $null -eq $providerType.GetMethod($name)) {
        throw "IUsageProvider member missing: $name"
    }
}

$providerImplementation = $assembly.GetType('OfficialUsageProvider')
if ($null -eq $providerImplementation) { throw 'OfficialUsageProvider type is missing' }
if (-not $providerType.IsAssignableFrom($providerImplementation)) { throw 'OfficialUsageProvider does not implement IUsageProvider' }
$settingsType = $assembly.GetType('AppSettings')
$themeType = $assembly.GetType('ThemeMode')
$settingsProbe = [Activator]::CreateInstance($settingsType)
$settingsProbe.Theme = [Enum]::ToObject($themeType, 99)
$settingsProbe.HistoryRetentionDays = 999
$settingsProbe.LaunchDelaySeconds = 999
$settingsProbe.Normalize()
if ($settingsProbe.Theme.ToString() -ne 'System') { throw 'invalid theme was not normalized' }
if ($settingsProbe.HistoryRetentionDays -ne 30) { throw 'invalid history retention was not normalized' }
if ($settingsProbe.LaunchDelaySeconds -ne 0) { throw 'invalid launch delay was not normalized' }
foreach ($name in @('HistoryRetentionDays', 'LaunchDelaySeconds', 'AutoCheckUpdates', 'AnimationsEnabled')) {
    if ($null -eq $settingsType.GetProperty($name)) { throw "AppSettings property missing: $name" }
}
if (-not $settingsProbe.AnimationsEnabled) { throw 'animations should be enabled by default' }
$settingsProbe.AnimationsEnabled = $false
$settingsClone = $settingsProbe.Clone()
if ($settingsClone.AnimationsEnabled) { throw 'animations setting was not cloned' }
$settingsProbe.AnimationsEnabled = $true
$paletteType = $assembly.GetType('ThemePalette')
if ($null -eq $paletteType.GetMethod('Create')) { throw 'ThemePalette factory is missing' }

$statusType = $assembly.GetType('UsageStatus')
foreach ($name in @('Loading', 'Live', 'Cached', 'OAuthExpired', 'NetworkError', 'ApiError', 'ParseError')) {
    if ($null -eq [Enum]::Parse($statusType, $name, $false)) { throw "UsageStatus value missing: $name" }
}
$windowType = $assembly.GetType('UsageWindow')
$windowCtor = $windowType.GetConstructor([Type[]]@([int], [double], [Nullable[DateTimeOffset]]))
$window = $windowCtor.Invoke([object[]]@(18000, 150.0, $null))
if ($window.UsedPercent -ne 100.0 -or $window.LimitWindowSeconds -ne 18000 -or $window.DisplayName -notmatch '5') { throw 'UsageWindow normalization failed' }
$nonFiniteWindow = $windowCtor.Invoke([object[]]@(18000, [double]::NaN, $null))
if ([double]::IsNaN($nonFiniteWindow.UsedPercent) -or [double]::IsInfinity($nonFiniteWindow.UsedPercent)) { throw 'UsageWindow accepted a non-finite percentage' }
$sanitizerType = $assembly.GetType('DiagnosticSanitizer')
$planMethod = $sanitizerType.GetMethod('PlanName')
if ($planMethod.Invoke($null, @('plus')) -cne 'GPT Plus') { throw 'short plus plan code was not normalized' }
if ($planMethod.Invoke($null, @('gpt_enterprise')) -cne 'GPT Enterprise') { throw 'enterprise plan code was not normalized' }
if ($planMethod.Invoke($null, @('GPT Pro')) -cne 'GPT Pro') { throw 'GPT Pro plan was incorrectly collapsed' }
if ($planMethod.Invoke($null, @('GPT Team')) -cne 'GPT Team') { throw 'GPT Team plan was incorrectly collapsed' }
if ($planMethod.Invoke($null, @('untrusted plan text')) -cne 'ChatGPT') { throw 'unknown plan text was not sanitized' }

$quotaServiceType = $assembly.GetType('OfficialQuotaService')
$quotaService = [Activator]::CreateInstance($quotaServiceType)
$resolvePlanMethod = $quotaServiceType.GetMethod('ResolvePlanName', [Reflection.BindingFlags]'NonPublic,Instance')
if ($null -eq $resolvePlanMethod) { throw 'OAuth plan resolver is missing' }
$authClaims = New-Object 'System.Collections.Generic.Dictionary[string,object]'
$authClaims['chatgpt_plan_type'] = 'plus'
$jwtClaims = New-Object 'System.Collections.Generic.Dictionary[string,object]'
$jwtClaims['https://api.openai.com/auth'] = $authClaims
$payloadJson = $jwtClaims | ConvertTo-Json -Compress -Depth 5
$payloadBytes = [Text.Encoding]::UTF8.GetBytes($payloadJson)
$payload = [Convert]::ToBase64String($payloadBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$tokenMap = New-Object 'System.Collections.Generic.Dictionary[string,object]'
$tokenMap['id_token'] = 'header.' + $payload + '.signature'
$tokenMap = $tokenMap.PSObject.BaseObject
$resolvedPlan = [string]$resolvePlanMethod.Invoke($quotaService, [object[]]@($tokenMap))
if ($resolvedPlan -cne 'GPT Plus') { throw "nested OAuth plan claim was not resolved: $resolvedPlan" }
$quotaService.Dispose()

$listType = [System.Collections.Generic.List``1].MakeGenericType($windowType)
$windows = [Activator]::CreateInstance($listType)
$windows.Add($window)
$live = $usageType.GetMethod('LiveResult').Invoke($null, [object[]]@('chatgpt-codex', 'GPT Plus', $windows, [DateTimeOffset]::Now))
if ($live.PlanName -cne 'GPT Plus') { throw 'live snapshot did not preserve the canonical plan name' }
$withFailure = $live.WithFailure([Enum]::Parse($statusType, 'NetworkError', $false), 'network_unavailable', [DateTimeOffset]::Now)
$cachedSnapshot = $withFailure.ToQuotaSnapshot()
if (-not $cachedSnapshot.Success -or -not $cachedSnapshot.IsStale -or $cachedSnapshot.Windows.Count -ne 1) { throw 'Failure should preserve the last live windows as cached data' }

$cacheType = $assembly.GetType('UsageCache')
$cacheCtor = $cacheType.GetConstructor([Reflection.BindingFlags]'NonPublic,Instance', $null, [Type[]]@([string]), $null)
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('chatgpt-codex-usage-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$cachePath = Join-Path $tempRoot 'cache.json'
$cache = $cacheCtor.Invoke([object[]]@([string]$cachePath))
$cache.Save($live)
$loadedCache = $cache.Load()
if ($null -eq $loadedCache -or $loadedCache.Status.ToString() -ne 'Cached' -or $loadedCache.Windows.Count -ne 1) { throw 'UsageCache round trip failed' }
$cacheJson = [IO.File]::ReadAllText($cachePath)
if ($cacheJson -match 'access_token|refresh_token|Bearer|account_id') { throw 'UsageCache contains a sensitive field' }
[IO.File]::WriteAllText($cachePath, '{broken json', [Text.Encoding]::UTF8)
$null = $cache.Load()
if (-not (Test-Path ($cachePath + '.bak'))) { throw 'Corrupt cache was not backed up' }
$cache.Clear()
if (Test-Path $cachePath) { throw 'UsageCache clear did not remove cache' }

$historyType = $assembly.GetType('HistoryStore')
$historyCtor = $historyType.GetConstructor([Reflection.BindingFlags]'NonPublic,Instance', $null, [Type[]]@([string]), $null)
$historyPath = Join-Path $tempRoot 'history.json'
$history = $historyCtor.Invoke([object[]]@([string]$historyPath))
$history.Append($live)
$history.Append($live)
$historyPoints = $history.Load()
if ($historyPoints.Count -ne 1) { throw 'HistoryStore did not deduplicate identical observations' }
$historyJson = [IO.File]::ReadAllText($historyPath)
if ($historyJson -match 'access_token|refresh_token|Bearer|account_id') { throw 'HistoryStore contains a sensitive field' }
$exportMethod = $historyType.GetMethod('ExportCsv')
if ($null -eq $exportMethod) { throw 'HistoryStore CSV export method is missing' }
$exportPath = [string]$exportMethod.Invoke($history, $null)
if ([string]::IsNullOrWhiteSpace($exportPath) -or -not (Test-Path -LiteralPath $exportPath)) { throw 'HistoryStore CSV export did not create a file' }
$exportText = [IO.File]::ReadAllText($exportPath)
if ($exportText -notmatch '^window_seconds,used_percent,reset_at,observed_at') { throw 'HistoryStore CSV header is invalid' }
if ($exportText -match 'access_token|refresh_token|Bearer|account_id|127\.0\.0\.1') { throw 'HistoryStore CSV contains a sensitive field' }
$historyRetentionCtor = $historyType.GetConstructor([Reflection.BindingFlags]'NonPublic,Instance', $null, [Type[]]@([string], [int]), $null)
if ($null -eq $historyRetentionCtor) { throw 'HistoryStore retention constructor is missing' }
$shortHistoryPath = Join-Path $tempRoot 'history-short.json'
$shortHistory = $historyRetentionCtor.Invoke([object[]]@([string]$shortHistoryPath, [int]7))
if ($shortHistory.RetentionDays -ne 7) { throw 'HistoryStore did not keep the configured retention period' }
$oldLive = $usageType.GetMethod('LiveResult').Invoke($null, [object[]]@('chatgpt-codex', 'GPT Plus', $windows, [DateTimeOffset]::Now.AddDays(-8)))
$shortHistory.Append($oldLive)
if ($shortHistory.Load().Count -ne 0) { throw 'HistoryStore did not trim points outside the retention period' }
$longHistoryPath = Join-Path $tempRoot 'history-existing.json'
$longHistory = $historyCtor.Invoke([object[]]@([string]$longHistoryPath))
$longHistory.Append($oldLive)
$shortExisting = $historyRetentionCtor.Invoke([object[]]@([string]$longHistoryPath, [int]7))
if ([IO.File]::ReadAllText($longHistoryPath) -notmatch '"Points"\s*:\s*\[\s*\]') { throw 'HistoryStore did not trim existing files during construction' }
if ($shortExisting.Load().Count -ne 0) { throw 'HistoryStore did not keep trimmed files empty on load' }
$shortHistory.SetRetentionDays(999)
if ($shortHistory.RetentionDays -ne 30) { throw 'HistoryStore did not normalize an invalid retention period' }
$history.Clear()
if (Test-Path -LiteralPath $exportPath) { throw 'HistoryStore clear did not remove CSV exports' }
$shortHistory.Clear()
Remove-Item -LiteralPath $tempRoot -Recurse -Force

$detailType = $assembly.GetType('UsageDetailsForm')
$historyPointType = $assembly.GetType('HistoryPoint')
$historyListType = [System.Collections.Generic.List``1].MakeGenericType($historyPointType)
$historyList = [Activator]::CreateInstance($historyListType)
$insightsType = $assembly.GetType('UsageInsights')
$insightType = $assembly.GetType('UsageInsight')
if ($null -eq $insightsType -or $null -eq $insightType) { throw 'Usage insights types are missing' }
foreach ($name in @('Build', 'CalculateRate', 'GetHealthLabel')) {
    if ($null -eq $insightsType.GetMethod($name)) { throw "UsageInsights method missing: $name" }
}
$historyPointCtor = $historyPointType.GetConstructor([Type[]]@([string], [int], [double], [Nullable[DateTimeOffset]], [DateTimeOffset]))
$insightNow = [DateTimeOffset]::UtcNow
$insightReset = $insightNow.AddHours(12)
$historyList.Add($historyPointCtor.Invoke([object[]]@('chatgpt-codex', 18000, 20.0, $insightReset, $insightNow.AddHours(-2))))
$historyList.Add($historyPointCtor.Invoke([object[]]@('chatgpt-codex', 18000, 40.0, $insightReset, $insightNow)))
$insightWindowList = [Activator]::CreateInstance($listType)
$insightWindowCtor = $windowType.GetConstructor([Type[]]@([int], [double], [Nullable[DateTimeOffset]]))
$insightWindowList.Add($insightWindowCtor.Invoke([object[]]@(18000, 40.0, $insightReset)))
$insightSnapshot = $usageType.GetMethod('LiveResult').Invoke($null, [object[]]@('chatgpt-codex', 'GPT Plus', $insightWindowList, $insightNow))
$calculateRateMethod = $insightsType.GetMethod('CalculateRate')
$rate = [double]$calculateRateMethod.Invoke($null, [object[]]@($historyList, 'chatgpt-codex', 18000, $insightNow))
if ([Math]::Abs($rate - 10.0) -gt 0.1) { throw 'UsageInsights rate calculation failed' }
$historyList.Add($historyPointCtor.Invoke([object[]]@('chatgpt-codex', 18000, 99.0, $insightReset.AddHours(-24), $insightNow.AddHours(-3))))
$buildMethod = $insightsType.GetMethod('Build')
$insightList = $buildMethod.Invoke($null, [object[]]@($insightSnapshot, $historyList, $insightNow))
if ($insightList.Count -lt 1 -or -not $insightList[0].HasRate -or $insightList[0].Direction.ToString() -ne 'Rising' -or $null -eq $insightList[0].ProjectedExhaustionAt) { throw 'UsageInsights forecast was not generated' }
if ([Math]::Abs([double]$insightList[0].RatePerHour - 10.0) -gt 0.1) { throw 'UsageInsights mixed reset cycles into the current trend' }
$cachedInsightSnapshot = $insightSnapshot.WithCachedState($insightNow.AddMinutes(-10))
$cachedInsights = $buildMethod.Invoke($null, [object[]]@($cachedInsightSnapshot, $historyList, $insightNow))
if ($null -ne $cachedInsights[0].ProjectedExhaustionAt) { throw 'UsageInsights forecast should not be presented as live data for stale cache' }
$healthMethod = $insightsType.GetMethod('GetHealthLabel')
if ([string]::IsNullOrWhiteSpace([string]$healthMethod.Invoke($null, [object[]]@($insightSnapshot, $historyList, $insightNow)))) { throw 'UsageInsights health label is empty' }
$noResetWindowList = [Activator]::CreateInstance($listType)
$noResetWindowList.Add($insightWindowCtor.Invoke([object[]]@(18000, 40.0, $null)))
$noResetSnapshot = $usageType.GetMethod('LiveResult').Invoke($null, [object[]]@('chatgpt-codex', 'GPT Plus', $noResetWindowList, $insightNow))
$noResetInsights = $buildMethod.Invoke($null, [object[]]@($noResetSnapshot, $historyList, $insightNow))
if ($null -ne $noResetInsights[0].ProjectedExhaustionAt) { throw 'UsageInsights forecast ignored missing reset_at' }
$detailConstructor = $detailType.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance') |
    Where-Object { $_.GetParameters().Count -eq 4 } |
    Select-Object -First 1
if ($null -eq $detailConstructor) { throw 'UsageDetailsForm constructor is missing' }
$details = $detailConstructor.Invoke([object[]]@($live, $historyList, $null, $null))
if ($details.ShowInTaskbar) { throw 'UsageDetailsForm should stay out of the taskbar' }
if ($details.AutoScaleMode.ToString() -ne 'Dpi') { throw 'UsageDetailsForm should use DPI scaling' }
$details.Dispose()
$hubType = $assembly.GetType('UsageHubForm')
$hubSurfaceType = $assembly.GetType('UsageHubSurface')
if ($null -eq $hubType -or $null -eq $hubSurfaceType) { throw 'Usage Hub presentation types are missing' }
foreach ($name in @('SetData', 'BeginEntrance', 'AdvanceAnimation', 'SetRefreshing', 'PlayRefreshCelebration')) {
    if ($null -eq $hubSurfaceType.GetMethod($name)) { throw "UsageHubSurface method missing: $name" }
}
if ($null -eq $hubType.GetMethod('ApplyExternalRefresh')) { throw 'UsageHubForm external refresh method is missing' }
$statusWindowType = $assembly.GetType('StatusWindow')
if ($null -eq $statusWindowType.GetMethod('BeginRefreshCelebration', [Reflection.BindingFlags]'NonPublic,Instance')) {
    throw 'StatusWindow refresh celebration entry is missing'
}
$statusWindow = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($statusWindowType)
$statusSettingsField = $statusWindowType.GetField('settings', [Reflection.BindingFlags]'NonPublic,Instance')
$statusSnapshotField = $statusWindowType.GetField('snapshot', [Reflection.BindingFlags]'NonPublic,Instance')
$statusActiveField = $statusWindowType.GetField('refreshCelebrationActive', [Reflection.BindingFlags]'NonPublic,Instance')
$statusProgressField = $statusWindowType.GetField('refreshCelebrationProgress', [Reflection.BindingFlags]'NonPublic,Instance')
if ($null -eq $statusSettingsField -or $null -eq $statusSnapshotField -or $null -eq $statusActiveField -or $null -eq $statusProgressField) {
    throw 'StatusWindow refresh celebration state is missing'
}
$statusSettingsField.SetValue($statusWindow, $settingsProbe.Clone())
$statusSnapshotField.SetValue($statusWindow, $live.ToQuotaSnapshot())
$beginStatusCelebration = $statusWindowType.GetMethod('BeginRefreshCelebration', [Reflection.BindingFlags]'NonPublic,Instance')
$applyStatusResult = $statusWindowType.GetMethod('ApplyUsageResult', [Reflection.BindingFlags]'NonPublic,Instance')
$beginStatusCelebration.Invoke($statusWindow, $null)
if ([bool]$statusActiveField.GetValue($statusWindow)) { throw 'hidden StatusWindow should not start a refresh celebration' }
$applyStatusResult.Invoke($statusWindow, [object[]]@($live.WithCachedState([DateTimeOffset]::Now.AddMinutes(-1))))
$beginStatusCelebration.Invoke($statusWindow, $null)
$failureSnapshot = $usageType.GetMethod('Failure').Invoke($null, [object[]]@('chatgpt-codex', [Enum]::Parse($statusType, 'NetworkError', $false), 'offline', [DateTimeOffset]::Now))
$applyStatusResult.Invoke($statusWindow, [object[]]@($failureSnapshot))
if ([bool]$statusActiveField.GetValue($statusWindow) -or [float]$statusProgressField.GetValue($statusWindow) -ne 0) {
    throw 'cached or failed StatusWindow results should cancel an active refresh celebration'
}
$palette = $paletteType.GetMethod('Create').Invoke($null, [object[]]@([Enum]::Parse($themeType, 'Dark')))
$hubSurfaceConstructor = $hubSurfaceType.GetConstructor([Type[]]@($paletteType, [bool]))
if ($null -eq $hubSurfaceConstructor) { throw 'UsageHubSurface constructor is missing' }
$hubSurface = $hubSurfaceConstructor.Invoke([object[]]@($palette, $true))
$burstActiveField = $hubSurfaceType.GetField('refreshBurstActive', [Reflection.BindingFlags]'NonPublic,Instance')
$burstProgressField = $hubSurfaceType.GetField('refreshBurstProgress', [Reflection.BindingFlags]'NonPublic,Instance')
if ($null -eq $burstActiveField -or $null -eq $burstProgressField) { throw 'UsageHub refresh burst state is missing' }
$hubSurface.PlayRefreshCelebration()
if (-not [bool]$burstActiveField.GetValue($hubSurface)) { throw 'refresh celebration did not start after a live refresh' }
$hubSurface.SetData($live, $null)
if ([bool]$burstActiveField.GetValue($hubSurface) -or [float]$burstProgressField.GetValue($hubSurface) -ne 0) {
    throw 'refresh celebration was not cancelled when new data arrived'
}
$hubSurface.PlayRefreshCelebration()
$cachedLiveSnapshot = $live.WithCachedState([DateTimeOffset]::Now.AddMinutes(-1))
$hubSurface.SetData($cachedLiveSnapshot, $null)
if ([bool]$burstActiveField.GetValue($hubSurface)) { throw 'cached data should cancel an active refresh celebration' }
$hubSurface.PlayRefreshCelebration()
$hubSurface.SetRefreshing($true)
if ([bool]$burstActiveField.GetValue($hubSurface)) { throw 'starting a refresh should cancel the previous celebration' }
$hubSurface.SetRefreshing($false)
$hubSurface.PlayRefreshCelebration()
for ($frame = 0; $frame -lt 20; $frame++) { $hubSurface.AdvanceAnimation() }
if ([bool]$burstActiveField.GetValue($hubSurface) -or [float]$burstProgressField.GetValue($hubSurface) -lt 0.99) {
    throw 'refresh celebration did not finish as a one-shot animation'
}
$hubSurface.Dispose()
$staticHubSurface = $hubSurfaceConstructor.Invoke([object[]]@($palette, $false))
$staticHubSurface.PlayRefreshCelebration()
if ([bool]$burstActiveField.GetValue($staticHubSurface)) { throw 'disabled animations should not start refresh celebration' }
$staticHubSurface.Dispose()
$hubConstructor = $hubType.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance') |
    Where-Object { $_.GetParameters().Count -eq 4 } |
    Select-Object -First 1
if ($null -eq $hubConstructor) { throw 'UsageHubForm constructor is missing' }
$hub = $hubConstructor.Invoke([object[]]@($live, $historyList, $null, $null))
if ($hub.ShowInTaskbar -or $hub.AutoScaleMode.ToString() -ne 'Dpi' -or $hub.ClientSize.Width -lt 760 -or $hub.ClientSize.Height -lt 620 -or $hub.MinimumSize.Height -lt 620) {
    throw 'UsageHubForm window flags, DPI mode or minimum canvas size are invalid'
}
if ($hub.Controls.Count -eq 0) { throw 'UsageHubForm did not build its drawing surface' }
$hubSurfaceField = $hubType.GetField('surface', [Reflection.BindingFlags]'NonPublic,Instance')
if ($null -eq $hubSurfaceField) { throw 'UsageHubForm surface field is missing' }
$hub.ApplyExternalRefresh($live, $historyList)
$externalSurface = $hubSurfaceField.GetValue($hub)
if (-not [bool]$burstActiveField.GetValue($externalSurface)) { throw 'external live refresh did not start a Hub celebration' }
$hub.ApplyExternalRefresh($cachedLiveSnapshot, $historyList)
if ([bool]$burstActiveField.GetValue($externalSurface)) { throw 'external cached refresh did not cancel a Hub celebration' }
$hub.Dispose()
$settingsFormType = $assembly.GetType('SettingsForm')
$settingsForm = [Activator]::CreateInstance($settingsFormType, [object[]]@($settingsProbe))
if ($settingsForm.ShowInTaskbar -or $settingsForm.AutoScaleMode.ToString() -ne 'Dpi' -or $settingsForm.ClientSize.Height -lt 400) { throw 'SettingsForm window flags, DPI mode or expanded options layout is invalid' }
$animationsField = $settingsFormType.GetField('animationsCheck', [Reflection.BindingFlags]'NonPublic,Instance')
if ($null -eq $animationsField -or -not $animationsField.GetValue($settingsForm).Checked) { throw 'SettingsForm animations option is missing or not enabled by default' }
$controlQueue = New-Object 'System.Collections.Generic.Queue[System.Windows.Forms.Control]'
$controlQueue.Enqueue($settingsForm)
while ($controlQueue.Count -gt 0) {
    $control = $controlQueue.Dequeue()
    if (($control -is [System.Windows.Forms.ComboBox] -or $control -is [System.Windows.Forms.NumericUpDown] -or $control -is [System.Windows.Forms.CheckBox]) -and [string]::IsNullOrWhiteSpace($control.AccessibleName)) {
        throw 'SettingsForm interactive control is missing an accessible name'
    }
    foreach ($child in $control.Controls) { $controlQueue.Enqueue($child) }
}
$settingsForm.Dispose()
$diagnosticsType = $assembly.GetType('DiagnosticsService')
$diagnostics = [Activator]::CreateInstance($diagnosticsType)
$loadingSnapshot = $assembly.GetType('QuotaSnapshot').GetMethod('Loading').Invoke($null, $null)
$report = $diagnosticsType.GetMethod('Build').Invoke($diagnostics, [object[]]@($loadingSnapshot, 'oauth-readable', 'system-network', $true, $false, $settingsProbe))
$themeMarker = ([char]0x4e3b).ToString() + ([char]0x9898).ToString() + ([char]0xff1a).ToString() + ([char]0x8ddf).ToString() + ([char]0x968f).ToString() + ([char]0x7cfb).ToString() + ([char]0x7edf).ToString()
$historyMarker = ([char]0x5386).ToString() + ([char]0x53f2).ToString() + ([char]0x4fdd).ToString() + ([char]0x7559).ToString() + ([char]0xff1a).ToString() + '30 ' + ([char]0x5929).ToString()
$updateMarker = ([char]0x542f).ToString() + ([char]0x52a8).ToString() + ([char]0x66f4).ToString() + ([char]0x65b0).ToString() + ([char]0x68c0).ToString() + ([char]0x67e5).ToString() + ([char]0xff1a).ToString() + ([char]0x5df2).ToString() + ([char]0x5173).ToString() + ([char]0x95ed).ToString()
if ($report -notmatch [regex]::Escape($themeMarker) -or $report -notmatch [regex]::Escape($historyMarker) -or $report -notmatch [regex]::Escape($updateMarker)) { throw 'Diagnostics report did not include normalized startup/history settings' }
$extendedReportMethod = $diagnosticsType.GetMethod('BuildExtended')
if ($null -eq $extendedReportMethod) { throw 'Diagnostics extended report method is missing' }
$extendedReport = [string]$extendedReportMethod.Invoke($diagnostics, [object[]]@($loadingSnapshot, 'oauth-readable', 'system-network', $true, $false, $settingsProbe, 3, $true, $true, $false, $true, $true, [DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now))
$recentAgeMarker = ([char]0x6700).ToString() + ([char]0x8fd1).ToString() + ([char]0x6210).ToString() + ([char]0x529f).ToString() + ([char]0x5e74).ToString() + ([char]0x9f84).ToString()
if ($extendedReport.Length -le $report.Length -or $extendedReport -notmatch 'Ctrl\+Alt\+U' -or $extendedReport -notmatch '3' -or $extendedReport -notmatch [regex]::Escape($recentAgeMarker)) { throw 'Diagnostics extended report omitted local feature status' }
if ($extendedReport -match 'access_token|refresh_token|Bearer|account_id|127\.0\.0\.1') { throw 'Diagnostics extended report leaked sensitive values' }

$checkStatusType = $assembly.GetType('DiagnosticCheckStatus')
$checkType = $assembly.GetType('DiagnosticCheck')
$diagnosticSnapshotType = $assembly.GetType('DiagnosticSnapshot')
$diagnosticFormType = $assembly.GetType('DiagnosticsForm')
if ($null -eq $checkStatusType -or $null -eq $checkType -or $null -eq $diagnosticSnapshotType -or $null -eq $diagnosticFormType) { throw 'Diagnostics center types are missing' }
$checks = $diagnosticsType.GetMethod('BuildChecks').Invoke($diagnostics, [object[]]@($loadingSnapshot, 'oauth-readable', 'system-network', $true, $false, $settingsProbe))
if ($checks.Count -lt 5) { throw 'Diagnostics center returned too few checks' }
$checksText = $checks | ConvertTo-Json -Depth 5
if ($checksText -match 'secret-plan-value|secret-account-value|access_token|account_id') { throw 'Diagnostics checks leaked sensitive test values' }
$extendedChecksMethod = $diagnosticsType.GetMethod('BuildChecksExtended')
if ($null -eq $extendedChecksMethod) { throw 'Diagnostics extended checks method is missing' }
$extendedChecks = $extendedChecksMethod.Invoke($diagnostics, [object[]]@($loadingSnapshot, 'oauth-readable', 'system-network', $true, $false, $settingsProbe, 3, $true, $true, $true, $false, $true, [DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now))
if ($extendedChecks.Count -lt 11) { throw 'Diagnostics extended checks returned too few checks' }
$extendedChecksText = $extendedChecks | ConvertTo-Json -Depth 5
if ($extendedChecksText -match 'access_token|account_id|127\.0\.0\.1') { throw 'Diagnostics extended checks are incomplete or unsafe' }
$diagnosticFormConstructor = $diagnosticFormType.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance') |
    Where-Object { $_.GetParameters().Count -eq 4 } |
    Select-Object -First 1
if ($null -eq $diagnosticFormConstructor) { throw 'DiagnosticsForm constructor is missing' }
$diagnosticForm = $diagnosticFormConstructor.Invoke([object[]]@([string]$report, $checks, $null, [Enum]::ToObject($themeType, 0)))
if ($diagnosticForm.ShowInTaskbar -or $diagnosticForm.AutoScaleMode.ToString() -ne 'Dpi' -or $diagnosticForm.StartPosition.ToString() -ne 'CenterScreen' -or $diagnosticForm.MaximizeBox) { throw 'DiagnosticsForm window flags, centering or custom chrome state are invalid' }
$diagnosticSnapshot = [Activator]::CreateInstance($diagnosticSnapshotType, [object[]]@([string]$report, $checks))
$diagnosticForm.UpdateSnapshot($diagnosticSnapshot)
if ($diagnosticForm.Controls.Count -eq 0) { throw 'DiagnosticsForm did not build its control layout' }
$diagnosticForm.Dispose()

$updateType = $assembly.GetType('UpdateService')
if ($null -eq $updateType) { throw 'UpdateService type is missing' }
$currentVersionField = $updateType.GetField('CurrentVersion')
if ($null -eq $currentVersionField -or [string]::IsNullOrWhiteSpace([string]$currentVersionField.GetValue($null))) { throw 'UpdateService version is missing' }
$verifyMethod = $updateType.GetMethod('VerifySha256')
$normalizeDigestMethod = $updateType.GetMethod('NormalizeDigest', [Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $normalizeDigestMethod) { throw 'UpdateService digest normalizer is missing' }
if (-not [string]::IsNullOrWhiteSpace([string]$normalizeDigestMethod.Invoke($null, [object[]]@((('z' * 64) -join ''))))) { throw 'invalid digest characters were accepted' }
$hashPath = Join-Path ([IO.Path]::GetTempPath()) ('usage-hash-' + [Guid]::NewGuid().ToString('N') + '.bin')
try {
    [IO.File]::WriteAllText($hashPath, 'usage-hub', [Text.Encoding]::UTF8)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([IO.File]::ReadAllBytes($hashPath))
        $expectedHash = ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
    if (-not [bool]$verifyMethod.Invoke($null, [object[]]@([string]$hashPath, ('sha256:' + $expectedHash)))) { throw 'SHA-256 verification failed' }
    if ([bool]$verifyMethod.Invoke($null, [object[]]@([string]$hashPath, (('0' * 64) -join '')))) { throw 'invalid SHA-256 digest was accepted' }
}
finally {
    Remove-Item -LiteralPath $hashPath -Force -ErrorAction SilentlyContinue
}

'P1 usage model smoke: PASS'
