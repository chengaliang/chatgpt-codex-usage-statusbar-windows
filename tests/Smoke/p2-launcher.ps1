$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class LauncherSmokeNativeP2
{
    public delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public static IntPtr FindProcessWindow(uint targetProcessId, string expectedTitle)
    {
        IntPtr matchedHandle = IntPtr.Zero;
        EnumWindowsCallback callback = delegate(IntPtr hWnd, IntPtr lParam)
        {
            uint windowProcessId = 0;
            GetWindowThreadProcessId(hWnd, out windowProcessId);
            if (windowProcessId != targetProcessId)
            {
                return true;
            }

            System.Text.StringBuilder title = new System.Text.StringBuilder(128);
            GetWindowText(hWnd, title, title.Capacity);
            if (string.Equals(title.ToString(), expectedTitle, StringComparison.Ordinal))
            {
                matchedHandle = hWnd;
                return false;
            }

            return true;
        };

        EnumWindows(callback, IntPtr.Zero);
        return matchedHandle;
    }
}
'@

$exePath = $env:STATUSBAR_EXE
if ([string]::IsNullOrWhiteSpace($exePath)) {
    $exePath = Join-Path $PSScriptRoot '..\..\dist\SubscriptionStatus.exe'
}
$exePath = (Resolve-Path -LiteralPath $exePath).Path
$rootPath = Join-Path (Split-Path -Parent (Split-Path -Parent $exePath)) 'SubscriptionStatus.exe'
$iconSourcePath = Join-Path (Split-Path -Parent (Split-Path -Parent $exePath)) 'assets\ChatGPTCodexUsageStatusBar.ico'
$windowAssertionsEnabled = [Environment]::UserInteractive -and [System.Windows.Forms.SystemInformation]::UserInteractive
if ($env:GITHUB_ACTIONS -eq 'true' -and $env:STATUSBAR_FORCE_WINDOW_SMOKE -ne '1') {
    $windowAssertionsEnabled = $false
}
$isolatedHome = Join-Path $env:TEMP ('chatgpt-codex-launcher-home-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $isolatedHome | Out-Null
$oldCodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
$oldProxy = [Environment]::GetEnvironmentVariable('CLASH_MIXED_PROXY', 'Process')
$oldDataHome = [Environment]::GetEnvironmentVariable('STATUSBAR_DATA_HOME', 'Process')
$oldLocalAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA', 'Process')
$oldAppData = [Environment]::GetEnvironmentVariable('APPDATA', 'Process')
$runRegistryPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$settingsRegistryPath = 'Software\ChatGPTCodexUsageStatusBar'
$startupRegistryValueNames = @(
    'ChatGPTCodexUsageStatusBar',
    'ChatGPTCodexUsageStatusBarConfigured'
)

function Capture-RegistryValue {
    param(
        [string]$SubKeyPath,
        [string]$ValueName
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKeyPath, $false)
    if ($null -eq $key) {
        return [pscustomobject]@{
            SubKeyPath = $SubKeyPath
            ValueName = $ValueName
            KeyExists = $false
            ValueExists = $false
            Value = $null
            ValueKind = $null
        }
    }

    try {
        $valueExists = $key.GetValueNames() -contains $ValueName
        if (-not $valueExists) {
            return [pscustomobject]@{
                SubKeyPath = $SubKeyPath
                ValueName = $ValueName
                KeyExists = $true
                ValueExists = $false
                Value = $null
                ValueKind = $null
            }
        }

        return [pscustomobject]@{
            SubKeyPath = $SubKeyPath
            ValueName = $ValueName
            KeyExists = $true
            ValueExists = $true
            Value = $key.GetValue($ValueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            ValueKind = $key.GetValueKind($ValueName)
        }
    }
    finally {
        $key.Dispose()
    }
}

function Restore-RegistryValue {
    param([pscustomobject]$Snapshot)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Snapshot.SubKeyPath, $true)
    if ($null -ne $key) {
        try {
            if ($Snapshot.ValueExists) {
                $key.SetValue($Snapshot.ValueName, $Snapshot.Value, $Snapshot.ValueKind)
            }
            else {
                $key.DeleteValue($Snapshot.ValueName, $false)
            }
        }
        finally {
            $key.Dispose()
        }
    }

    if (-not $Snapshot.KeyExists) {
        $remaining = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Snapshot.SubKeyPath, $false)
        if ($null -ne $remaining) {
            $deleteEmptyKey = $false
            try {
                $deleteEmptyKey = $remaining.ValueCount -eq 0 -and $remaining.SubKeyCount -eq 0
            }
            finally {
                $remaining.Dispose()
            }
            if ($deleteEmptyKey) {
                [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($Snapshot.SubKeyPath, $false)
            }
        }
    }
}

$registrySnapshots = @()
foreach ($valueName in $startupRegistryValueNames) {
    $registrySnapshots += Capture-RegistryValue $runRegistryPath $valueName
}
$registrySnapshots += Capture-RegistryValue $settingsRegistryPath 'ChatGPTCodexUsageStatusBarConfigured'

# Keep launcher smoke offline and credential-free.
[Environment]::SetEnvironmentVariable('CODEX_HOME', $isolatedHome, 'Process')
[Environment]::SetEnvironmentVariable('CLASH_MIXED_PROXY', 'http://127.0.0.1:1', 'Process')
[Environment]::SetEnvironmentVariable('STATUSBAR_DATA_HOME', $isolatedHome, 'Process')
[Environment]::SetEnvironmentVariable('LOCALAPPDATA', (Join-Path $isolatedHome 'local'), 'Process')
[Environment]::SetEnvironmentVariable('APPDATA', (Join-Path $isolatedHome 'roaming'), 'Process')

function Get-StatusProcesses {
    param([string]$Path)

    @(
        Get-CimInstance Win32_Process -Filter "Name = 'SubscriptionStatus.exe'" |
            Where-Object { $_.ExecutablePath -eq $Path }
    )
}

function Stop-StatusProcesses {
    param([string]$Path)

    foreach ($statusProcess in (Get-StatusProcesses $Path)) {
        Stop-Process -Id $statusProcess.ProcessId -Force -ErrorAction SilentlyContinue
    }

    $deadline = (Get-Date).AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 100
        $remaining = Get-StatusProcesses $Path
    } while (@($remaining).Count -gt 0 -and (Get-Date) -lt $deadline)
}

