# AI Usage Hub 下一阶段产品设计

**日期：** 2026-08-27  
**状态：** 已获用户全权授权，作为下一阶段实现基线  
**适用项目：** ChatGPT / Codex Usage Status Bar for Windows

## 1. 背景与问题判断

当前版本已经具备官方额度查询、开机自启、托盘、刷新周期、有限透明度、阈值提醒和脱敏诊断，但常驻界面仍然是一个只能承载少量文字的 320×40 状态条。用户能看到数字，却看不到数据新鲜度、历史趋势、失败后的最后成功数据，也没有一个能够承接设置、诊断和未来 Provider 的稳定工作区。

下一阶段不再以“继续增加右键菜单项”为主，而是把产品定位为 **AI Usage Hub**：状态栏负责低打扰的实时入口，详情面板负责可读、可操作的额度工作流，托盘负责长期驻留，缓存/历史/Provider/诊断负责可靠性和可维护性。

## 2. 目标与非目标

### 2.1 目标

- 在不牺牲免安装、低资源和隐私边界的前提下，显著改善首屏可读性和长期使用价值。
- 网络失败时保留最后一次成功摘要，并明确标出“缓存/过期”，禁止把旧数据伪装成实时数据。
- 提供 24 小时和 7 天的本地趋势，帮助用户判断额度消耗速度，而不是只看一个瞬时百分比。
- 把当前单文件协调逻辑逐步拆为模型、Provider、存储、调度和展示层，新增功能不再直接挤入绘制方法。
- 为其他官方额度来源预留稳定 Provider 契约，但本阶段不凭猜测接入未验证的第三方接口。
- 增加可回滚的手动更新检查、SHA-256 校验和清晰的版本信息。
- 让详情面板、设置窗口和诊断窗口在高 DPI、键盘操作和多显示器场景下保持可用。

### 2.2 非目标

- 不建设云端账户系统、遥测服务或远程额度代理。
- 不上传额度历史、OAuth 凭据、完整响应、代理地址或本地路径。
- 不默认读取 Claude、Gemini、第三方中转站或其他软件的密钥。
- 不在没有验证官方契约前宣称支持某个具体订阅计划或第三方 Provider。
- 不强制迁移到 WPF/.NET 8；先保持当前 Windows 自带 .NET Framework 编译路径，未来单独评估渲染层迁移。

## 3. 用户体验设计

### 3.1 状态栏模式

默认状态栏保持常驻、紧凑和低打扰，但重新组织信息层级：

- 左侧显示来源/计划的安全短名和状态点。
- 中部显示主要窗口和次要窗口的百分比、进度条及 `MM/dd HH:mm` 重置时间。
- 右侧显示 `实时`、`缓存 · 12 分钟前`、`OAuth` 或 `错误` 状态，不把旧数据伪装成实时。
- 点击状态栏空白区域或状态区域打开详情面板；刷新和隐藏仍保留图标操作。
- `WS_EX_TOOLWINDOW`、`ShowInTaskbar=false` 和托盘恢复行为继续保留。

### 3.2 详情面板

详情面板是无边框、可拖动、固定最小尺寸约 420×360 的工具窗口，`ShowInTaskbar=false`，不进入 `Alt+Tab`。它包含：

1. 顶部状态带：来源名称、安全计划名、数据状态、最近成功时间和手动刷新按钮。
2. 窗口卡片：每个官方 `rate_limit` 窗口显示当前百分比、剩余百分比、重置日期时间、倒计时和状态颜色。
3. 趋势区域：使用本地摘要绘制 24 小时/7 天折线或柱线；没有足够数据时显示明确的“正在收集数据”，不画虚假曲线。
4. 操作区：设置、诊断、打开 Release、复制安全摘要、隐藏到托盘和退出。
5. 页脚：应用版本、数据来源声明、缓存保留期限和“所有数据仅保存在本机”的简短提示。

详情面板不展示账户 ID、Token、完整代理 URI、完整响应或本地凭据路径。计划名称继续使用白名单显示策略。

### 3.3 视觉与可访问性

