# AI Usage Hub 下一阶段 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在保留免安装和隐私边界的前提下，把现有 ChatGPT/Codex 状态栏升级为带可靠缓存、历史趋势、详情面板、主题和可信更新边界的本地 AI Usage Hub。

**Architecture:** 先在根目录拆出 Domain/Application/Infrastructure/Presentation 对应的内部 C# 类型，保持现有 .NET Framework 4.5 `csc.exe` 多文件构建。`StatusWindow` 作为协调器，通过模型、缓存、历史、Provider 和详情窗口组合功能，所有持久化对象都禁止携带 OAuth 敏感字段。

**Tech Stack:** C# 5-compatible syntax, .NET Framework 4.5 WinForms, `HttpClient`, `JavaScriptSerializer`, Windows Registry, GitHub REST API, PowerShell smoke tests.

**Spec:** `docs/superpowers/specs/2026-08-27-usage-hub-next-design.md`

## Global Constraints

- OAuth Token 只允许存在于 Provider 的单次请求内存路径，不进入缓存、历史、通知、诊断、剪贴板、更新请求或日志。
- `src/` 目录全部 `.cs` 使用 Windows 自带 .NET Framework `csc.exe` 编译并开启 `/warnaserror`；不引入外部运行时依赖。
- 主状态栏和详情/设置/诊断窗口均使用 `ShowInTaskbar=false`，主状态栏和详情面板使用 `WS_EX_TOOLWINDOW`，不进入 `Alt+Tab`。
- 本地只保存 `%LOCALAPPDATA%\ChatGPTCodexUsageStatusBar` 子目录内的设置、缓存和非敏感摘要；损坏文件必须备份并安全回退。
- 新增业务方法和复杂私有方法必须有中文注释；所有异步 UI 事件必须捕获最终异常。
- 每个任务先运行失败测试，再实现最小改动，完成后运行 csc、smoke 和 `git diff --check`，使用 Conventional Commit 中文描述。

---

### Task 1: 统一额度状态模型与 Provider 边界

**Files:**
- Create: `src/UsageModels.cs`
- Create: `src/IUsageProvider.cs`
- Create: `src/OfficialUsageProvider.cs`
- Modify: `src/SubscriptionStatus.cs`
- Test: `tests/Smoke/p1-usage-hub.ps1`

**Interfaces:**
- `UsageSnapshot` exposes `ProviderId`, `PlanName`, `Windows`, `Status`, `IsStale`, `LastLiveAt`, `QueriedAt`, and `ErrorCode`.
- `UsageWindow` exposes `LimitWindowSeconds`, `UsedPercent`, `ResetAt`, and controlled `DisplayName`.
- `IUsageProvider` exposes `ProviderId`, `GetUsageAsync(CancellationToken)`, `GetCredentialDiagnostic()`, `GetNetworkDiagnostic()`, and `Dispose()`.
- `OfficialUsageProvider` adapts the existing official OAuth query without exposing credential internals.

- [ ] **Step 1: Write the failing reflection smoke**

```powershell
$usageType = $assembly.GetType('UsageSnapshot')
$providerType = $assembly.GetType('IUsageProvider')
if ($null -eq $usageType -or $null -eq $providerType) { throw 'Usage model/provider boundary is missing' }
foreach ($name in @('ProviderId','Status','IsStale','LastLiveAt','ErrorCode')) {
    if ($null -eq $usageType.GetProperty($name)) { throw "UsageSnapshot property missing: $name" }
}
```

