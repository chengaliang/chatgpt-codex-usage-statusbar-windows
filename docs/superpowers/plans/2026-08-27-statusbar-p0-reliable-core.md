# ChatGPT/Codex Status Bar P0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将当前单文件状态栏升级为可设置、可恢复、可诊断且不出现在 Alt+Tab 的 Windows 常驻工具，同时保持本地 OAuth、免安装和隐私边界。

**Architecture:** 保留 WinForms/.NET Framework 4.5 运行方式，先把设置、启动项、刷新调度、通知和托盘从窗口绘制逻辑中抽出为小模块。状态栏仍是 320×40 的主视图，设置写入 `%LOCALAPPDATA%`，启动命令写入 HKCU Run，OAuth 只进入 Provider 请求路径。

**Tech Stack:** C#、.NET Framework 4.5+、WinForms、`JavaScriptSerializer`、PowerShell smoke tests、GitHub Actions Windows runner。

**Spec:** `docs/superpowers/specs/2026-08-27-chatgpt-codex-statusbar-product-design.md`

## Global Constraints

- OAuth Token、refresh token、id token、账户 ID、代理完整 URI、完整响应和异常堆栈不得进入设置、缓存、日志、剪贴板或 Release。
- 未设置 `CLASH_MIXED_PROXY` 时使用 Windows 系统代理或直连；只有显式 HTTP/HTTPS URI 才创建自定义代理。
- 首次运行默认开启当前用户开机自启；配置标记不得写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 保持约 320×40 像素状态栏、`ShowInTaskbar=false` 和免安装单 EXE；窗口必须通过 `WS_EX_TOOLWINDOW` 隐藏 Alt+Tab。
- 刷新周期只允许 1、5、10、15、30、60 分钟，默认 5 分钟；请求不得并行。
- 背景样式只提供实色、约 85% 半透明和约 65% 高透明；本轮不做纯透明 layered window。
- 新增业务代码必须有说明职责和设计取舍的中文注释；所有异步 UI 入口必须有最后一道异常边界。
- 每个任务完成后运行指定 smoke test，并使用 Conventional Commit 的中文提交信息。

---

### Task 1: 添加设置模型与安全 JSON 存储

**Files:**
- Create: `AppSettings.cs`
- Create: `SettingsStore.cs`
- Create: `tests/Smoke/p0-settings.ps1`
- Modify: `.gitignore`

**Interfaces:**
- Produces `BackgroundStyle` enum with `Opaque`, `SemiTransparent`, `HighTransparency`.
- Produces `AppSettings` with `RefreshIntervalMinutes`, `AutoStartEnabled`, `BackgroundStyle`, `NotificationsEnabled`, `NotificationThresholdPercent`, `RestorePosition`, `Clone()` and `Normalize()`.
- Produces `SettingsStore.Load()` and `SettingsStore.Save(AppSettings settings)` using `%LOCALAPPDATA%\ChatGPTCodexUsageStatusBar\settings.json`.

- [ ] **Step 1: 写设置模型的失败测试**

在 `tests/Smoke/p0-settings.ps1` 中加载编译后的程序集，反射创建 `AppSettings`，将刷新周期设为 `2`、阈值设为 `150`，调用 `Normalize()`，断言周期回到 `5`、阈值回到 `80`，并断言 `Clone()` 不共享引用。

```powershell
$settingsType = $assembly.GetType('AppSettings')
$settings = [Activator]::CreateInstance($settingsType)
$settings.RefreshIntervalMinutes = 2
$settings.NotificationThresholdPercent = 150
$settings.Normalize()
if ($settings.RefreshIntervalMinutes -ne 5) { throw 'invalid refresh interval was not normalized' }
if ($settings.NotificationThresholdPercent -ne 80) { throw 'invalid threshold was not normalized' }
$clone = $settings.Clone()
if ([object]::ReferenceEquals($settings, $clone)) { throw 'settings clone shares the source object' }
```

