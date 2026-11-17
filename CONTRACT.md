# TileStartMenu 工程契约（并行开发分工与接口约定）

目标：Windows 11 原生磁贴开始菜单。C# / .NET 9 / WPF，零第三方 NuGet 依赖（保证离线可构建）。
解决方案根目录：`<repo>\`

## 1. 环境事实（已实测，不必重复探测）

- .NET SDK：9.0.302，位于 `C:\Program Files\dotnet\dotnet.exe`（**不在 PATH**，必须用绝对路径调用）。
- 无 Visual Studio IDE / 无 MSBuild 命令行；构建统一用 `dotnet` CLI。
- 系统：Windows 11 Pro 10.0.26300，有桌面会话，可运行 GUI 程序。
- 已有基线：解决方案可编译通过（Core 类库 + App WPF 应用，App 引用 Core）。
- Shell 只有 PowerShell（无 Bash）。NuGet 官方源可达，但**本工程禁止引入外部 NuGet 包**。

构建命令（Release）：
```
C:\Program Files\dotnet\dotnet.exe build "<repo>\TileStartMenu.sln" -c Release
```
产物：`src\TileStartMenu.App\bin\Release\net9.0-windows\TileStartMenu.App.exe`

## 2. 文件所有权（并行时严禁越界，避免互相覆盖）

| 路径 | 归属 |
| --- | --- |
| `src/TileStartMenu.Core/**` | 核心与系统集成 |
| `src/TileStartMenu.App/AppHost/**`（可新建） | 核心与系统集成 |
| `src/TileStartMenu.App/App.xaml`、`App.xaml.cs` | 核心与系统集成 |
| `src/TileStartMenu.App/UI/**` | UI 与交互 |
| `src/TileStartMenu.App/*.csproj`、`src/TileStartMenu.Core/*.csproj`、`*.sln` | 冻结：任何人不得修改（确有需要请回报 Organizer） |
| `CONTRACT.md` | 冻结 |
| `docs/**` | 测试与验收 |
| `assets/**` | UI 与交互 |

两方都不得修改对方目录下的文件；跨层需求通过下面的接口解决，不要靠改对方代码。

## 3. 已冻结的接口（签名不可改，只能补实现）

命名空间与文件位置如下，`CONTRACT.md` 之外任何人不得改动这些声明：

- `TileStartMenu.Core.Models`（`Models/TileModels.cs`）
  - `TileSize { Small, Medium, Wide, Large }`
  - `TileActionKind { Launch, File, Folder, Url, Shell }`
  - `TileItem`（Id / Name / Kind / Target / Arguments / WorkingDirectory / Color / Size / IconPath / GroupId / Order / ShowLabel）
  - `TileGroup`（Id / Name / Order）
  - `AppSettings`（WinKeyEnabled / LaunchAtStartup / BackgroundColor / BackgroundOpacity / ShowClock / MaxTapHoldMs）
  - `MenuConfig`（Version / Groups / Tiles / Settings，含 `Normalize()`）
- `TileStartMenu.Core.Persistence.IConfigStore`：`ConfigPath` / `IconCacheDir` / `Current` / `Load()` / `Save(MenuConfig)` / `event Changed`；实现 `JsonConfigStore` 已可用（原子写 + 损坏自愈 + 首次运行种子数据）。
- `TileStartMenu.Core.Launcher.ITileLauncher`：`Execute(TileItem, out string? error)` / `CanResolve(TileItem)`；实现 `TileLauncher` 已可用（ShellExecute 语义）。
- `TileStartMenu.Core.Hotkey.IWinKeyHookService`：`IsRunning` / `LastError` / `Start()` / `Stop()` / `event StartKeyPressed`。
  **当前为占位实现，必须由核心方替换为真实钩子。**
- `TileStartMenu.Core.Icons.IIconService`：`GetIconPngPath(TileItem)` / `Invalidate(TileItem)`。
  **当前为占位实现（返回 null），必须由核心方实现真实提取。**
- `TileStartMenu.App.UI.IMenuWindow`（`UI/IMenuWindow.cs`）：`IsMenuOpen` / `ShowMenu()` / `HideMenu()` / `ToggleMenu()`。
  系统集成层只通过该接口控制菜单显隐；`MainWindow` 实现该接口。
- `TileStartMenu.App.UI.TileMetrics`（`UI/TileMetrics.cs`）：Metro 网格度量（Cell=96、Gap=8，以及各尺寸宽高与格数）。UI 方可按需扩展，但常量语义保持一致。

装配约定：`MainWindow` 的构造函数保持
`public MainWindow(IConfigStore store, ITileLauncher launcher)`
（UI 方可自行增加可空可选参数，但不得删除这两个必填参数）。

## 4. 配置文件

- 路径：`%LOCALAPPDATA%\TileStartMenu\menu.json`（图标缓存在同目录 `icons\`）。
- 结构即 `MenuConfig` 的 JSON（camelCase，枚举以字符串序列化），已由 `JsonConfigStore` 落地。
- 磁贴必须真实可执行：Launch/File/Folder/Url/Shell 五类都要能跑通，禁止造假数据。

## 5. 硬性行为约束（Win 键接管）

- 用 `WH_KEYBOARD_LL` 低层键盘钩子，**只在**"单按 Win 键且过程中未按下其他键、且按住时长 ≤ MaxTapHoldMs"时吞掉该次 Win 键并触发 `StartKeyPressed`。
- 任何 Win+X 组合（Win+R / Win+L / Win+Shift+S / Win+D / Win+E / Win+Tab…）必须放行，不得破坏系统快捷键。
- 必须提供：托盘菜单里的「恢复系统默认 Win 键」开关、以及「退出」入口。
- 不以管理员身份强制常驻；若必须提升才能覆盖某些前台窗口，要在 README 里写清事实与后果。
- 钩子安装失败不得崩溃：`Start()` 返回 false + `LastError`，界面给出可读提示。

## 6. 代码风格

- 全部注释与用户可见文案用简体中文；代码标识符用英文。
- 不引入外部 NuGet 包；只用 BCL + P/Invoke。
- 任何新增文件都放在自己所有权目录内；文件命名用英文 PascalCase。
- 提交前必须保证 `dotnet build` 为 0 error（警告尽量清零）。

## 7. 追加需求（v2，优先级高于前面关于"示例磁贴/居中窗口"的描述）

用户追加了两条硬性需求，若已实现部分与之冲突，按本节改：

1. **菜单必须真正全屏铺满**：App 窗口覆盖整个屏幕（含任务栏区域），磁贴网格铺开、可滚动，深色纯色/渐变底。关闭方式四种都要能快速关闭：Esc、再按一次 Win 键、点窗口外空白、点窗口边缘；关闭后焦点必须回到用户原来的前台窗口（由系统集成层记录并恢复前台 HWND）。
2. **默认磁贴 = 全部应用**：首次启动枚举本机所有可启动应用生成磁贴，而不是几个示例项。
   - Win32：扫描 `%ProgramData%\Microsoft\Windows\Start Menu\Programs` 与 `%APPDATA%\Microsoft\Windows\Start Menu\Programs` 下所有 `.lnk`（递归），解析目标与图标。
   - UWP：通过 `shell:AppsFolder` 枚举（AUMID 启动），能直接启动。
   - 去重（同名且同目标只留一个）；过滤卸载程序 / readme / 帮助 / 网址快捷方式（`.url`）等无意义项。
   - "默认全量"只发生在首次运行或用户手动点「重置为全部应用」；用户删掉的磁贴不得在下次启动被自动补回。
   - 规模：100~300 个应用时全屏网格必须流畅 —— 图标异步提取、界面虚拟化/懒加载，不得阻塞冷启动。
3. 磁贴交互要轻量：长按/拖动进入拖拽（有视觉反馈，不需要复杂编辑模式），可拖拽排序、拖到分组、右键快速改尺寸与重命名。

追加验收标准：
① 按 Win 键后全屏铺满屏幕，Esc / 再按 Win / 点空白 都能关闭并回到原前台窗口；
② 首次启动磁贴数与本机实际可启动应用数一致（要给出实测数量），且磁贴点击能真实启动对应应用；
③ 磁贴规模 ≥100 时打开与滚动不卡顿（给出实测数据）。

新增冻结接口：`src/TileStartMenu.Core/Catalog/AppCatalogContracts.cs`
（`AppEntryKind` / `AppEntry` / `IAppCatalog` / `TilePalette`），实现方不得改动签名，只能补实现。
另有一份冻结工具：`src/TileStartMenu.Core/Catalog/AppIdentity.cs`（Pin/Unpin 的应用身份 key）。

## 8. 追加需求（v3：Win8.1 编辑交互，优先级最高）

用户明确要求编辑交互按 **Win8.1** 手感做，不要做成 Win10/11 的右键小菜单：

### 8.1 Pin / Unpin
- 全屏开始菜单里右键（或长按）磁贴 → 屏幕**底部升起 Win8.1 风格操作栏（App Bar）**，栏内是底部条状、图标+文字横向排布的按钮：「从开始菜单取消固定」「调整大小」等；**不是**弹在光标旁的小上下文菜单。
- 「全部应用」列表（全屏菜单内的应用清单页，按首字母分组）里右键某应用 → 同一底部操作栏出现「固定到开始菜单」；已固定的显示为「取消固定」。
- Pin / Unpin 一步到位：取消固定后磁贴带动画消失；重新固定出现在磁贴区**末尾**（不恢复上次位置）。
- 取消固定只从磁贴墙移除：不删除应用、不删除其配置记录，「全部应用」里仍存在且可再次固定。
- 磁贴墙被取消固定到空时有空态提示（例如「在全部应用里右键应用即可固定到此处」），不能留黑屏。

### 8.2 磁贴尺寸
- 每磁贴独立四档：小 1x1 / 中 2x2 / 宽 4x2 / 大 4x4，网格单位为 1 小格。
- 「调整大小」每次点击切到下一档并循环（Win8.1 行为）；操作栏里也可直接选档。
- 尺寸变更后网格立即重排，无重叠、无空洞塌陷，并写入配置持久化。

### 8.3 编辑体验
- 拖拽移动位置、拖到别的分组、拖入/拖出分组；拖动时被拖磁贴半透明跟随，其余磁贴**实时让位**并预览落点。
- 分组可命名、可整体拖拽移动、可新建分组。
- 所有编辑即时生效并自动保存，**不提供保存/取消对话框**。
- 不需要模态"编辑模式"：常态右键即唤起底部操作栏；Esc 或点击空白收起。

### 8.4 端到端验收（必须实测并留证据）
1. 全部应用右键某应用 → Pin → 磁贴出现在墙；再右键该磁贴 → Unpin → 磁贴带动画消失。
2. 右键磁贴 → 调整大小 → 依次切到小/中/宽/大四档，网格重排正确；重启程序后尺寸保持。
3. 拖拽磁贴换位、拖进另一分组；重启后顺序与分组保持。
4. Esc / 再按 Win 键退出全屏菜单并回焦到桌面；退出后系统 Win 键行为正常（不会同时弹出系统开始菜单）。

背景观感：全屏纯色深底 + 方块磁贴，先不做模糊玻璃。

### 8.5 为此需要的模型调整（由核心方实施）
- `TileItem` 增加 `Pinned`（bool，默认 true）：磁贴墙只渲染 `Pinned == true` 的项；`MenuConfig.Normalize()` 不得丢弃 `Pinned == false` 的记录。
- Pin 的落点：目标分组末尾（`Order = 该组最大 Order + 1`）。
- 「全部应用」列表数据来自 `IAppCatalog.Enumerate()`（有缓存，必须快），与 `Tiles` 用 `AppIdentity` 对齐判断是否已固定。

## 9. 视觉与布局总纲（v5：完全模仿 Win8.1，优先级高于任何"现代化/玻璃感"建议）

用户判定当前界面"太古老"，要求推翻外观重做。以下为硬性设计规范：

### 9.1 禁止出现的老式 UI
- 拆掉顶部工具栏（「磁贴 / 全部应用 / + 添加磁贴 / 设置」那一排），它是最像老软件的元凶。
- 无标题栏、无窗口边框、无菜单栏、无可见竖向滚动条实体；无圆角（CornerRadius 必须为 0）、无渐变、无阴影（DropShadowEffect）、无模糊（BlurEffect）、无发光描边。
- 不得使用 WPF 默认 Button / ScrollBar / ContextMenu 外观，一律重写 ControlTemplate。
- 字体：Segoe UI（Semibold/Regular 混用），时间用 Segoe UI Light 大字；中文回退 Microsoft YaHei UI Light。
- 磁贴悬停不要描边高亮；按下时整体轻微下沉缩放（0.97~0.98，约 100ms）后回弹。

### 9.2 Win8.1 屏幕结构
- 全屏、**横向**滚动：磁贴按列排布，若干列组成一个分组区块，整屏从左向右铺开、越界横向滚动（滚轮向下=向右翻页，拖动背景可跟手横滑，触控板横滑可用）。不得再是纵向 ScrollViewer 单列堆叠。
- 每个分组上方一行小字组名；分组之间以留白/细竖线分隔。
- 右上角：电源按钮（⏻），点开浮层给 关机/重启/睡眠/锁屏/注销，关机与重启二次确认；旁边可放用户头像圆点。
- 左下角：`所有应用 ↓` 入口，点击切到全部应用列表；在列表里右键 → Pin。
- 右键空白背景 → 底部升起 AppBar，内含「自定义」（分组命名、新建分组、主题色），不得再挂到顶栏。
- 「叠加分组」= Win8.1 语义缩放：Ctrl+滚轮 / 双指捏合 / 齿轮入口 → 切换分组总览（可拖动分组整体位置、改名、新建组），再缩回磁贴视图；分组之间并列叠加，不是嵌套树。

### 9.3 尺寸与图标规格
网格单位 G 取 50~56px（可把 `TileMetrics.Cell` 由 96 改为 56，四种尺寸与图标比例同步调整）：
- 小 1×1：图标约 60% 边长（32~36px），不显示标签或极小字。
- 中 2×2：图标约 60~65% 边长（**72~80px，主力尺寸，图标必须明显大**），左下 12px 名称。
- 宽 4×2：图标 100~110px，右侧可放名称与一行副文本。
- 大 4×4：图标 160~180px 居中（图标占主导，不得缩在角落），名称 14px 左下。
- 图标倍数可由用户微调：`TileItem` 增加 `double IconScale`（默认 1.0，表示该尺寸的标准比例），可选 0.5/0.6/0.75/1.0 四档，随磁贴持久化。
- 图标必须是应用原始图标（≥256px 高清源），居中、等比、不裁切主体，四周留白均匀。

### 9.4 电源
- 右上角 ⏻ 按钮 + 「系统」组 5 个电源小磁贴，样式与其他磁贴完全一致（纯色方块 + 白色符号），不得做成灰底按钮。
- 电源浮层同为 Win8.1 风格：纯平无圆角无阴影的深色面板，Segoe UI Light 文字，确定/取消为纯色块。

### 9.5 交付前自查（必须留证据）
1. 全屏截图与真实 Win8.1 开始屏幕截图并排对照，说明差异。
2. 全局搜索确认不存在 CornerRadius>0 / LinearGradientBrush / DropShadowEffect / BlurEffect / 默认 Button 与 ScrollBar 外观。
3. 横向滚动手感、按下 0.97 缩放、AppBar 下方滑入（约 150ms）逐项实测。
4. 不破坏：全屏铺满、Win 键唤起、Esc 退出、点击（含慢点击）启动、Pin/Unpin、四档尺寸重排、配置持久化、首启 6~10 基础磁贴。
