param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

# 生成多尺寸 PNG payload 的 ICO，避免依赖设计软件或外部图标工具。
# 图标只包含产品视觉元素，不包含账户、OAuth 或本机路径等信息。

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot '..\assets\ChatGPTCodexUsageStatusBar.ico'
}

Add-Type -AssemblyName System.Drawing

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

function New-RoundedPath {
    param(
        [System.Drawing.Rectangle]$Bounds,
        [int]$Radius
    )

    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList @($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $resources = New-Object System.Collections.Generic.List[IDisposable]
    $path = $null
    $stream = New-Object System.IO.MemoryStream
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $margin = [Math]::Max(1, [int]($Size * 0.08))
        $radius = [Math]::Max(2, [int]($Size * 0.2))
        $bounds = New-Object System.Drawing.Rectangle -ArgumentList @($margin, $margin, ($Size - ($margin * 2)), ($Size - ($margin * 2)))
        $path = New-RoundedPath $bounds $radius
        $backgroundBrush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 11, 19, 36))
        $null = $resources.Add($backgroundBrush)
        $graphics.FillPath($backgroundBrush, $path)

        # 左侧青色弧线表达可持续监控，右侧阶梯表达额度窗口和趋势。
        $strokeWidth = [Math]::Max(1.5, $Size * 0.12)
        $arcPen = New-Object System.Drawing.Pen -ArgumentList @([System.Drawing.Color]::FromArgb(255, 73, 218, 190), [single]$strokeWidth)
        $null = $resources.Add($arcPen)
        $arcPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arcPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arcBox = New-Object System.Drawing.RectangleF -ArgumentList @([single]($Size * 0.22), [single]($Size * 0.23), [single]($Size * 0.39), [single]($Size * 0.54))
        $graphics.DrawArc($arcPen, $arcBox, 55, 265)

        $barBrush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 103, 164, 255))
        $null = $resources.Add($barBrush)
        $barWidth = [Math]::Max(2, [int]($Size * 0.1))
        $barGap = [Math]::Max(2, [int]($Size * 0.045))
        $barBottom = [int]($Size * 0.77)
        $barHeights = @([int]($Size * 0.2), [int]($Size * 0.34), [int]($Size * 0.5))
        for ($index = 0; $index -lt $barHeights.Count; $index++) {
            $x = [int]($Size * 0.59) + (($barWidth + $barGap) * $index)
            $height = [Math]::Max(2, $barHeights[$index])
            $barBounds = New-Object System.Drawing.Rectangle -ArgumentList @($x, ($barBottom - $height), $barWidth, $height)
            $barPath = New-RoundedPath $barBounds ([Math]::Max(1, [int]($barWidth * 0.35)))
            $graphics.FillPath($barBrush, $barPath)
            $barPath.Dispose()
        }

        $dotBrush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 255, 184, 92))
        $null = $resources.Add($dotBrush)
        $dotSize = [Math]::Max(2, [int]($Size * 0.11))
        $graphics.FillEllipse($dotBrush, [single]($Size * 0.75), [single]($Size * 0.2), $dotSize, $dotSize)

        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,([byte[]]$stream.ToArray())
    }
    finally {
        if ($null -ne $path) { $path.Dispose() }
        foreach ($resource in $resources) { $resource.Dispose() }
        $stream.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 32, 64, 256)
$payloads = @($sizes | ForEach-Object { New-IconPng $_ })
$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter -ArgumentList $icoStream
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $payloadOffset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $payload = [byte[]]$payloads[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$payload.Length)
        $writer.Write([uint32]$payloadOffset)
        $payloadOffset += $payload.Length
    }

    foreach ($payload in $payloads) {
        $writer.Write([byte[]]$payload)
    }
    [IO.File]::WriteAllBytes($outputFullPath, $icoStream.ToArray())
}
finally {
    $writer.Dispose()
    $icoStream.Dispose()
}

$hash = (Get-FileHash -LiteralPath $outputFullPath -Algorithm SHA256).Hash.ToUpperInvariant()
Write-Output "Generated $outputFullPath"
Write-Output "SHA256 $hash"