- 提供 `跟随系统`、`深色`、`浅色` 三种主题；默认跟随系统，深色作为高对比回退。
- 使用四种语义状态颜色：实时成功、缓存可用、需要操作、错误；颜色之外必须有文字或图标表达状态。
- 所有固定窗口使用稳定宽高约束；文本过长时截断并通过工具提示提供安全短说明。
- 支持系统字体缩放，不用视口宽度缩放字体；高 DPI 下不裁剪按钮、数字和日期。
- 键盘可通过 Tab 到达刷新、设置、诊断、复制、隐藏和退出；Esc 关闭详情面板而不退出主程序。
- 不使用真正的纯透明窗口；继续提供实色、85% 和 65% 三档可读透明度。

## 4. 架构设计

### 4.1 分层边界

```text
Presentation
  StatusWindow / UsageDetailsForm / SettingsForm / DiagnosticsForm / TrayController
       |
Application
  AppCoordinator / RefreshScheduler / NotificationEvaluator / UsageCache / HistoryService
       |
Domain
  UsageSnapshot / UsageWindow / UsageStatus / UsageHistoryPoint / DiagnosticReport
       |
Infrastructure
  IUsageProvider / ChatGptCodexProvider / CodexCredentialReader
  SettingsStore / HistoryStore / StartupManager / UpdateChecker
```

当前源码仍以根目录 `.cs` 文件和内部类型为主；允许分阶段拆分，不一次性更换项目文件。所有新模块保持 `internal`，由 `SubscriptionStatus.cs` 的协调器注入依赖。

### 4.2 Provider 契约

```csharp
internal interface IUsageProvider : IDisposable
{
    string ProviderId { get; }
    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken);
    string GetCredentialDiagnostic();
    string GetNetworkDiagnostic();
}
```

当前 `OfficialQuotaService` 通过适配器实现该契约，保留现有 ChatGPT/Codex OAuth 请求行为。Provider 只能返回已经脱敏的 `UsageSnapshot`；凭据读取器不得把 Token 交给缓存、历史、通知、诊断或更新模块。

### 4.3 统一状态模型

`UsageSnapshot` 必须包含：

- `ProviderId`、白名单 `PlanName`、窗口列表和查询时间。
- `UsageStatus`：`Loading`、`Live`、`Cached`、`OAuthExpired`、`NetworkError`、`ApiError`、`ParseError`。
- `IsStale`、`LastLiveAt` 和稳定错误码；错误消息只来自固定映射。
- `UsageWindow`：窗口秒数、使用率、重置时间和受控显示名称。

缓存快照必须把状态标记为 `Cached`，并保留 `LastLiveAt`。状态栏、详情面板和诊断读取同一快照，禁止各自推断“是否实时”。

### 4.4 本地缓存与历史

- `UsageCache` 保存最近一次成功快照，最多保存 1 个 JSON 文件；只包含窗口摘要、查询时间、ProviderId、计划白名单名和应用版本。
- `HistoryStore` 保存最多 500 条或最近 30 天的摘要点，以原子替换写入；每次成功查询最多追加一条，按 ProviderId 和窗口秒数去重同一查询时间。
- 历史记录不保存账户标签、账户 ID、响应原文、Token、代理地址、文件路径或异常堆栈。
- 文件损坏时备份为 `.bak`，回退为空历史/无缓存，状态栏仍可启动。
- 退出或手动清理只删除应用自己的 `%LOCALAPPDATA%\ChatGPTCodexUsageStatusBar` 子目录，不碰 Codex CLI 凭据。

### 4.5 通知与更新

- 通知默认关闭；开启后按 ProviderId、窗口秒数和有效 `reset_at` 追踪边沿，阈值通知与重置通知都去重。
- 通知正文只能包含受控窗口名称、百分比、重置日期时间和状态词。
- `UpdateChecker` 只在用户手动触发或用户打开自动检查时访问 GitHub Releases；仅比较 SemVer，下载后计算 SHA-256，校验失败不得替换当前程序。
- 更新默认不自动安装；下载、替换和重启均需用户确认，旧版本文件保留到确认新版本可启动之后。

## 5. 数据流与错误恢复

```text
Timer / 手动刷新
      |
      v
AppCoordinator --禁止并行--> IUsageProvider
      |                         |
      |                    官方 HTTPS 请求
      v                         v
UsageSnapshot --> UsageCache / HistoryStore / NotificationEvaluator
      |                         |
      +--> StatusWindow / UsageDetailsForm / TrayController
      +--> DiagnosticsService
      +--> UpdateChecker（独立、用户触发）
```