try {
    $existingDeadline = (Get-Date).AddSeconds(3)
    do {
        $existing = Get-StatusProcesses $exePath
        if (@($existing).Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $existingDeadline)
    if (@($existing).Count -gt 0) {
        throw 'P2 launcher smoke requires no pre-existing SubscriptionStatus.exe instance'
    }

    $first = $null
    $second = $null
    $process = $null

    try {
        $first = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath) -PassThru
    $deadline = (Get-Date).AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 250
        $first.Refresh()
        $process = Get-Process -Id $first.Id -ErrorAction SilentlyContinue
    } while ($null -ne $process -and -not $process.HasExited -and (Get-Date) -lt $deadline -and $windowAssertionsEnabled -and $process.MainWindowHandle -eq 0)

    if ($null -eq $process -or $process.HasExited) {
        throw 'status bar process exited during startup'
    }
    if (-not $process.Responding) {
        throw 'status bar process is not responding after startup'
    }
    if ($windowAssertionsEnabled -and $process.MainWindowHandle -eq 0) {
        throw 'status bar did not create its visible window handle'
    }

    $windowHandle = [IntPtr]::Zero
    if ($windowAssertionsEnabled) {
        [uint32]$windowProcessId = 0
        $windowTitleDeadline = (Get-Date).AddSeconds(3)
        do {
            Start-Sleep -Milliseconds 100
            $windowHandle = [LauncherSmokeNativeP2]::FindProcessWindow([uint32]$process.Id, 'ChatGPT quota')
            $windowProcessId = 0
            if ($windowHandle -ne [IntPtr]::Zero) {
                [LauncherSmokeNativeP2]::GetWindowThreadProcessId($windowHandle, [ref]$windowProcessId) | Out-Null
            }
        } while (($windowHandle -eq [IntPtr]::Zero -or $windowProcessId -ne [uint32]$process.Id) -and (Get-Date) -lt $windowTitleDeadline)
        if ($windowHandle -eq [IntPtr]::Zero -or $windowProcessId -ne [uint32]$process.Id) {
            throw 'status bar window title did not resolve to the launched process'
        }

        # Minimize first so the second launch must restore the existing window.
        [LauncherSmokeNativeP2]::ShowWindow($windowHandle, 6) | Out-Null
        Start-Sleep -Milliseconds 150
        if (-not [LauncherSmokeNativeP2]::IsIconic($windowHandle)) {
            throw 'launcher smoke could not minimize the status bar window'
        }
    }

    $second = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath) -PassThru
    if (-not $second.WaitForExit(4000)) {
        throw 'second launch did not return after handing off to the existing instance'
    }
    $instancesDeadline = (Get-Date).AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 100
        $instancesAfterSecondLaunch = Get-StatusProcesses $exePath
    } while (@($instancesAfterSecondLaunch).Count -eq 0 -and (Get-Date) -lt $instancesDeadline)
    if (@($instancesAfterSecondLaunch).Count -ne 1) {
        $details = ($instancesAfterSecondLaunch | ForEach-Object { $_.ProcessId.ToString() + ':' + [string]$_.ExecutablePath }) -join ', '
        throw ('second launch created a duplicate status bar process (count=' + @($instancesAfterSecondLaunch).Count + ', ' + $details + ')')
    }
    if ($windowAssertionsEnabled) {
        $restoreDeadline = (Get-Date).AddSeconds(3)
        while ([LauncherSmokeNativeP2]::IsIconic($windowHandle) -and (Get-Date) -lt $restoreDeadline) {
            Start-Sleep -Milliseconds 100
        }
        if ([LauncherSmokeNativeP2]::IsIconic($windowHandle) -or -not [LauncherSmokeNativeP2]::IsWindowVisible($windowHandle)) {
            throw 'second launch did not restore the existing status bar window'
        }
        'P2 direct launch and duplicate-instance smoke: PASS'
    }
    else {
        'P2 process and duplicate-instance smoke: PASS (window assertions skipped in headless session)'
    }
    }
    finally {
        if ($null -ne $second -and -not $second.HasExited) {
            Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue
        }
        if ($null -ne $first -and -not $first.HasExited) {
            Stop-Process -Id $first.Id -Force -ErrorAction SilentlyContinue
        }
        Stop-StatusProcesses $exePath
    }

