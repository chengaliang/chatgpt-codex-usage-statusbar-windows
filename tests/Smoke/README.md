# Windows Smoke Tests

`p0-settings.ps1` 使用已编译的 `SubscriptionStatus.exe` 做无网络回归，覆盖：

- 设置默认值、范围归一化、克隆和损坏文件备份
- Windows 当前用户启动项迁移、引号、启用和禁用
- 额度阈值、周期重置和预计耗尽提醒的首次基线、触发和去重
- 诊断计划白名单与敏感字段脱敏

`p1-usage-hub.ps1` 继续覆盖：

- Provider/统一快照状态、动态计划白名单和失败保留缓存
- 缓存往返、损坏备份、分离清理、历史去重、脱敏 CSV 导出与敏感字段扫描
- 详情/设置/诊断中心窗口的 `ShowInTaskbar=false` 与 `AutoScaleMode=Dpi`
- 诊断检查项、最近成功年龄、下一步建议、安全摘要复制、趋势导出、扩展状态和动态布局
- 当前重置周期趋势回归、旧周期隔离和过期缓存不预测边界
- 动效设置默认值、克隆持久化和关闭动效后的静态回退边界
- GitHub 更新服务版本元数据和 SHA-256 正负样本

在仓库根目录编译后运行：

```powershell
$env:STATUSBAR_EXE = (Resolve-Path .\dist\SubscriptionStatus.exe).Path
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p1-usage-hub.ps1
```

脚本会临时修改当前用户的启动项并在结束时恢复原值，不会读取或上传 OAuth 凭据。