恢复策略：

| 场景 | 状态栏与详情面板 | 可操作动作 |
| --- | --- | --- |
| 首次加载 | 加载中，不绘制虚假百分比 | 等待或手动刷新 |
| 网络失败且有缓存 | 显示缓存数据和缓存时间 | 检查代理、刷新、打开诊断 |
| 网络失败且无缓存 | 显示错误状态和下一步 | `codex login`、检查网络 |
| OAuth 过期 | 显示 OAuth 过期 | 运行 `codex login` |
| 字段解析失败 | 保留上次成功摘要并标为过期 | 复制诊断、等待适配 |
| 缓存/历史损坏 | 删除应用自己的坏文件并回退默认 | 继续运行 |
| 更新失败 | 保持当前版本不变 | 手动下载 Release |

所有 Timer、托盘、窗口事件和异步回调都必须有最后一道异常边界；关闭顺序为停止调度、取消请求、保存位置、释放 Provider/缓存/托盘/窗口资源。

## 6. 设置与迁移

新增设置字段：

- `ThemeMode`：`System`、`Dark`、`Light`。
- `DetailsPanelEnabled`、`HistoryRetentionDays`、`AutoCheckUpdates`、`LaunchDelaySeconds`。
- 既有刷新、启动、透明度、通知、阈值、位置字段保持兼容。

旧设置缺字段时使用安全默认值；非法枚举、周期、保留天数和延迟统一归一化。现有错误启动项 marker 迁移逻辑不得回归。设置 JSON 版本只用于本地迁移，不写入任何凭据。

## 7. 测试与质量门禁

每个模块先写可失败的反射/纯逻辑 smoke，再实现；至少覆盖：

- 缓存成功/失败回退、过期标记、损坏备份和原子写入。
- 历史裁剪、日期保留、同一查询去重和重置周期。
- Provider 适配器的失败状态映射和取消行为。
- 详情面板无数据、缓存、错误和多窗口布局；设置取消不写入。
- 主题/高 DPI 固定尺寸、`WS_EX_TOOLWINDOW`、托盘隐藏/恢复/退出和单实例。
- 通知阈值、有效 `reset_at` 变化、缺失字段和重复刷新。
- 诊断、通知、缓存、历史和更新请求敏感信息扫描为零。
- GitHub Actions 使用根目录全部 `.cs` 编译；Release 包含 EXE、SHA-256 文件和 ASCII 元数据。

本地门禁：

```powershell
git diff --check
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = @(Get-ChildItem -File -Filter *.cs | Select-Object -ExpandProperty FullName)
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror /utf8output `
  /out:SubscriptionStatus.exe /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll $sources
$env:STATUSBAR_EXE = (Resolve-Path .\SubscriptionStatus.exe).Path
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p0-settings.ps1
```

## 8. 分阶段交付

### Wave 1：可读性和可靠缓存

统一状态模型、缓存/历史、详情面板、状态颜色和缓存时间；旧状态栏继续可用，失败时不清空最后成功数据。

### Wave 2：日常使用体验

趋势图、主题、高 DPI/多显示器、启动延迟、历史清理、通知重置事件和诊断中心。

### Wave 3：可信分发和生态边界

手动更新检查、SHA-256、回滚、发布校验、Provider 注册表和经过验证的官方来源扩展。

每个 Wave 都必须能单独构建、回归和发布，不把未完成能力藏在默认开关后面。

## 9. 验收标准

- 用户只看状态栏也能知道数据是实时、缓存还是错误；缓存数据带时间，不伪装成实时。
- 点击状态栏能打开详情面板，看到所有官方窗口、重置倒计时和真实历史趋势。
- 详情面板和主状态栏都不进入 `Alt+Tab`，托盘隐藏/恢复/退出可靠。
- 主题、刷新周期、透明度、历史保留、通知和更新偏好重启后保持，非法设置安全回退。
- 无网络、OAuth 过期、接口字段变化和缓存损坏都能继续启动并给出下一步动作。
- 任意缓存、历史、通知、诊断、更新请求和 Release 资产均不包含凭据或个人敏感信息。
- Windows 构建、smoke、fixture、UI 生命周期、敏感扫描和 Release 校验全部通过。
