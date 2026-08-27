# P0 Smoke Tests

`p0-settings.ps1` 使用已编译的 `SubscriptionStatus.exe` 做无网络回归，覆盖：

- 设置默认值、范围归一化、克隆和损坏文件备份
- Windows 当前用户启动项迁移、引号、启用和禁用
- 额度阈值提醒的首次基线、跨越触发和去重
- 诊断计划白名单与敏感字段脱敏

在仓库根目录编译后运行：

```powershell
$env:STATUSBAR_EXE = (Resolve-Path .\SubscriptionStatus.exe).Path
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1
```

脚本会临时修改当前用户的启动项并在结束时恢复原值，不会读取或上传 OAuth 凭据。
