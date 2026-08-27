# ChatGPT / Codex 额度状态栏教程

这是一条约 370×56 像素的 Windows 迷你状态栏，显示 ChatGPT/Codex 官方返回的动态额度。它不提供登录页面，只复用本机 Codex CLI 已完成的 ChatGPT OAuth 登录，不把额度功能限定为 Plus 计划；点击空白区域或右侧展开按钮即可打开带环形额度卡、趋势和操作中心的 Usage Hub 大屏。

## 设计优势

- **计划无关**：只要官方接口为当前账户返回额度窗口，就按实际数据展示，不假设必须是 Plus。
- **隐私优先**：复用本机 OAuth 会话，令牌只在内存中使用，不要求 API Key，不上传凭据。
- **信息密度高**：一眼查看 5 小时/7 天用量、进度条和下一次重置日期时间，不需要打开网页仪表盘。
- **轻量原生**：约 370×56 的 WinForms 入口，单个 EXE 即可运行；需要时再打开 Usage Hub，不安装后台服务。
- **网络灵活**：默认使用系统代理或直连，需要时再配置 HTTP/HTTPS 代理。
- **可验证构建**：仓库提供源码、Windows 自带编译命令和 GitHub Actions 构建检查。
- **断网可读**：最近一次成功数据会安全缓存，网络或 OAuth 暂时失败时明确标记缓存而不是清空数字。
- **可维护**：详情窗口、诊断摘要、更新检查、SHA-256 校验和本地数据清理均有独立入口。

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

状态栏会出现在启动时所在显示器的工作区右下角，任务栏上方。查询完成后会显示：

- `5h`：5 小时窗口已使用百分比和下一次重置时间
- `7d`：7 天窗口已使用百分比和下一次重置时间
- `实时`：最近一次官方查询成功

如果官方返回其他窗口，状态栏会自动回退显示前两个窗口，详情窗口会列出全部窗口。

重置时间显示 `MM/dd HH:mm`，悬停状态栏可以查看缓存时间和安全错误提示。

状态栏窗口不会出现在 `Alt+Tab` 切换列表中。状态栏会以低频状态呼吸和进度高光保持可见反馈；点击空白区域或展开按钮进入 Usage Hub 后，可以看到环形进度、趋势线、扫描线和刷新旋转。点击右上角 `×` 会隐藏到 Windows 通知区域，双击托盘图标可以恢复；只有在托盘或右键菜单选择“退出”才会结束程序。

## 开机自启与诊断

第一次启动会默认开启当前 Windows 用户的开机自启，不需要管理员权限。状态栏右键菜单中的“开机自启”可以随时关闭或重新开启；程序会记住你的选择，不会在下次启动强行改回。

右键选择“诊断中心”会先重新查询一次，再显示 OAuth、网络代理、额度查询、窗口数量、本地缓存、历史和启动项等固定检查项，并给出下一步建议。窗口内可以重新检查或复制安全摘要，其中不包含 Token、账户 ID、代理地址或完整响应。

右键选择“设置”可以调整自动刷新周期（1/5/10/15/30/60 分钟）、历史保留周期（7/30/90 天）、跟随系统/深色/浅色/石墨主题、背景实色或两档透明度、开机后的首次查询延迟、是否在启动时检查更新（仅提示，不自动安装）、是否记住窗口位置、是否开启平滑动效，以及是否在额度达到阈值时弹出通知。平滑动效默认开启，会让进度条和刷新图标自然过渡；关闭后会立即回到静态绘制。

点击空白区域、右侧展开按钮或右键选择“打开 Usage Hub”，可以查看所有额度窗口、剩余百分比、重置倒计时、最近成功时间和按设置保留 7/30/90 天的本地百分比趋势。大屏底部的刷新、设置、诊断、项目主页和回到状态栏按钮会复用主窗口的安全边界；选择“清除本地缓存与历史”只删除本项目数据，不会删除 Codex 登录。

## 操作方式

| 操作 | 方法 |
| --- | --- |
| 移动 | 拖动状态栏空白区域 |
| 手动刷新 | 点击圆形箭头 |
| 隐藏到托盘 | 点击 `×`，程序继续在通知区域运行 |
| 打开 Usage Hub | 点击空白区域、展开按钮或右键选择“打开 Usage Hub” |
| 设置、选项与诊断 | 右键状态栏或托盘图标 |

程序默认每 5 分钟自动刷新一次，也可以在设置中选择其他周期。开机自启时可设置 0/5/15/30 秒延迟，让代理和 Codex CLI 有时间完成初始化；开启“启动时检查更新”后只会在首次查询完成后提示公开 Release，不会自动下载或替换文件。拖动过窗口后，自动定位不会再抢回你选择的位置；开启“记住上次位置”后，下次启动会恢复该位置。右键“检查更新”只读取公开 Release 元数据，确认后打开 GitHub 页面，不会后台覆盖运行中的文件。

## 从源码编译

Windows 自带 .NET Framework C# 编译器，可以直接编译：

```powershell
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -File -Filter *.cs | Select-Object -ExpandProperty FullName)
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
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

修改源码后可以运行仓库自带的 P0/P1 smoke：

```powershell
$env:STATUSBAR_EXE = (Resolve-Path .\SubscriptionStatus.exe).Path
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p1-usage-hub.ps1
```

它覆盖设置文件、启动项迁移、阈值通知、诊断脱敏、缓存/历史隐私、Usage Hub/详情/设置 DPI 标志、自绘面板入口和 SHA-256 校验，不会访问官方额度接口，也不会上传本机凭据。

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
- 本地缓存和按设置保留 7/30/90 天的历史只保存 Provider、窗口秒数、百分比和时间，可从右键菜单一键删除。
- 更新检查只读取公开 GitHub Release 元数据，确认后打开下载页，不会自动替换正在运行的程序。
- 官方后端接口可能变化，接口异常时程序只显示错误状态，不显示响应原文。
