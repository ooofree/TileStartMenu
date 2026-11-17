# EVIDENCE — 核心与系统集成层实测记录（v4 + 审查修复）

> 环境：Windows 11 Pro 10.0.26300，.NET 9.0.302，Release 构建。
> 记录时间：2026-11-17。

## 0. v4.2 审查修复（本次）

| 编号 | 问题 | 改法 | 实测 |
|---|---|---|---|
| P0-1 | PowerService 未注入 MainWindow，UI 侧 `_power` 恒 null | `App` 加 `public IPowerService Power { get; }`，构造时 `new PowerService()`；`new MainWindow(_store, _launcher, _icons, _catalog, _power)`；启动 Debug 打印 `[App] power=ready` | 源码已改；App 工程构建被冻结（MainAgent 正在取证），待信号后统一真机验证 |
| P0-2 | `Execute` 直接调 ExitWindowsEx，二次确认只活在 UI | `IPowerService.Arm(PowerAction)` + 10s 有效期；`Execute` 前必须 Arm，否则返回错误"需要在界面二次确认后再执行"；`DryRun` 不受限；lock 保护 | **PowerTest 实测全过**：未 Arm → 5/5 全 REJECTED；Arm(Shutdown) 后 Execute(Restart) → REJECTED（动作不匹配）；Arm(Shutdown) 等 11s 后 Execute(Shutdown) → REJECTED（过期）；DryRun 5/5 OK |
| P1-3 | 默认浏览器磁贴 `arguments="\"%1\""` 把字面量 %1 传给 Edge | `ResolveDefaultBrowser` 剥 `"%1"`/`%*`/`"%*"`/`%*`，剩空串则 null；**不再 Trim 引号**（避免误伤 AutoCAD 的 `/language "zh-CN"`） | menu.json 中 msedge 磁贴 `Arguments=<null>`；AutoCAD 磁贴参数已恢复为 `/language "zh-CN"` |
| P1-4 | `Max(...) ?? 0 + 1` 运算符优先级隐患 | 审查代码：本层无此写法；`MenuConfig.Normalize()` 末尾对每组 `Order` 重排为 0..N-1 连续唯一，作为兜底 | Core 编译 0 error |
| P1-5 | 紧急热键在钩子线程内 `Dispatcher.Invoke` → `Join(钩子线程自身)` 死等 2s | `EmergencyRestoreRequested` 改 `Dispatcher.BeginInvoke`（异步，钩子线程不阻塞）；钩子线程自己 `PostThreadMessage(WM_QUIT)` 退出消息循环；新增 `hook-debug.log` 记录钩子线程 lifetime | 源码已改；真机耗时待冻结解除后实测（目标 <200ms） |

### P0-2 实测输出（PowerTest，未真触发任何关机/重启）

```
=== DryRun (no Arm needed) ===
[OK] Shutdown : SE_SHUTDOWN_NAME checked(err=1313,交互式用户已自带权限)
[OK] Restart  : 同上
[OK] Sleep    : no privilege elevation needed
[OK] Lock     : no privilege elevation needed
[OK] SignOut  : no privilege elevation needed

=== Execute WITHOUT Arm (should all be rejected) ===
[REJECTED] Shutdown: 需要在界面二次确认后再执行（Shutdown 未 Arm 或已超过 10s）
[REJECTED] Restart : 同上
[REJECTED] Sleep   : 同上
[REJECTED] Lock    : 同上
[REJECTED] SignOut : 同上

=== Arm(Shutdown) then Execute(Restart) ===
[REJECTED] Restart after Arm(Shutdown): 动作不匹配

=== Arm(Shutdown), wait 11s, Execute(Shutdown) ===
[REJECTED] Shutdown after 11s: 已过期
```

### menu.json 中 msedge 磁贴修正后片段

```json
{
  "name": "msedge",
  "kind": "Launch",
  "target": "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
  "arguments": null,
  "iconScale": 1.0
}
```

### 构建状态
- `dotnet build src/TileStartMenu.Core/TileStartMenu.Core.csproj -c Release`：**0 error / 0 warning**（本次修复后已重跑确认）。
- App 工程按 MainAgent 冻结要求暂不构建/启动，等"可以构建"信号后统一真机验证。

