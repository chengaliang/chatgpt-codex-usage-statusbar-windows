# 兼容旧的脚本入口：动态查询和 UI 统一由编译后的 WinForms 程序负责。
$exePath = Join-Path $PSScriptRoot 'SubscriptionStatus.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "状态栏程序不存在: $exePath"
}
Start-Process -FilePath $exePath -WorkingDirectory $PSScriptRoot
