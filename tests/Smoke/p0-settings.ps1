$ErrorActionPreference = 'Stop'

$exePath = $env:STATUSBAR_EXE
if ([string]::IsNullOrWhiteSpace($exePath)) {
    $exePath = Join-Path $PSScriptRoot '..\..\SubscriptionStatus.exe'
}
$exePath = (Resolve-Path -LiteralPath $exePath).Path
$assembly = [Reflection.Assembly]::LoadFrom($exePath)

$settingsType = $assembly.GetType('AppSettings')
if ($null -eq $settingsType) { throw 'AppSettings type is missing' }
$settings = [Activator]::CreateInstance($settingsType)
$settings.RefreshIntervalMinutes = 2
$settings.NotificationThresholdPercent = 150
$settings.Normalize()
if ($settings.RefreshIntervalMinutes -ne 5) { throw 'invalid refresh interval was not normalized' }
if ($settings.NotificationThresholdPercent -ne 80) { throw 'invalid threshold was not normalized' }
$clone = $settings.Clone()
if ([object]::ReferenceEquals($settings, $clone)) { throw 'settings clone shares the source object' }

$tempRoot = Join-Path $env:TEMP ('chatgpt-codex-statusbar-smoke-' + [Guid]::NewGuid().ToString('N'))
$settingsPath = Join-Path $tempRoot 'settings.json'
try {
    $storeType = $assembly.GetType('SettingsStore')
    $constructor = $storeType.GetConstructor(
        [Reflection.BindingFlags]'Instance,NonPublic',
        $null,
        [Type[]]@([string]),
        $null)
    if ($null -eq $constructor) { throw 'test SettingsStore constructor is missing' }
    $store = $constructor.Invoke([object[]]@([string]$settingsPath))

    $settings.RefreshIntervalMinutes = 10
    $settings.NotificationThresholdPercent = 90
    $settings.AnimationsEnabled = $false
    $store.Save($settings)
    $savedJson = [IO.File]::ReadAllText($settingsPath)
    if ($savedJson -match 'access_token|refresh_token|id_token|account_id') { throw 'settings file contains credential fields' }
    $loaded = $store.Load()
    if ($loaded.RefreshIntervalMinutes -ne 10) { throw 'saved refresh interval was not loaded' }
    if ($loaded.NotificationThresholdPercent -ne 90) { throw 'saved threshold was not loaded' }
    if ($loaded.AnimationsEnabled) { throw 'saved animations setting was not loaded' }

    [IO.File]::WriteAllText($settingsPath, '{ invalid json', [Text.Encoding]::UTF8)
    $fallback = $store.Load()
    if ($fallback.RefreshIntervalMinutes -ne 5) { throw 'corrupt settings did not fall back to defaults' }
    if (-not (Test-Path -LiteralPath ($settingsPath + '.bak'))) { throw 'corrupt settings backup is missing' }
    'P0 settings smoke: PASS'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsKeyPath = 'HKCU:\Software\ChatGPTCodexUsageStatusBar'
$startupName = 'ChatGPTCodexUsageStatusBar'
$legacyName = 'ChatGPTCodexUsageStatusBarConfigured'
$runBefore = Get-ItemProperty -Path $runKeyPath -ErrorAction SilentlyContinue
$settingsBefore = Get-ItemProperty -Path $settingsKeyPath -ErrorAction SilentlyContinue
$oldStartupValue = if ($null -ne $runBefore -and $null -ne $runBefore.PSObject.Properties[$startupName]) { [string]$runBefore.PSObject.Properties[$startupName].Value } else { $null }
$oldLegacyValue = if ($null -ne $runBefore -and $null -ne $runBefore.PSObject.Properties[$legacyName]) { [string]$runBefore.PSObject.Properties[$legacyName].Value } else { $null }
$oldConfiguredValue = if ($null -ne $settingsBefore -and $null -ne $settingsBefore.PSObject.Properties[$legacyName]) { [string]$settingsBefore.PSObject.Properties[$legacyName].Value } else { $null }

try {
    Remove-ItemProperty -Path $runKeyPath -Name $startupName -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKeyPath -Name $legacyName -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $settingsKeyPath -Name $legacyName -ErrorAction SilentlyContinue
    New-Item -Path $runKeyPath -Force | Out-Null
    Set-ItemProperty -Path $runKeyPath -Name $startupName -Value ('"' + $exePath + '"')
    Set-ItemProperty -Path $runKeyPath -Name $legacyName -Value '1'

    $startupType = $assembly.GetType('StartupManager')
    if ($null -eq $startupType) { throw 'StartupManager type is missing' }
    $startupManager = [Activator]::CreateInstance($startupType, [object[]]@([string]$exePath))
    $getMethod = $startupType.GetMethod('TryGetEnabled')
    $getArgs = [object[]]@($false, '')
    $getResult = $getMethod.Invoke($startupManager, $getArgs)
    if (-not [bool]$getResult -or -not [bool]$getArgs[0]) { throw 'startup migration did not enable the app' }

    $runAfterMigration = Get-ItemProperty -Path $runKeyPath -ErrorAction Stop
    $settingsAfterMigration = Get-ItemProperty -Path $settingsKeyPath -ErrorAction Stop
    if ($null -ne $runAfterMigration.PSObject.Properties[$legacyName]) { throw 'legacy Run marker was not removed' }
    if ([string]::IsNullOrWhiteSpace([string]$settingsAfterMigration.PSObject.Properties[$legacyName].Value)) { throw 'startup marker was not migrated' }

    $setMethod = $startupType.GetMethod('TrySetEnabled')
    $setOffArgs = [object[]]@($false, '')
    if (-not [bool]$setMethod.Invoke($startupManager, $setOffArgs)) { throw 'startup disable failed' }
    $runAfterOff = Get-ItemProperty -Path $runKeyPath -ErrorAction Stop
    if ($null -ne $runAfterOff.PSObject.Properties[$startupName]) { throw 'startup value remained after disable' }

    $setOnArgs = [object[]]@($true, '')
    if (-not [bool]$setMethod.Invoke($startupManager, $setOnArgs)) { throw 'startup enable failed' }
    $runAfterOn = Get-ItemProperty -Path $runKeyPath -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace([string]$runAfterOn.PSObject.Properties[$startupName].Value)) { throw 'startup value missing after enable' }
    if ([string]$runAfterOn.PSObject.Properties[$startupName].Value -cne ('"' + $exePath + '"')) { throw 'startup command is not quoted executable path' }
    if ($null -ne $runAfterOn.PSObject.Properties[$legacyName]) { throw 'legacy Run marker returned after enable' }
    'P0 startup smoke: PASS'
}
finally {
    Remove-ItemProperty -Path $runKeyPath -Name $startupName -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKeyPath -Name $legacyName -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $settingsKeyPath -Name $legacyName -ErrorAction SilentlyContinue
    if ($null -ne $oldStartupValue) { Set-ItemProperty -Path $runKeyPath -Name $startupName -Value $oldStartupValue }
    if ($null -ne $oldLegacyValue) { Set-ItemProperty -Path $runKeyPath -Name $legacyName -Value $oldLegacyValue }
    if ($null -ne $oldConfiguredValue) { Set-ItemProperty -Path $settingsKeyPath -Name $legacyName -Value $oldConfiguredValue }
}

