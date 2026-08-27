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
$settingsProbe.Normalize()
if ($settingsProbe.Theme.ToString() -ne 'System') { throw 'invalid theme was not normalized' }
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
$history.Clear()
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
if ($settingsForm.ShowInTaskbar -or $settingsForm.AutoScaleMode.ToString() -ne 'Dpi') { throw 'SettingsForm window flags or DPI mode are invalid' }
$settingsForm.Dispose()

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