if (Test-Path -LiteralPath $rootPath -PathType Leaf) {
    $existingAfterCleanupDeadline = (Get-Date).AddSeconds(3)
    do {
        $existingAfterCleanup = Get-StatusProcesses $exePath
        if (@($existingAfterCleanup).Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $existingAfterCleanupDeadline)
    if (@($existingAfterCleanup).Count -gt 0) {
        throw 'compatibility launcher smoke started with a stale process'
    }

    $shim = $null
    try {
        $shim = Start-Process -FilePath $rootPath -WorkingDirectory (Split-Path -Parent $rootPath) -PassThru
        $shimDeadline = (Get-Date).AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 250
            $shimProcesses = Get-StatusProcesses $exePath
        } while (@($shimProcesses).Count -eq 0 -and (Get-Date) -lt $shimDeadline)
        if (@($shimProcesses).Count -ne 1) {
            throw 'root compatibility launcher did not start the dist executable'
        }
        'P2 root compatibility launcher smoke: PASS'
    }
    finally {
        if ($null -ne $shim -and -not $shim.HasExited) {
            Stop-Process -Id $shim.Id -Force -ErrorAction SilentlyContinue
        }
        Stop-StatusProcesses $exePath
    }
}

if (-not (Test-Path -LiteralPath $iconSourcePath -PathType Leaf)) {
    throw 'product icon source asset is missing'
}
$iconSourceBytes = [IO.File]::ReadAllBytes($iconSourcePath)
if ($iconSourceBytes.Length -lt 6 -or [BitConverter]::ToUInt16($iconSourceBytes, 0) -ne 0 -or
    [BitConverter]::ToUInt16($iconSourceBytes, 2) -ne 1 -or [BitConverter]::ToUInt16($iconSourceBytes, 4) -ne 4) {
    throw 'product icon source asset has an invalid ICO header'
}

$iconPaths = @($exePath)
if (Test-Path -LiteralPath $rootPath -PathType Leaf) {
    $iconPaths += $rootPath
}
foreach ($iconPath in $iconPaths) {
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($iconPath)
    if ($null -eq $icon) {
        throw ('compiled executable does not expose an associated icon: ' + $iconPath)
    }
    try {
        if ($icon.Width -lt 32 -or $icon.Height -lt 32) {
            throw ('compiled executable icon has an invalid size: ' + $iconPath)
        }
        $bitmap = $icon.ToBitmap()
        try {
            # Check stable brand colors so a generic Windows icon cannot pass as the product icon.
            $centerPixel = $bitmap.GetPixel(10, 10)
            $barPixel = $bitmap.GetPixel(25, 16)
            if ([Math]::Abs([int]$centerPixel.R - 11) -gt 12 -or [Math]::Abs([int]$centerPixel.G - 19) -gt 12 -or [Math]::Abs([int]$centerPixel.B - 36) -gt 12) {
                throw ('compiled executable does not contain the product icon background: ' + $iconPath)
            }
            if ([Math]::Abs([int]$barPixel.R - 103) -gt 20 -or [Math]::Abs([int]$barPixel.G - 164) -gt 20 -or [Math]::Abs([int]$barPixel.B - 255) -gt 20) {
                throw ('compiled executable does not contain the product icon bars: ' + $iconPath)
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $icon.Dispose()
    }
}

'P2 embedded icon smoke: PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('CODEX_HOME', $oldCodexHome, 'Process')
    [Environment]::SetEnvironmentVariable('CLASH_MIXED_PROXY', $oldProxy, 'Process')
    [Environment]::SetEnvironmentVariable('STATUSBAR_DATA_HOME', $oldDataHome, 'Process')
    [Environment]::SetEnvironmentVariable('LOCALAPPDATA', $oldLocalAppData, 'Process')
    [Environment]::SetEnvironmentVariable('APPDATA', $oldAppData, 'Process')
    foreach ($registrySnapshot in $registrySnapshots) {
        Restore-RegistryValue $registrySnapshot
    }
    if (Test-Path -LiteralPath $isolatedHome) {
        Remove-Item -LiteralPath $isolatedHome -Recurse -Force -ErrorAction SilentlyContinue
    }
}
