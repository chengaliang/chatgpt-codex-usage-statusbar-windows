$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$exePath = $env:STATUSBAR_EXE
if ([string]::IsNullOrWhiteSpace($exePath)) {
    $exePath = Join-Path $PSScriptRoot '..\..\dist\SubscriptionStatus.exe'
}
$exePath = (Resolve-Path -LiteralPath $exePath).Path
$rootPath = Join-Path (Split-Path -Parent (Split-Path -Parent $exePath)) 'SubscriptionStatus.exe'

function Get-StatusProcesses {
    param([string]$Path)

    @(
        Get-CimInstance Win32_Process -Filter "Name = 'SubscriptionStatus.exe'" |
            Where-Object { $_.ExecutablePath -eq $Path }
    )
}

$existing = Get-StatusProcesses $exePath
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
    } while ($null -ne $process -and -not $process.HasExited -and (Get-Date) -lt $deadline -and $process.MainWindowHandle -eq 0)

    if ($null -eq $process -or $process.HasExited) {
        throw 'status bar process exited during startup'
    }
    if (-not $process.Responding) {
        throw 'status bar process is not responding after startup'
    }
    if ($process.MainWindowHandle -eq 0) {
        throw 'status bar did not create its visible window handle'
    }

    $second = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath) -PassThru
    if (-not $second.WaitForExit(4000)) {
        throw 'second launch did not return after handing off to the existing instance'
    }
    Start-Sleep -Milliseconds 300
    $instancesAfterSecondLaunch = Get-StatusProcesses $exePath
    if (@($instancesAfterSecondLaunch).Count -ne 1) {
        $details = ($instancesAfterSecondLaunch | ForEach-Object { $_.ProcessId.ToString() + ':' + [string]$_.ExecutablePath }) -join ', '
        throw ('second launch created a duplicate status bar process (count=' + @($instancesAfterSecondLaunch).Count + ', ' + $details + ')')
    }
    'P2 direct launch and duplicate-instance smoke: PASS'
}
finally {
    if ($null -ne $second -and -not $second.HasExited) {
        Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $first -and -not $first.HasExited) {
        Stop-Process -Id $first.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($process in (Get-StatusProcesses $exePath)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

if (Test-Path -LiteralPath $rootPath -PathType Leaf) {
    $existingAfterCleanup = Get-StatusProcesses $exePath
    if (@($existingAfterCleanup).Count -gt 0) {
        throw 'compatibility launcher smoke started with a stale process'
    }

    $shim = $null
    try {
        $shim = Start-Process -FilePath $rootPath -WorkingDirectory (Split-Path -Parent $rootPath) -PassThru
        Start-Sleep -Seconds 2
        $shimProcesses = Get-StatusProcesses $exePath
        if (@($shimProcesses).Count -ne 1) {
            throw 'root compatibility launcher did not start the dist executable'
        }
        'P2 root compatibility launcher smoke: PASS'
    }
    finally {
        if ($null -ne $shim -and -not $shim.HasExited) {
            Stop-Process -Id $shim.Id -Force -ErrorAction SilentlyContinue
        }
        foreach ($process in (Get-StatusProcesses $exePath)) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
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
        if ($icon.Width -lt 16 -or $icon.Height -lt 16) {
            throw ('compiled executable icon has an invalid size: ' + $iconPath)
        }
    }
    finally {
        $icon.Dispose()
    }
}

'P2 embedded icon smoke: PASS'