---

## 0. v4 变更摘要（相对上一版）

- **图标提取改为 IShellItemImageFactory 256×256**（SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY），ExtractIconEx 大图标作为后备；失败写 `icon-debug.log`。
- **首轮种子改为"少量基础磁贴"**（v2 的"全量 547 磁贴"已作废）：只生成 6~10 个本机真实存在的常用磁贴 + 5 个 1×1 电源磁贴；其余 500+ 应用只进「全部应用」列表，由用户自行 Pin。
- **新增 PowerService**：ExitWindowsEx / SetSuspendState / LockWorkStation，带 SE_SHUTDOWN_NAME 权限提升；DryRun 只校验不触发。
- `TileItem` 新增 `PowerAction` 字段（None/Shutdown/Restart/Sleep/Lock/SignOut）。

## 1. 构建状态

- `dotnet build src/TileStartMenu.Core/TileStartMenu.Core.csproj -c Release`：**0 error / 0 warning**。
- 整解决方案：Core 通过；App 工程仍被并行 UI 方在写文件 `UI/AllAppsView.xaml.cs`（缺 `using System.Windows.Input;`）阻塞，非核心层引入。

## 2. 图标提取（IShellItemImageFactory 256px）

实现路径：`SHCreateItemFromParsingName(parseName, null, IID_IShellItemImageFactory, out factory)` → `factory.GetImage(SIZE(256,256), SIIGBF_BIGGERSIZEOK|SIIGBF_ICONONLY, out hbmp)` → `GetDIBits` 拿 BGRA → `PngBitmapEncoder` 落盘。

随机抽 5 个磁贴实测（清空缓存后首次提取）：

| 目标 | PNG 尺寸 | 文件大小 | 耗时 | 缓存路径 |
|---|---|---|---|---|
| explorer.exe | **256×256** | 22 955 B | 177 ms | `%LOCALAPPDATA%\TileStartMenu\icons\c097890c….png` |
| notepad.exe | **256×256** | 67 056 B | 28 ms | `…\icons\52cfbfa6….png` |
| calc.exe | **256×256** | 4 706 B | 17 ms | `…\icons\a09f62dc….png` |
| cmd.exe | **256×256** | 6 902 B | 17 ms | `…\icons\a7a88a05….png` |
| C:\Windows（文件夹） | **256×256** | 25 487 B | 12 ms | `…\icons\be167108….png` |

5/5 都是真彩色 256×256 PNG，不是 32px 放大、不是文字占位。缓存键 = SHA1(kind + 解析后路径 + 文件 mtime)，重复调用命中缓存不重复提取。`RequestIcon` 用 SemaphoreSlim(2) 在后台线程跑，失败写 `icon-debug.log`，返回 null 不抛异常。

> 注：UWP（`shell:AppsFolder\{AUMID}`）走同一条 SHCreateItemFromParsingName 路径，理论上能拿到资源管理器里那张彩色 Logo；本轮未单独跑 UWP 样本测试（App 工程被 UI 阻塞无法端到端启动），留作联调后补测。

## 3. 首轮基础磁贴（v4，替代旧"全量种子"）

首次启动且 `AllAppsSeeded=false` 时只生成以下磁贴（不存在的跳过）：

**「常用」分组（Medium 尺寸）：**
1. 文件资源管理器（explorer.exe）
2. 默认浏览器（从注册表 `HKCR\http\shell\open\command` 解析，不写死 Edge）
3. 设置（ms-settings:）
4. 计算器（calc.exe）
5. 记事本（notepad.exe）
6. 终端（优先 wt.exe → WindowsTerminal.exe → powershell.exe）

**「系统」分组（Small 尺寸，#1B1B1B 深灰）：**
7. 关机（PowerAction=Shutdown）
8. 重启（PowerAction=Restart）
9. 睡眠（PowerAction=Sleep）
10. 锁屏（PowerAction=Lock）
11. 注销（PowerAction=SignOut）

共 **最多 11 条**（6 基础 + 5 电源），远小于旧设计的 547 条。`AllAppsSeeded=true` 后绝不再自动补磁贴；用户手动「重置为全部应用」入口可重建。