- [ ] **Step 2: 运行测试确认当前源码失败**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1`

Expected: FAIL，因为当前程序集没有 `AppSettings` 类型。

- [ ] **Step 3: 实现 `AppSettings` 和归一化规则**

使用 .NET Framework 4.5 可用的普通构造函数和自动属性，不使用较新的初始化器语法。默认值必须是 5 分钟、开机自启开启、实色、通知关闭、80% 阈值和恢复位置开启。`Normalize()` 只接受六个预设周期，透明度由枚举决定，阈值限制在 50 到 100。

- [ ] **Step 4: 实现 `SettingsStore` 的原子保存与损坏回退**

`Load()` 在文件不存在、JSON 损坏或字段类型错误时返回默认设置；损坏文件先改名为同目录下的 `settings.json.bak`，不得阻止程序启动。`Save()` 先写 `settings.json.tmp`，成功后替换目标文件；目标不存在时移动临时文件。设置文件只包含模型字段，不允许写入 OAuth 或网络响应。

- [ ] **Step 5: 添加路径忽略并运行通过测试**

将 `settings.json`、`settings.json.tmp`、`settings.json.bak` 加入 `.gitignore`，运行本地 csc 编译和 `p0-settings.ps1`，Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add AppSettings.cs SettingsStore.cs tests/Smoke/p0-settings.ps1 .gitignore
git commit -m "feat: 增加本地设置存储模型"
```

### Task 2: 抽出启动项管理并完成旧值迁移

**Files:**
- Create: `StartupManager.cs`
- Modify: `SubscriptionStatus.cs:617-755`（删除窗口内启动项常量和私有方法，改为调用管理器）
- Modify: `AppSettings.cs`
- Modify: `tests/Smoke/p0-settings.ps1`

**Interfaces:**
- Produces `StartupManager(string executablePath)`.
- Produces `bool TryGetEnabled(out bool enabled, out string error)`.
- Produces `bool TrySetEnabled(bool enabled, out string error)`.
- Consumes `AppSettings.AutoStartEnabled` only as用户偏好，不把标记写入 Run 键。

- [ ] **Step 1: 为启动项迁移写失败测试**

在 smoke test 中清理测试值后写入旧版 `ChatGPTCodexUsageStatusBarConfigured=1`，再通过反射创建 `StartupManager` 并调用 `TryGetEnabled`；断言 Run 键不再包含旧 marker，独立配置键 `HKCU\Software\ChatGPTCodexUsageStatusBar` 含 marker，实际 EXE 值仍存在。

- [ ] **Step 2: 实现 `StartupManager`**

把当前已验证的迁移逻辑移入独立类：首次运行写入带引号的绝对 EXE 命令并在独立配置键记录 marker；检测到旧 Run marker 时先复制到独立键再删除；关闭自启只删除 EXE 值，保留配置 marker；所有 Registry 异常返回稳定中文错误，不抛到 UI。

- [ ] **Step 3: 接入 `StatusWindow` 并删除重复逻辑**

窗口只保留 `StartupManager` 字段和 `autoStartEnabled/autoStartError` 状态；构造函数加载管理器状态；右键切换调用 `TrySetEnabled`。旧常量、`InitializeAutoStart()`、`TrySetAutoStart()`、`GetStartupCommand()` 必须删除，避免两个实现产生不一致。

