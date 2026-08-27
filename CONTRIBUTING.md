# 贡献指南

感谢你帮助改进 ChatGPT / Codex Usage Status Bar。这个项目优先处理可复现的问题、官方接口兼容性和轻量桌面体验。

## 提交 Issue

- Bug 请说明 Windows 版本、复现步骤、预期结果和实际结果。
- 网络问题请说明是否配置了 `CLASH_MIXED_PROXY`，不要提交代理账号或订阅链接。
- 功能建议请描述使用场景和希望解决的问题。
- 绝不要粘贴 `auth.json`、OAuth Token、账户 ID、完整接口响应或包含个人信息的截图。
- 优先使用状态栏右键菜单的“复制诊断信息”提供脱敏摘要；提交前仍请确认没有附带个人路径或截图。

## 本地验证

修改后使用 README 中的 .NET Framework `csc.exe` 命令编译，并确认 `SubscriptionStatus.exe` 能启动。提交前运行：

```powershell
git diff --check
```

## Pull Request

- 保持改动聚焦，说明行为变化和验证方式。
- 新增代码应保留中文业务注释，并避免记录凭据或响应原文。
- UI 调整请说明窗口尺寸、缩放和交互验证结果。
- 不要提交本地 `auth.json`、日志、截图或构建临时文件。