## 4. 电源服务 DryRun 实测

`PowerService.DryRun(action, out report)` 对 5 个动作的输出（本机普通交互式用户，未提权）：

```
[OK] Shutdown : action=Shutdown; SE_SHUTDOWN_NAME elevate=checked(err=1313,通常交互式用户已自带权限); user=OOOFREE\ooofr; DryRun: not actually triggered
[OK] Restart  : action=Restart;  同上 err=1313
[OK] Sleep    : action=Sleep;    no privilege elevation needed; user=OOOFREE\ooofr; DryRun: not actually triggered
[OK] Lock     : action=Lock;     同上
[OK] SignOut  : action=SignOut;  同上
```

- err=1313 = ERROR_ALL_ASSIGNED（SeShutdownPrivilege 已在交互式用户 token 里），DryRun 不把它当失败。
- 真实 `Execute` 路径：Shutdown/Restart 走 `ExitWindowsEx(EWX_POWEROFF|EWX_FORCEIFHUNG)` / `ExitWindowsEx(EWX_REBOOT|EWX_FORCEIFHUNG)`；Sleep 走 `SetSuspendState(false,false,false)`；Lock 走 `LockWorkStation()`；SignOut 走 `ExitWindowsEx(EWX_LOGOFF)`。
- **本轮没有真的触发任何关机/重启**（DryRun 只做权限与参数校验）。
- 电源磁贴 `PowerAction != None`，Normalize 不会丢弃；UI 层应禁止 Unpin/删除，但允许拖动改位置/改尺寸。

## 5. 应用枚举（保留给「全部应用」列表，不再用于首轮种子）

实测（本机）：

```
首次枚举：547 条（Win32=236, UWP=311），耗时 3465 ms
缓存命中（第二次进程启动）：547 条，0 ms
```

- Win32：递归扫 `%ProgramData%\Microsoft\Windows\Start Menu\Programs` + `%APPDATA%\Microsoft\Windows\Start Menu\Programs`，IShellLinkW 解析 .lnk。
- UWP：Shell.Application.NameSpace("shell:AppsFolder")，AUMID 作为启动目标。
- 缓存到 `%LOCALAPPDATA%\TileStartMenu\appcatalog.json`，按开始菜单目录 LastWriteTimeUtc 失效。
- 枚举在后台线程跑，不阻塞菜单弹出。

## 6. Win 键接管 / 系统集成（保持上一版）

- WH_KEYBOARD_LL 钩子在专用线程；`WinKeyGestureClassifier` 纯逻辑 17 条自测全过。
- 冷启动（进程→钩子就绪）：**325–357 ms**（timing.log 样本）。
- 紧急恢复：Ctrl+Alt+Shift+Esc 立即 PostThreadMessage(WM_QUIT) 卸载钩子；托盘菜单有「恢复系统默认 Win 键」。
- 单实例（命名互斥量 + 管道唤起）、托盘（P/Invoke Shell_NotifyIcon，无 WinForms 依赖）、HKCU Run 自启、Ctrl+Alt+Space 备用热键、GetForegroundWindow/SetForegroundWindow 焦点归位、订阅 IConfigStore.Changed 即时重应用。
- SendInput 补送 Win down 带 `dwExtraInfo=0x54534D54` 魔数避免递归。

## 7. 已知限制

1. 整解决方案 App 工程当前被 UI 方在写的 `UI/AllAppsView.xaml.cs` 阻塞（缺 using），核心层未碰该文件。
2. `invoke_to_visible_ms` 样本要等 UI 联调后窗口能弹出才会写入。
3. UWP 图标 256px 提取本轮未单独跑样本（Shell 路径理论可用），待联调后补。
4. 电源磁贴的"不可 Unpin"语义靠 UI 层尊重 `PowerAction != None` 标志，核心层不强制（不涉及删配置）。
5. 默认浏览器解析读的是 `HKCR\http\shell\open\command`，对少数把 http 关联到 UWP 浏览器（如 Edge 的 AppExecutionAlias）的情况可能拿到 UWP AUMID 而不是 exe 路径；这种情况会跳过浏览器磁贴而不是报错。