- [ ] **Step 4: 运行注册表迁移回归**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1`

Expected: 首次运行只有一个有效 Run EXE 值；旧 marker 被清理；关闭后重启保持关闭；重新开启后恢复 EXE 值；没有名为 `1` 的 Run 值。

- [ ] **Step 5: 提交**

```powershell
git add StartupManager.cs AppSettings.cs SubscriptionStatus.cs tests/Smoke/p0-settings.ps1
git commit -m "refactor: 独立管理用户启动项"
```

### Task 3: 配置刷新调度与安全窗口样式

**Files:**
- Create: `RefreshScheduler.cs`
- Modify: `SubscriptionStatus.cs:537-1248`
- Modify: `tests/Smoke/p0-settings.ps1`

**Interfaces:**
- Produces `RefreshScheduler(int intervalMinutes, Func<Task> refreshAction)` with `Start()`, `Stop()`, `SetInterval(int minutes)`, `Dispose()` and `IsRunning`.
- Consumes `AppSettings.RefreshIntervalMinutes` and `AppSettings.BackgroundStyle`.
- Produces right-click “刷新周期” and “背景样式” menu items with one checked value each.

- [ ] **Step 1: 写调度器失败测试**

通过一个 `TaskCompletionSource<bool>` 和反射构造调度器，调用 `SetInterval(2)` 后断言实际 `IntervalMinutes` 为 5；调用 `Stop()` 后断言 `IsRunning=false`。当前代码没有该类型，测试应失败。

- [ ] **Step 2: 实现调度器**

调度器内部使用 WinForms `Timer`，在 UI 线程触发 `Func<Task>`；`SetInterval` 先停止旧计时器，再按归一化分钟数设置毫秒间隔并重新启动；刷新回调本身由窗口的安全异步边界包裹，不能并行执行。

- [ ] **Step 3: 接入窗口设置和即时保存**

窗口构造时从 `SettingsStore` 读取设置，使用 `RefreshScheduler` 替换当前固定 5 分钟 Timer。刷新周期菜单使用六个预设，点击后更新模型、保存设置、重建调度间隔并刷新菜单勾选状态；保存失败只提示一次并保留当前运行值。

- [ ] **Step 4: 隐藏 Alt+Tab**

在现有 `CreateParams` 中加入 `const int WsExToolWindow = 0x00000080; parameters.ExStyle |= WsExToolWindow;`，保留 `ShowInTaskbar=false`。不要加入 `WS_EX_NOACTIVATE`，避免破坏拖动、刷新和右键交互。

- [ ] **Step 5: 实现三档透明度并保留可读性**

增加 `ApplyBackgroundStyle()`：实色设置 `Opacity=1.0`，半透明设置 `Opacity=0.85`，高透明设置 `Opacity=0.65`；背景渐变、边框、文字和命中区域保持不变。纯透明不进入本轮实现。样式菜单点击后立即重绘、保存并更新勾选。

- [ ] **Step 6: 运行窗口回归与提交**

启动程序后检查进程响应、右键菜单周期和透明度入口；使用 `Alt+Tab` 人工确认没有状态栏条目；运行 smoke test 与 csc `/warnaserror`。

```powershell
git add RefreshScheduler.cs SubscriptionStatus.cs tests/Smoke/p0-settings.ps1
git commit -m "feat: 支持刷新周期和可选透明度"
```

### Task 4: 增加托盘恢复与设置窗口

**Files:**
- Create: `TrayController.cs`
- Create: `SettingsForm.cs`
- Modify: `SubscriptionStatus.cs`
- Modify: `README.md`
- Modify: `docs/QUICKSTART.zh-CN.md`

**Interfaces:**
- Produces `TrayController(Action show, Action refresh, Action settings, EventHandler diagnostics, Action openProject, Action exit)` and a five-`Action` compatibility overload; both expose `Dispose()`.
- Produces modal `SettingsForm(AppSettings currentSettings)`; `DialogResult.OK` 返回 `AppSettings Result`，取消不写入。
- Consumes `StatusWindow` callbacks without直接访问 OAuth 或窗口私有字段。

- [ ] **Step 1: 写托盘/设置失败测试**

反射加载程序集并断言存在 `TrayController`、`SettingsForm`；创建设置窗体后断言包含刷新周期、背景样式、开机自启、通知和取消/应用控件。当前程序集应失败。

- [ ] **Step 2: 实现 `TrayController`**

使用 `NotifyIcon` 和 `SystemIcons.Application`，菜单提供“显示状态栏、立即刷新、设置、运行诊断、打开项目主页、退出”。托盘对象负责资源释放；事件回调只回到窗口协调器，不能在托盘类中读取凭据。

- [ ] **Step 3: 实现紧凑 `SettingsForm`**

使用 `TableLayoutPanel`、`ComboBox`、`CheckBox`、`NumericUpDown` 和按钮，固定合理最小尺寸；刷新周期和背景样式使用下拉框，开机自启/通知使用复选框，阈值限制 50–100。打开时克隆设置，点击取消直接返回 `Cancel`，点击应用先 `Normalize()` 再返回克隆结果。

- [ ] **Step 4: 接入隐藏/显示生命周期**

状态栏关闭按钮改为 `HideToTray()`，不终止进程；托盘显示回调调用 `Show()`、`WindowState=Normal` 和 `Activate()`；只有托盘“退出”调用 `Close()`。窗口首次运行仍显示，隐藏后 Alt+Tab 和任务栏均不出现。

- [ ] **Step 5: 同步文档和人工回归**

README 和中文教程加入托盘恢复、设置窗口、Alt+Tab 隐藏、刷新周期和三档透明度说明；运行程序确认隐藏后可从托盘恢复，设置取消不会写入，应用后重启仍保持。

- [ ] **Step 6: 提交**

```powershell
git add TrayController.cs SettingsForm.cs SubscriptionStatus.cs README.md docs/QUICKSTART.zh-CN.md
git commit -m "feat: 增加托盘恢复和设置窗口"
```

### Task 5: 增加阈值通知和可操作诊断

**Files:**
- Create: `NotificationEvaluator.cs`
- Create: `DiagnosticsService.cs`
- Modify: `SubscriptionStatus.cs`
- Modify: `AppSettings.cs`
- Modify: `README.md`
- Modify: `SECURITY.md`
- Modify: `.github/ISSUE_TEMPLATE/bug_report.md`

**Interfaces:**
- Produces `NotificationEvaluator.Evaluate(QuotaSnapshot snapshot, int thresholdPercent)` returning zero或多个 `UsageNotification`（受控标题和正文），并按有效 `reset_at` 识别新周期。
- Produces `DiagnosticsService.Build(QuotaSnapshot snapshot, string credentialDiagnostic, string proxyDiagnostic, bool autoStartEnabled, bool hasAutoStartError, AppSettings settings)` returning a redacted string.
- Consumes `NotifyIcon.ShowBalloonTip` only after user enabled notifications.

- [ ] **Step 1: 写通知和诊断脱敏失败测试**

构造 79%、80%、95% 窗口快照，断言只在穿越 80% 时生成通知；构造计划名 `X\r\nSECRET` 和代理值 `http://127.0.0.1:7890`，断言诊断不含 `SECRET`、账户 ID、完整代理地址或响应原文。

