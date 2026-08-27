# ChatGPT / Codex 额度状态栏教程

这是一条约 320×40 像素的 Windows 迷你状态栏，显示 ChatGPT/Codex 的 5 小时和 7 天动态额度。它不提供登录页面，只复用本机 Codex CLI 已完成的 ChatGPT OAuth 登录，不把额度功能限定为 Plus 计划。

## 设计优势

- **计划无关**：只要官方接口为当前账户返回额度窗口，就按实际数据展示，不假设必须是 Plus。
- **隐私优先**：复用本机 OAuth 会话，令牌只在内存中使用，不要求 API Key，不上传凭据。
- **信息密度高**：一眼查看 5 小时/7 天用量、进度条和下一次重置日期时间，不需要打开网页仪表盘。
- **轻量原生**：约 320×40 的 WinForms 窗口，单个 EXE 即可运行，不安装后台服务。
- **网络灵活**：默认使用系统代理或直连，需要时再配置 HTTP/HTTPS 代理。
- **可验证构建**：仓库提供源码、Windows 自带编译命令和 GitHub Actions 构建检查。

## 使用前准备

### 安装并登录 Codex CLI

在 PowerShell 或 Windows Terminal 中运行：

```powershell
codex login
```

登录成功后，Codex CLI 会在默认目录保存凭据：

```text
%USERPROFILE%\.codex\auth.json
```

如果你通过 `CODEX_HOME` 指定了其他目录，状态栏会优先读取：

```text
%CODEX_HOME%\auth.json
```

不要把这个文件上传、复制到 issue、聊天或公开仓库。

默认使用 Windows 系统代理；如果系统没有配置代理，则直接连接。只有你的网络确实需要 Clash Verge 或其他本地 HTTP/HTTPS 代理时，才在启动前设置：

```powershell
$env:CLASH_MIXED_PROXY = "http://127.0.0.1:7890"
Start-Process -FilePath .\SubscriptionStatus.exe -WorkingDirectory $PWD
```

环境变量只影响当前终端窗口，也可以在 Windows 系统环境变量中设置同名变量。

## 启动状态栏

进入仓库目录后，双击：

- `start-statusbar.cmd`
- 或 `launcher.vbs`

状态栏会出现在主屏幕工作区的右下角，任务栏上方。查询完成后会显示：

- `5h`：5 小时窗口已使用百分比和下一次重置时间
- `7d`：7 天窗口已使用百分比和下一次重置时间
- `OK`：最近一次官方查询成功

重置时间显示 `MM/dd HH:mm`，悬停状态栏可以查看脱敏账户后缀和错误原因。

状态栏窗口不会出现在 `Alt+Tab` 切换列表中。点击右上角 `×` 会隐藏到 Windows 通知区域，双击托盘图标可以恢复；只有在托盘或右键菜单选择“退出”才会结束程序。

## 开机自启与诊断

第一次启动会默认开启当前 Windows 用户的开机自启，不需要管理员权限。状态栏右键菜单中的“开机自启”可以随时关闭或重新开启；程序会记住你的选择，不会在下次启动强行改回。

右键选择“运行诊断”会先重新查询一次，再显示 OAuth 配置、计划、网络模式、额度窗口数量、最近查询时间和启动项状态。选择“复制诊断信息”可复制一份适合提交 Issue 的脱敏摘要，其中不包含 Token、账户 ID、代理地址或完整响应。

右键选择“设置”可以调整自动刷新周期（1/5/10/15/30/60 分钟）、背景实色或两档透明度、是否记住窗口位置，以及是否在额度达到阈值时弹出通知。透明度采用有限档位，保证文字和点击区域始终清晰可用。

## 操作方式

| 操作 | 方法 |
| --- | --- |
| 移动 | 拖动状态栏空白区域 |
| 手动刷新 | 点击圆形箭头 |
| 隐藏到托盘 | 点击 `×`，程序继续在通知区域运行 |
| 查看详情 | 鼠标悬停 |
| 设置、选项与诊断 | 右键状态栏或托盘图标 |

程序默认每 5 分钟自动刷新一次，也可以在设置中选择其他周期。拖动过窗口后，自动定位不会再抢回你选择的位置；开启“记住上次位置”后，下次启动会恢复该位置。

## 从源码编译

Windows 自带 .NET Framework C# 编译器，可以直接编译：

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = @(Get-ChildItem -File -Filter *.cs | Select-Object -ExpandProperty FullName)
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror /utf8output `
  /out:SubscriptionStatus.exe `
  /reference:System.dll `
  /reference:System.Core.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  /reference:System.Net.Http.dll `
  /reference:System.Web.Extensions.dll `
  $sources
```

编译完成后双击 `start-statusbar.cmd` 即可启动。仓库中的 GitHub Actions 也会在 Windows runner 上执行同一套编译检查。

## 本地回归

修改源码后可以运行仓库自带的 P0 smoke：

```powershell
$env:STATUSBAR_EXE = (Resolve-Path .\SubscriptionStatus.exe).Path
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1
```

它覆盖设置文件、启动项迁移、阈值通知和诊断脱敏，不会访问官方接口，也不会上传本机凭据。

## 常见问题

### 提示 OAuth 不可用

重新运行 `codex login`。状态栏只接受 ChatGPT OAuth 模式，不会读取 API Key 账单额度。

### 提示网络不可用或请求超时

程序默认使用 Windows 系统代理或直连。只有配置了 `CLASH_MIXED_PROXY` 时，才需要检查代理是否运行，并确认它是完整的 `http://` 或 `https://` 地址。

### 看不到窗口

先查看 Windows 通知区域，双击本项目图标即可恢复。由于程序使用单实例保护，再次运行 `start-statusbar.cmd` 不会创建第二个窗口；如果托盘图标也不可见，再用任务管理器结束 `SubscriptionStatus.exe` 后重新启动。

## 隐私边界

- 本项目不包含任何个人 `auth.json`、Token、账户 ID、桌面截图或真实额度数据。
- OAuth Token 只在内存中用于固定 HTTPS 请求，不写日志、不写回文件、不上传 GitHub。
- Tooltip 只显示账户 ID 的末四位。
- 右键复制的诊断摘要会主动隐藏 Token、账户 ID、代理地址和完整响应，提交前仍请自行快速检查。
- 官方后端接口可能变化，接口异常时程序只显示错误状态，不显示响应原文。