- [ ] **Step 2: Run it and verify the expected failure**

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p1-usage-hub.ps1`; expected failure is `Usage model/provider boundary is missing`.

- [ ] **Step 3: Implement the model and adapter**

Map existing successful `QuotaSnapshot` values to `UsageSnapshot.Live`; map stable failures to explicit status values; clamp percentages and never copy `AccountLabel` into the persisted model.

- [ ] **Step 4: Compile and rerun the smoke**

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = @(Get-ChildItem -LiteralPath .\src -File -Filter *.cs | Select-Object -ExpandProperty FullName)
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror /utf8output /out:.\dist\SubscriptionStatus.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

Expected: compiler exit 0 and the reflection smoke reaches its next assertion.

- [ ] **Step 5: Commit**

```powershell
git add src/UsageModels.cs src/IUsageProvider.cs src/OfficialUsageProvider.cs src/SubscriptionStatus.cs tests/Smoke/p1-usage-hub.ps1
git commit -m "refactor: 增加统一额度状态模型"
```

### Task 2: 最近成功缓存与历史摘要存储

**Files:**
- Create: `src/UsageCache.cs`
- Create: `src/HistoryStore.cs`
- Modify: `src/AppSettings.cs`
- Modify: `src/SettingsStore.cs`
- Modify: `src/SubscriptionStatus.cs`
- Test: `tests/Smoke/p1-usage-hub.ps1`

**Interfaces:**
- `UsageCache.Load()` returns a safe `UsageSnapshot` or null; `Save(UsageSnapshot snapshot)` persists only successful snapshots.
- `HistoryStore.Append(UsageSnapshot snapshot)`, `Load()`, `Trim()` and `Clear()` manage at most 500 points and 30 days.
- Cache and history use `%LOCALAPPDATA%\ChatGPTCodexUsageStatusBar\cache.json` and `history.json` with temporary files and `.bak` fallback.

- [ ] **Step 1: Add failing cache/history assertions**

Assert the two types and methods exist, then assert the cache DTO property list contains no `AccountId`, `AccessToken`, `RefreshToken`, `ProxyUri` or `RawResponse` property.

- [ ] **Step 2: Implement atomic cache and bounded history**

Use serializer DTOs containing only provider ID, whitelist plan, windows, timestamps and status. Reject null windows, clamp percentages, trim old UTC points according to the selected 7/30/90-day retention and cap the list at 500 before atomic replacement.

- [ ] **Step 3: Integrate stale fallback**

Load cache before the first query. On a failed query keep cached windows, set `IsStale=true`, expose cached age, and display the error state. On success update cache and append one point per query timestamp.

- [ ] **Step 4: Test corruption and privacy**

Corrupt both files, assert `.bak` exists and loading returns safe defaults. Serialize secret-looking plan/account text and assert files contain none of `access_token`, `account_id`, `secret` or a full URI.

- [ ] **Step 5: Compile, smoke and commit**

```powershell
$env:STATUSBAR_EXE = (Resolve-Path .\dist\SubscriptionStatus.exe).Path
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p1-usage-hub.ps1
git add src/UsageCache.cs src/HistoryStore.cs src/AppSettings.cs src/SettingsStore.cs src/SubscriptionStatus.cs tests/Smoke/p1-usage-hub.ps1
git commit -m "feat: 增加额度缓存与本地历史"
```

### Task 3: 详情面板和趋势视图

**Files:**
- Create: `src/UsageDetailsForm.cs`
- Create: `src/TrendPanel.cs`
- Modify: `src/SubscriptionStatus.cs`
- Modify: `src/TrayController.cs`
- Modify: `src/AppSettings.cs`
- Test: `tests/Smoke/p1-usage-hub.ps1`

**Interfaces:**
- `UsageDetailsForm(UsageSnapshot snapshot, IList<HistoryPoint> history, Action refresh, Action settings, Action diagnostics, Action hide, Action exit)` is a non-taskbar tool window.
- `TrendPanel.SetPoints(IList<HistoryPoint> points)` draws no-data, cached-data and real-data states without inventing points.
- `StatusWindow.ShowDetails()` reuses one visible details form instead of creating duplicates.

- [ ] **Step 1: Write failing UI reflection checks**

Assert the details form is a `Form`, `ShowInTaskbar=false`, contains refresh/settings/diagnostics/hide/exit controls, and the trend panel has `SetPoints`.

- [ ] **Step 2: Implement the fixed-size details layout**

Use a 420×360 minimum client area, top status banner, generated window cards, trend panel and footer. Keep every form outside `Alt+Tab` and keep all sizes stable.

- [ ] **Step 3: Implement safe trend drawing**

Draw axes and points only with at least two valid points. Use clipped drawing and semantic colors; otherwise display `正在收集数据` instead of a fake line.

- [ ] **Step 4: Wire status-bar click and tray menu**

Clicking a non-button area calls `ShowDetails()`; the tray menu gets `打开详情`; callbacks use existing safe refresh boundaries and dispose the form on exit.

- [ ] **Step 5: Run UI reflection/process checks and commit**

Start the executable, confirm it responds, verify hidden/restore and assert only one details window exists.

```powershell
git add src/UsageDetailsForm.cs src/TrendPanel.cs src/SubscriptionStatus.cs src/TrayController.cs src/AppSettings.cs tests/Smoke/p1-usage-hub.ps1
git commit -m "feat: 增加额度详情面板与趋势"
```

### Task 4: 主题、高 DPI 和多显示器体验

**Files:**
- Create: `src/ThemeManager.cs`
- Create: `src/WindowPlacement.cs`
- Modify: `src/AppSettings.cs`
- Modify: `src/SettingsForm.cs`
- Modify: `src/SubscriptionStatus.cs`
- Modify: `src/UsageDetailsForm.cs`
- Test: `tests/Smoke/p1-usage-hub.ps1`

**Interfaces:**
- `ThemeMode` values are `System`, `Dark`, `Light`; `ThemeManager.Resolve()` returns a fixed palette.
- `WindowPlacement.TryRestore(Form, AppSettings)` validates a visible work area; `Capture(Form, AppSettings)` stores coordinates only when restore is enabled.

- [ ] **Step 1: Add failing theme/placement assertions**

Assert all three theme values exist, invalid values normalize to `System`, and saved coordinates outside all monitor work areas are rejected.

- [ ] **Step 2: Implement palettes and apply them consistently**

Centralize background, foreground, border, accent, warning and error colors. Apply the palette to status bar, details, settings and diagnostics without decorative blobs or low-contrast text.

- [ ] **Step 3: Implement DPI and placement validation**

Use stable minimum sizes, `AutoScaleMode=Dpi` for larger forms, visible-area intersection validation and primary-work-area fallback. Keep the compact bar dimensions stable.

- [ ] **Step 4: Run reflection/manual checks and commit**

Test invalid coordinates, light/dark/system settings, 125%/150% scale if available and a disconnected-monitor fallback.

```powershell
git add src/ThemeManager.cs src/WindowPlacement.cs src/AppSettings.cs src/SettingsForm.cs src/SubscriptionStatus.cs src/UsageDetailsForm.cs tests/Smoke/p1-usage-hub.ps1
git commit -m "feat: 优化主题与多显示器布局"
```

### Task 5: 诊断中心和可操作错误状态

**Files:**
- Create: `src/DiagnosticsForm.cs`
- Modify: `src/DiagnosticsService.cs`
- Modify: `src/SubscriptionStatus.cs`
- Modify: `src/UsageDetailsForm.cs`
- Modify: `SECURITY.md`
- Test: `tests/Smoke/p1-usage-hub.ps1`

**Interfaces:**
- `DiagnosticsForm(string report, IList<DiagnosticCheck> checks, Action copy, Action close)` is a non-taskbar tool window with a read-only report and check rows.
- `DiagnosticCheck` exposes a fixed name, status enum and safe next action; it has no raw exception field.

- [ ] **Step 1: Write failing check/report assertions**

Inject a bad plan, account label, proxy URI and response body; assert the report contains only whitelist values and the form exposes copy/close controls.

- [ ] **Step 2: Implement safe checks**

Add checks for OAuth mode/file presence, system/custom proxy mode, HTTPS endpoint reachability, cache/history readability, startup value and single-instance marker. Each check returns fixed status text and a next action.

- [ ] **Step 3: Integrate details/tray diagnostics**

“诊断中心” opens the form; “复制安全摘要” copies only the whitelist report. Network checks use short timeouts and never include response bodies or paths.

- [ ] **Step 4: Run privacy/UI smoke and commit**

```powershell
$env:STATUSBAR_EXE = (Resolve-Path .\dist\SubscriptionStatus.exe).Path
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p1-usage-hub.ps1
git add src/DiagnosticsForm.cs src/DiagnosticsService.cs src/SubscriptionStatus.cs src/UsageDetailsForm.cs SECURITY.md tests/Smoke/p1-usage-hub.ps1
git commit -m "feat: 增加可操作诊断中心"
```

### Task 6: 可信更新与发布资产

**Files:**
- Create: `src/UpdateChecker.cs`
- Create: `src/ReleaseVerifier.cs`
- Modify: `src/AppSettings.cs`
- Modify: `src/SettingsForm.cs`
- Modify: `src/TrayController.cs`
- Modify: `.github/workflows/build.yml`
- Modify: `CHANGELOG.md`
- Test: `tests/Smoke/p1-usage-hub.ps1`

**Interfaces:**
- `UpdateChecker.CheckAsync(CancellationToken)` calls only the public GitHub Releases endpoint and returns safe `UpdateInfo`.
- `ReleaseVerifier.ComputeSha256(string path)` returns uppercase SHA-256; `Verify(byte[] data, string expected)` is constant-format and never executes data.

- [ ] **Step 1: Write failing update/verifier tests**

Use a known byte array and expected hash; assert mismatch fails, malformed release JSON returns no update, and no token/header is required.

- [ ] **Step 2: Implement manual update check**

Use `HttpClient` with a short timeout, parse only tag/name/download/checksum URLs, compare SemVer safely and show confirmation before opening/downloading. Never self-replace a running process.

- [ ] **Step 3: Add checksum to the release workflow**

After compilation run `Get-FileHash .\dist\SubscriptionStatus.exe -Algorithm SHA256`, write ASCII `dist\SHA256SUMS.txt`, upload both as artifact and Release assets. Keep release metadata ASCII to avoid Windows path/encoding issues.

- [ ] **Step 4: Run verifier/update/privacy checks and commit**

```powershell
git add src/UpdateChecker.cs src/ReleaseVerifier.cs src/AppSettings.cs src/SettingsForm.cs src/TrayController.cs .github/workflows/build.yml CHANGELOG.md tests/Smoke/p1-usage-hub.ps1
git commit -m "feat: 增加可信更新校验"
```

### Task 7: Fixtures、CI 和最终发布

**Files:**
- Create: `tests/Fixtures/usage-live.json`
- Create: `tests/Fixtures/usage-error.json`
- Modify: `README.md`
- Modify: `docs/QUICKSTART.zh-CN.md`
- Modify: `CONTRIBUTING.md`
- Modify: `CHANGELOG.md`
- Modify: `.github/workflows/build.yml`
- Test: `tests/Smoke/p1-usage-hub.ps1`

**Interfaces:**
- Fixtures contain no credentials, account IDs, secret URLs or personal data.
- CI compiles all `src/` `.cs` files, runs smoke and sensitive-pattern checks, and uploads EXE/SHA-256 artifacts only from `dist/`.

- [ ] **Step 1: Add fixture parsing tests**

Parse live/error fixtures through the same safe mapper and assert windows, reset times, status mapping and malformed-response fallback.

- [ ] **Step 2: Update docs with the Usage Hub workflow**

Document compact mode, details panel, cache age, history retention, themes, notifications, diagnostics, update verification and cleanup boundaries. Keep plan-agnostic wording and optional proxy guidance.

- [ ] **Step 3: Expand CI gates**

Compile all `src/` files, run `git diff --check`, smoke, fixture parsing and a sensitive-pattern scan. Upload `SubscriptionStatus.exe` and `SHA256SUMS.txt` only from `dist/`.

- [ ] **Step 4: Build and verify locally**

```powershell
git diff --check
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = @(Get-ChildItem -LiteralPath .\src -File -Filter *.cs | Select-Object -ExpandProperty FullName)
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror /utf8output /out:.\dist\SubscriptionStatus.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll $sources
$env:STATUSBAR_EXE = (Resolve-Path .\dist\SubscriptionStatus.exe).Path
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Smoke\p1-usage-hub.ps1
```

Expected: compiler exit 0, smoke exit 0, no sensitive-pattern matches, process responsive, details panel opens, cache fallback visibly marked and all windows remain outside `Alt+Tab`.

- [ ] **Step 5: Publish and verify Release**

Create a SemVer release with ASCII metadata, upload EXE and `dist\SHA256SUMS.txt`, verify the public tree has no question-mark filenames, verify Actions success and leave the final local binary running.

```powershell
git add tests/Fixtures tests/Smoke README.md docs/QUICKSTART.zh-CN.md CONTRIBUTING.md CHANGELOG.md .github/workflows/build.yml
git commit -m "docs: 完善 Usage Hub 发布与维护门禁"
```

## Plan Self-Review

- **Spec coverage:** model/provider, cache/history, detail/trend UI, theme/DPI/placement, diagnostics, update verification, fixtures, CI, docs and release each have a dedicated task.
- **Completeness scan:** every step names files, interfaces, commands and concrete assertions; no step depends on an undefined follow-up.
- **Type consistency:** `UsageSnapshot`, `UsageWindow`, `IUsageProvider`, `HistoryPoint`, `UsageDetailsForm`, `ThemeMode`, `DiagnosticCheck`, `UpdateInfo` and `ReleaseVerifier` are referenced with fixed names and signatures.
- **Safety check:** only whitelist DTOs can enter local storage and UI; update checks are manual and checksum-gated; no task asks for account scraping or API-key collection.