- [ ] **Step 2: 实现通知边沿判断**

`NotificationEvaluator` 只保留每个窗口最近一次百分比和重置时间；从低于阈值到达到阈值时生成一次通知，重复刷新不重复弹出；窗口重置后清理该窗口状态。通知默认关闭，正文只写窗口名称、百分比和本地时间。

- [ ] **Step 3: 抽出 `DiagnosticsService` 白名单**

诊断只允许系统版本、.NET 版本、进程位数、OAuth 是否可读、白名单计划名（GPT Plus 或 ChatGPT）、代理协议、查询状态、窗口数量、最近查询时间、启动状态、刷新周期、背景样式和通知开关。禁止拼接账户标签、路径、原始异常、Token、URI 和未经验证的计划文本。

- [ ] **Step 4: 接入托盘/右键“运行诊断”和复制**

运行诊断先走安全刷新，再用 `DiagnosticsService` 显示可复制文本；复制按钮捕获剪贴板异常并给出稳定提示。报告末尾明确“未包含 Token、账户 ID、代理地址和完整响应”。

- [ ] **Step 5: 更新文档和模板**

补充通知默认关闭、阈值触发、诊断白名单和提交 Issue 的复制步骤；Security 文档说明诊断字段级白名单，Bug 模板优先请求脱敏报告。

