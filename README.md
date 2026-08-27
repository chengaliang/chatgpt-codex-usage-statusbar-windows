# ChatGPT / Codex Usage Status Bar for Windows

[![Build Windows status bar](https://github.com/chengaliang/chatgpt-codex-usage-statusbar-windows/actions/workflows/build.yml/badge.svg)](https://github.com/chengaliang/chatgpt-codex-usage-statusbar-windows/actions/workflows/build.yml) [![GitHub stars](https://img.shields.io/github/stars/chengaliang/chatgpt-codex-usage-statusbar-windows?style=flat)](https://github.com/chengaliang/chatgpt-codex-usage-statusbar-windows/stargazers) [![GitHub issues](https://img.shields.io/github/issues/chengaliang/chatgpt-codex-usage-statusbar-windows?style=flat)](https://github.com/chengaliang/chatgpt-codex-usage-statusbar-windows/issues) [![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

## 中文概览

这是一个轻量的 Windows 桌面状态栏，用于查看 **ChatGPT / Codex CLI** 的官方动态额度。它复用本机 Codex CLI 的 ChatGPT OAuth 登录，通过系统网络连接访问 ChatGPT/Codex 后端，显示 5 小时和 7 天窗口的用量、进度和下一次重置时间。

项目按官方返回的额度窗口工作，不把功能限定为某一个订阅计划；OAuth 凭据只在内存中使用，不上传、不写日志。默认使用系统代理或直连，需要时再配置本地 HTTP/HTTPS 代理。

首次启动默认开启当前 Windows 用户的开机自启，不需要管理员权限；状态栏右键可关闭或重新开启。右键菜单还提供立即刷新、运行诊断、复制脱敏诊断信息和打开项目主页，遇到问题可以直接把诊断摘要贴到 Issue。

> Unofficial Windows desktop status bar for ChatGPT and Codex CLI usage limits. Reads local Codex OAuth credentials in memory, supports optional HTTP/HTTPS proxies, and keeps the UI at about 320×40 pixels.

## Why This Project

- **Plan-agnostic**: renders any official `rate_limit` windows returned for the signed-in ChatGPT/Codex account; it does not assume Plus-only access.
- **Privacy-first**: reuses the local Codex OAuth session in memory, never asks for API keys, and never uploads or logs credentials.
- **Useful at a glance**: shows 5-hour and 7-day usage, progress bars, local reset date/time, and a compact status indicator without opening a dashboard.
- **Tiny native footprint**: one WinForms executable around a 320×40 overlay, with no runtime installer or background service.
- **Network-flexible**: works with Windows system proxy settings or direct connection, with an explicit HTTP/HTTPS proxy option when needed.
- **Easy to verify**: source code, an in-box .NET compiler command, and a Windows Actions build are included in the repository.

## Features

- **Mini overlay**: approximately 320×40 pixels, pinned to the lower-right work area.
- **Official dynamic windows**: 5-hour and 7-day `used_percent` values with progress bars.
- **Next reset time**: each window shows the local `MM/dd HH:mm`; hover for account suffix and error details.
- **Plan-aware label**: shows the OAuth plan when available and uses a generic ChatGPT label when it is not; quota rendering is plan-agnostic.
- **Local OAuth only**: reads `%USERPROFILE%\.codex\auth.json` or `%CODEX_HOME%\auth.json`; never writes credentials back.
- **Optional proxy**: uses the Windows system proxy or a direct connection by default; set `CLASH_MIXED_PROXY` when a local Clash Verge or other HTTP/HTTPS proxy is needed.
- **Safe failure states**: expired OAuth, missing credentials, proxy errors and malformed responses become readable UI states instead of dumping response bodies.
- **Startup & diagnostics**: first launch enables current-user startup by default; right-click to toggle it, run a fresh health check, copy a redacted issue report, or open the project page.
- **No runtime dependency installer**: the checked-in executable can be launched directly, or rebuilt with the .NET Framework compiler already included in Windows.

## Quick Start

### 1. Sign in with Codex CLI

The status bar does not implement a login flow. Sign in once with the official Codex CLI in a terminal:

```powershell
codex login
```

The app expects ChatGPT OAuth mode and an `auth.json` under the Codex home directory.

## Download

For a ready-to-run Windows binary, download [`SubscriptionStatus.exe` from v0.2.0](https://github.com/chengaliang/chatgpt-codex-usage-statusbar-windows/releases/download/v0.2.0/SubscriptionStatus.exe).
The executable is also included in the repository for source review and offline use.

### 2. Launch the mini bar

Double-click [`start-statusbar.cmd`](start-statusbar.cmd) or [`launcher.vbs`](launcher.vbs). The first launch enables startup for the current Windows user; the app keeps one compact bar above the taskbar.
Without a custom proxy, the app uses Windows system proxy settings or a direct connection. If your network needs Clash Verge or another local HTTP/HTTPS proxy, set `CLASH_MIXED_PROXY` before launching:

```powershell
$env:CLASH_MIXED_PROXY = "http://127.0.0.1:7890"
Start-Process -FilePath .\SubscriptionStatus.exe -WorkingDirectory $PWD
```

| Action | How |
| --- | --- |
| Move | Drag any empty part of the bar |
| Refresh | Click the circular-arrow icon |
| Close | Click the `×` icon |
| Details | Hover over the bar |
| Options | Right-click the bar for startup and diagnostics |

The bar refreshes automatically every five minutes.

## Build From Source

A Windows machine with .NET Framework 4.5+ can compile the WinForms source with the in-box C# compiler:

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror /utf8output `
  /out:SubscriptionStatus.exe `
  /reference:System.dll `
  /reference:System.Core.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  /reference:System.Net.Http.dll `
  /reference:System.Web.Extensions.dll `
  .\SubscriptionStatus.cs
```

The repository also includes a GitHub Actions Windows build so source changes are compiled on every push and pull request.

## Privacy and Security

- **Never upload `auth.json`**. It contains OAuth credentials and is not part of this repository.
- The access token is read into memory only and sent as a Bearer token to the fixed HTTPS ChatGPT/Codex usage endpoint.
- No token, response body, account ID or exception stack is written to a log file or displayed in the bar.
- The tooltip masks the account ID and only shows its last four characters.
- This project does not ask for, store or proxy OpenAI API keys.
- The usage endpoint is a ChatGPT/Codex backend contract used by compatible clients and may change without notice. This project is not affiliated with OpenAI.

Read the step-by-step Chinese tutorial in [`docs/QUICKSTART.zh-CN.md`](docs/QUICKSTART.zh-CN.md).

## Troubleshooting

### `OAuth 不可用`

Run `codex login` again and confirm that the CLI is using ChatGPT OAuth mode. Do not paste the contents of `auth.json` into an issue.

### `网络不可用` or `请求超时`

The app uses Windows system proxy settings or a direct connection by default. If your network needs a local proxy, start it and set `CLASH_MIXED_PROXY` to the correct HTTP/HTTPS URL.

### The bar is not visible

Run [`start-statusbar.cmd`](start-statusbar.cmd) again. The app positions itself inside the primary work area and rechecks its position after the first query. Dragging the bar disables automatic repositioning.

### Startup or diagnostics

Right-click the bar to turn current-user startup on or off. **Run diagnostics** refreshes the official endpoint and shows only safe status details. **Copy diagnostic info** is redacted for issue reports and does not include tokens, account IDs, proxy addresses or full responses.

## 持续维护与反馈

项目目标是长期跟进 ChatGPT/Codex 官方额度接口的变化，定期修复可复现问题，并持续优化兼容性、稳定性和使用体验。欢迎通过 [Issues](https://github.com/chengaliang/chatgpt-codex-usage-statusbar-windows/issues) 提交：

- OAuth、额度窗口、重置时间或网络连接问题
- Windows 版本、显示缩放和窗口布局问题
- 清晰可复现的功能建议和改进想法

提交问题前请先阅读 [`CONTRIBUTING.md`](CONTRIBUTING.md)。反馈中不要粘贴 `auth.json`、OAuth Token、账户 ID、完整响应或个人截图；也可以先在状态栏右键选择“复制诊断信息”，提交已脱敏摘要。每次发布会在 Release 中记录变更。项目对你有帮助时，欢迎点 Star、Watch 或分享给有同样需求的人，这能帮助维护工作持续获得优先级。

## Search Keywords

`chatgpt usage limits` · `chatgpt quota` · `chatgpt plus quota` · `chatgpt pro quota` · `chatgpt team quota` · `codex-cli` · `codex quota` · `subscription status bar` · `windows desktop widget` · `windows startup` · `diagnostics` · `oauth` · `clash-verge` · `usage monitor`

If this saves you time, a ⭐ on GitHub helps other users find the project.
