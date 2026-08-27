$ErrorActionPreference = 'Stop'

$exePath = $env:STATUSBAR_EXE
if ([string]::IsNullOrWhiteSpace($exePath)) {
    $exePath = Join-Path $PSScriptRoot '..\..\SubscriptionStatus.exe'
}
$exePath = (Resolve-Path -LiteralPath $exePath).Path
$assembly = [Reflection.Assembly]::LoadFrom($exePath)

$usageType = $assembly.GetType('UsageSnapshot')
$providerType = $assembly.GetType('IUsageProvider')
if ($null -eq $usageType -or $null -eq $providerType) { throw 'Usage model/provider boundary is missing' }
foreach ($name in @('ProviderId', 'Status', 'IsStale', 'LastLiveAt', 'ErrorCode')) {
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
foreach ($name in @('HistoryRetentionDays', 'LaunchDelaySeconds', 'AutoCheckUpdates')) {
    if ($null -eq $settingsType.GetProperty($name)) { throw "AppSettings property missing: $name" }
}
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
$sanitizerType = $assembly.GetType('DiagnosticSanitizer')
$planMethod = $sanitizerType.GetMethod('PlanName')
if ($planMethod.Invoke($null, @('GPT Pro')) -cne 'GPT Pro') { throw 'GPT Pro plan was incorrectly collapsed' }
if ($planMethod.Invoke($null, @('GPT Team')) -cne 'GPT Team') { throw 'GPT Team plan was incorrectly collapsed' }
if ($planMethod.Invoke($null, @('untrusted plan text')) -cne 'ChatGPT') { throw 'unknown plan text was not sanitized' }

$listType = [System.Collections.Generic.List``1].MakeGenericType($windowType)
$windows = [Activator]::CreateInstance($listType)
$windows.Add($window)
$live = $usageType.GetMethod('LiveResult').Invoke($null, [object[]]@('chatgpt-codex', 'GPT Plus', $windows, [DateTimeOffset]::Now))
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
$shortHistory.Clear()
Remove-Item -LiteralPath $tempRoot -Recurse -Force

$detailType = $assembly.GetType('UsageDetailsForm')
$historyPointType = $assembly.GetType('HistoryPoint')
$historyListType = [System.Collections.Generic.List``1].MakeGenericType($historyPointType)
$historyList = [Activator]::CreateInstance($historyListType)
$detailConstructor = $detailType.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance') |
    Where-Object { $_.GetParameters().Count -eq 4 } |
    Select-Object -First 1
if ($null -eq $detailConstructor) { throw 'UsageDetailsForm constructor is missing' }
$details = $detailConstructor.Invoke([object[]]@($live, $historyList, $null, $null))
if ($details.ShowInTaskbar) { throw 'UsageDetailsForm should stay out of the taskbar' }
if ($details.AutoScaleMode.ToString() -ne 'Dpi') { throw 'UsageDetailsForm should use DPI scaling' }
$details.Dispose()
$settingsFormType = $assembly.GetType('SettingsForm')
$settingsForm = [Activator]::CreateInstance($settingsFormType, [object[]]@($settingsProbe))
if ($settingsForm.ShowInTaskbar -or $settingsForm.AutoScaleMode.ToString() -ne 'Dpi' -or $settingsForm.ClientSize.Height -lt 400) { throw 'SettingsForm window flags, DPI mode or expanded options layout is invalid' }
$settingsForm.Dispose()
$diagnosticsType = $assembly.GetType('DiagnosticsService')
$diagnostics = [Activator]::CreateInstance($diagnosticsType)
$loadingSnapshot = $assembly.GetType('QuotaSnapshot').GetMethod('Loading').Invoke($null, $null)
$report = $diagnosticsType.GetMethod('Build').Invoke($diagnostics, [object[]]@($loadingSnapshot, 'oauth-readable', 'system-network', $true, $false, $settingsProbe))
$themeMarker = ([char]0x4e3b).ToString() + ([char]0x9898).ToString() + ([char]0xff1a).ToString() + ([char]0x8ddf).ToString() + ([char]0x968f).ToString() + ([char]0x7cfb).ToString() + ([char]0x7edf).ToString()
$historyMarker = ([char]0x5386).ToString() + ([char]0x53f2).ToString() + ([char]0x4fdd).ToString() + ([char]0x7559).ToString() + ([char]0xff1a).ToString() + '30 ' + ([char]0x5929).ToString()
$updateMarker = ([char]0x542f).ToString() + ([char]0x52a8).ToString() + ([char]0x66f4).ToString() + ([char]0x65b0).ToString() + ([char]0x68c0).ToString() + ([char]0x67e5).ToString() + ([char]0xff1a).ToString() + ([char]0x5df2).ToString() + ([char]0x5173).ToString() + ([char]0x95ed).ToString()
if ($report -notmatch [regex]::Escape($themeMarker) -or $report -notmatch [regex]::Escape($historyMarker) -or $report -notmatch [regex]::Escape($updateMarker)) { throw 'Diagnostics report did not include normalized startup/history settings' }

$checkStatusType = $assembly.GetType('DiagnosticCheckStatus')
$checkType = $assembly.GetType('DiagnosticCheck')
$diagnosticSnapshotType = $assembly.GetType('DiagnosticSnapshot')
$diagnosticFormType = $assembly.GetType('DiagnosticsForm')
if ($null -eq $checkStatusType -or $null -eq $checkType -or $null -eq $diagnosticSnapshotType -or $null -eq $diagnosticFormType) { throw 'Diagnostics center types are missing' }
$checks = $diagnosticsType.GetMethod('BuildChecks').Invoke($diagnostics, [object[]]@($loadingSnapshot, 'oauth-readable', 'system-network', $true, $false, $settingsProbe))
if ($checks.Count -lt 5) { throw 'Diagnostics center returned too few checks' }
$checksText = $checks | ConvertTo-Json -Depth 5
if ($checksText -match 'secret-plan-value|secret-account-value|access_token|account_id') { throw 'Diagnostics checks leaked sensitive test values' }
$diagnosticFormConstructor = $diagnosticFormType.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance') |
    Where-Object { $_.GetParameters().Count -eq 4 } |
    Select-Object -First 1
if ($null -eq $diagnosticFormConstructor) { throw 'DiagnosticsForm constructor is missing' }
$diagnosticForm = $diagnosticFormConstructor.Invoke([object[]]@([string]$report, $checks, $null, [Enum]::ToObject($themeType, 0)))
if ($diagnosticForm.ShowInTaskbar -or $diagnosticForm.AutoScaleMode.ToString() -ne 'Dpi') { throw 'DiagnosticsForm window flags or DPI mode are invalid' }
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