- [ ] **Step 6: 运行脱敏烟测并提交**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1`

Expected: 通知边沿、计划注入、代理隐藏、右键诊断和剪贴板路径全部 PASS。

```powershell
git add NotificationEvaluator.cs DiagnosticsService.cs AppSettings.cs SubscriptionStatus.cs README.md SECURITY.md .github/ISSUE_TEMPLATE/bug_report.md tests/Smoke/p0-settings.ps1
git commit -m "feat: 增加阈值通知和安全诊断"
```

### Task 6: 更新构建、烟测和发布文档

**Files:**
- Modify: `.github/workflows/build.yml`
- Modify: `tests/Smoke/p0-settings.ps1`
- Modify: `README.md`
- Modify: `docs/QUICKSTART.zh-CN.md`
- Modify: `CHANGELOG.md`
- Create: `tests/Smoke/README.md`

**Interfaces:**
- CI compiles every root `*.cs` file (excluding test scripts) with `/warnaserror`.
- Smoke script accepts an executable path through `$env:STATUSBAR_EXE` and returns non-zero on any failed assertion.

- [ ] **Step 1: 固化 smoke test 启动与单实例检查**

脚本启动 `$env:STATUSBAR_EXE`，等待最多 5 秒确认进程响应；再启动第二个实例并断言同路径进程数量仍为 1；测试结束只停止本次启动的同路径进程，不删除用户其他程序。

- [ ] **Step 2: 更新 Actions 编译所有源文件**

在 workflow 中用 PowerShell 收集 `Get-ChildItem -Filter *.cs` 的根目录源文件，传给 csc；保留现有系统程序集引用、`/warnaserror` 和 artifact 上传；随后执行 smoke script 的纯静态/反射部分。

- [ ] **Step 3: 补充 README、教程和变更记录**

README 首屏列出托盘、Alt+Tab 隐藏、刷新周期、透明度、通知和脱敏诊断；中文教程给出操作表和恢复路径；CHANGELOG 增加 P0 版本条目；所有下载链接指向含新功能的 Release。

- [ ] **Step 4: 本地执行完整门禁**

```powershell
git diff --check
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = @(Get-ChildItem -File -Filter *.cs | Select-Object -ExpandProperty FullName)
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror /utf8output `
  /out:SubscriptionStatus.exe /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll $sources
$env:STATUSBAR_EXE = (Resolve-Path .\SubscriptionStatus.exe)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1
```

Expected: csc exit 0、smoke exit 0、程序响应、Run 键无旧 marker、敏感信息扫描无新增凭据。

- [ ] **Step 5: 提交计划实现**

```powershell
git add .github/workflows/build.yml tests/Smoke README.md docs/QUICKSTART.zh-CN.md CHANGELOG.md
git commit -m "ci: 增加状态栏 P0 构建和烟测门禁"
```

## Plan Self-Review

- **Spec coverage:** P0 设置/持久化对应 Task 1；启动项迁移对应 Task 2；刷新、Alt+Tab 和透明度对应 Task 3；托盘与设置窗口对应 Task 4；通知和诊断对应 Task 5；构建、烟测和文档对应 Task 6。历史趋势、自动更新和多 Provider 明确留在后续路线，不属于本计划。
- **Placeholder scan:** 本计划没有 `TODO`、`TBD`、`later` 或未定义的“适当处理”步骤；每个代码接口和测试命令均已给出。
- **Type consistency:** `AppSettings`、`SettingsStore`、`StartupManager`、`RefreshScheduler`、`TrayController`、`SettingsForm`、`NotificationEvaluator` 和 `DiagnosticsService` 的签名在任务之间保持一致；`StatusWindow` 只作为协调器使用这些接口。
- **Safety check:** 迁移任务明确删除 Run marker；透明度限制为三档；诊断使用白名单；CI 不读取或上传用户凭据。