$windowType = $assembly.GetType('QuotaWindow')
$windowConstructor = $windowType.GetConstructor([Type[]]@([string], [int], [double], [Nullable[DateTimeOffset]]))
$windowListType = [System.Collections.Generic.List``1].MakeGenericType($windowType)
$reset1 = [DateTimeOffset]::Now
$reset2 = $reset1.AddHours(5)
$windowList = [Activator]::CreateInstance($windowListType)
$windowList.Add($windowConstructor.Invoke([object[]]@('5 小时窗口', 18000, 70.0, $reset1)))
$snapshotType = $assembly.GetType('QuotaSnapshot')
$snapshotMethod = $snapshotType.GetMethod('SuccessResult')
$snapshot = $snapshotMethod.Invoke($null, [object[]]@('secret-plan-value', 'secret-account-value', $windowList))

$evaluatorType = $assembly.GetType('NotificationEvaluator')
$evaluator = [Activator]::CreateInstance($evaluatorType)
$evaluateMethod = $evaluatorType.GetMethod('Evaluate')
$firstNotifications = $evaluateMethod.Invoke($evaluator, [object[]]@($snapshot, 80))
if ($firstNotifications.Count -ne 0) { throw 'notification evaluator alerted on initial baseline' }
$windowList2 = [Activator]::CreateInstance($windowListType)
$windowList2.Add($windowConstructor.Invoke([object[]]@('5 小时窗口', 18000, 85.0, $reset1)))
$snapshot2 = $snapshotMethod.Invoke($null, [object[]]@('secret-plan-value', 'secret-account-value', $windowList2))
$secondNotifications = $evaluateMethod.Invoke($evaluator, [object[]]@($snapshot2, 80))
if ($secondNotifications.Count -ne 1) { throw 'notification evaluator did not detect threshold crossing' }
$thirdNotifications = $evaluateMethod.Invoke($evaluator, [object[]]@($snapshot2, 80))
if ($thirdNotifications.Count -ne 0) { throw 'notification evaluator repeated the same alert' }
$windowList3 = [Activator]::CreateInstance($windowListType)
$windowList3.Add($windowConstructor.Invoke([object[]]@('5-hour window', 18000, 70.0, $reset2)))
$snapshot3 = $snapshotMethod.Invoke($null, [object[]]@('secret-plan-value', 'secret-account-value', $windowList3))
$resetNotifications = $evaluateMethod.Invoke($evaluator, [object[]]@($snapshot3, 80))
if ($resetNotifications.Count -ne 0) { throw 'notification evaluator alerted while establishing a new reset baseline' }
$windowList4 = [Activator]::CreateInstance($windowListType)
$windowList4.Add($windowConstructor.Invoke([object[]]@('5-hour window', 18000, 90.0, $reset2)))
$snapshot4 = $snapshotMethod.Invoke($null, [object[]]@('secret-plan-value', 'secret-account-value', $windowList4))
$postResetNotifications = $evaluateMethod.Invoke($evaluator, [object[]]@($snapshot4, 80))
if ($postResetNotifications.Count -ne 1) { throw 'notification evaluator did not alert after a new reset cycle' }
$evaluatorNull = [Activator]::CreateInstance($evaluatorType)
$evaluateMethod.Invoke($evaluatorNull, [object[]]@($snapshot, 80)) | Out-Null
$evaluateMethod.Invoke($evaluatorNull, [object[]]@($snapshot2, 80)) | Out-Null
$windowListNull = [Activator]::CreateInstance($windowListType)
$windowListNull.Add($windowConstructor.Invoke([object[]]@('5-hour window', 18000, 85.0, $null)))
$snapshotNull = $snapshotMethod.Invoke($null, [object[]]@('secret-plan-value', 'secret-account-value', $windowListNull))
$nullNotifications = $evaluateMethod.Invoke($evaluatorNull, [object[]]@($snapshotNull, 80))
if ($nullNotifications.Count -ne 0) { throw 'missing reset_at field caused a duplicate notification' }

$diagnosticsType = $assembly.GetType('DiagnosticsService')
$diagnostics = [Activator]::CreateInstance($diagnosticsType)
$buildMethod = $diagnosticsType.GetMethod('Build')
$safeSettings = [Activator]::CreateInstance($settingsType)
$report = [string]$buildMethod.Invoke($diagnostics, [object[]]@(
    $snapshot2,
    'OAuth: ChatGPT OAuth config readable',
    'Network: custom HTTP proxy (address hidden)',
    $true,
    $false,
    $safeSettings))
if ($report -match 'secret-plan-value|secret-account-value|account_id|127\.0\.0\.1') { throw 'diagnostic report leaked sensitive test values' }
if ($report -notmatch 'ChatGPT') { throw 'diagnostic plan allowlist is missing' }
'P0 notification and diagnostics smoke: PASS'
