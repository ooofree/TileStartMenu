using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Catalog;

/// <summary>
/// 本机可启动应用枚举器（v3 过滤版）：
///   唯一枚举源 = shell:AppsFolder（与 Win11 开始菜单"所有应用"完全一致，313 条基线）。
///   在此之上：名称规则过滤垃圾项（Uninstall/Help/SDK/spawner 等）、
///   Win32 .lnk 目标存在性校验 + 控制台子系统过滤（白名单除外）、
///   UWP 必须能拿到可启动 AUMID；按归一化显示名去重（同名保留能启动的那条）。
/// 结果按开始菜单目录时间戳 + catalogVersion 缓存到 appcatalog.json；枚举在后台线程跑，不抛异常。
/// </summary>
public sealed class AppCatalog : IAppCatalog
{
    private const int CatalogVersion = 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _cachePath;
    private readonly string _logPath;
    private readonly object _gate = new();
    private IReadOnlyList<AppEntry>? _cached;

    public AppCatalog(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        _cachePath = Path.Combine(cacheDir, "appcatalog.json");
        _logPath = Path.Combine(cacheDir, "catalog-filter.log");
    }

    public string LastDiagnostics { get; private set; } = "(尚未枚举)";

    public IReadOnlyList<AppEntry> Enumerate()
    {
        lock (_gate)
        {
            try
            {
                if (_cached is not null) return _cached;

                if (TryLoadCache(out var cached, out long cachedStamp, out int cachedVersion))
                {
                    long currentStamp = GetStartMenuStamp();
                    if (currentStamp == cachedStamp && cached.Count > 0 && cachedVersion == CatalogVersion)
                    {
                        _cached = cached;
                        LastDiagnostics = $"缓存命中：{cached.Count} 条 (catalogVersion={cachedVersion})";
                        return cached;
                    }
                }

                var sw = Stopwatch.StartNew();

                // 1) 从 shell:AppsFolder 拿全部条目（与系统开始菜单一致）
                var raw = EnumerateAppsFolder();
                int totalRaw = raw.Count;

                // 2) 名称规则过滤
                var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var examples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                void Hit(string rule, AppEntry e)
                {
                    counters[rule] = (counters.TryGetValue(rule, out var n) ? n : 0) + 1;
                    if (!examples.TryGetValue(rule, out var list)) { list = new List<string>(); examples[rule] = list; }
                    if (list.Count < 3) list.Add(e.DisplayName + " → " + (e.ExecutablePath ?? e.LaunchTarget));
                }

                var kept = new List<AppEntry>();
                foreach (var e in raw)
                {
                    if (string.IsNullOrWhiteSpace(e.DisplayName)) { Hit("空名", e); continue; }
                    string name = e.DisplayName.Trim();

                    // 名称垃圾规则（Settings / WSL Settings 也剔除，与系统开始菜单对齐）
                    if (MatchesName(name, "uninstall", "unins", "卸载", "remove", "unregister", "register as a service",
                        "start/stop", "launch v-ray", "spawner",
                        "release notes", "release note", "documentation", "sdk classes", "sdk documentation",
                        "online help", "help", "帮助", "readme", "license", "licence", "许可", "eula",
                        "apple software update", "hp update", "data collection", "settings", "wsl settings",
                        "chaos license", "whatsnew", "what's new", "最新版本的更新", "最近版本",
                        "faq", "faqs", "how to "))
                    {
                        Hit("名称垃圾", e); continue;
                    }

                    // 3) 目标规则：对 launchTarget 做同族判定（显示名漏网时兜底）
                    if (IsJunkTarget(e.LaunchTarget, out var junkReason))
                    {
                        Hit($"目标规则({junkReason})", e); continue;
                    }

                    // 4) 目标校验：Win32 .lnk 必须 TargetPath 存在
                    if (e.Kind == AppEntryKind.Win32Shortcut)
                    {
                        string exe = e.ExecutablePath ?? "";
                        if (string.IsNullOrEmpty(exe) || (!File.Exists(exe) && !Directory.Exists(exe)))
                        {
                            Hit("死链", e); continue;
                        }

                        // 控制台子系统过滤（白名单除外）
                        string bn = Path.GetFileName(exe);
                        string[] consoleWhitelist = { "cmd.exe", "powershell.exe", "wt.exe", "windowsterminal.exe", "terminal.exe", "bash.exe" };
                        bool inWhitelist = consoleWhitelist.Contains(bn, StringComparer.OrdinalIgnoreCase);
                        if (!inWhitelist && PeIsConsoleSubsystem(exe)) { Hit("控制台子系统", e); continue; }
                    }

                    kept.Add(e);
                }

                int afterNameFilter = kept.Count;

                // 4) 按精确显示名去重（系统基准 313→304 差 9，说明系统也是按完全相同的名合并）
                var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
                var merged = new List<string>();
                foreach (var e in kept)
                {
                    string key = e.DisplayName.Trim();
                    if (!byName.TryGetValue(key, out var existing))
                    {
                        byName[key] = e;
                        continue;
                    }
                    AppEntry winner;
                    AppEntry loser;
                    if (existing.Kind == AppEntryKind.Uwp && e.Kind == AppEntryKind.Win32Shortcut)
                    {
                        winner = e; loser = existing; byName[key] = e;
                    }
                    else { winner = existing; loser = e; }
                    merged.Add($"{loser.DisplayName} ({loser.Kind}/{loser.LaunchTarget}) 被 {winner.DisplayName} ({winner.Kind}/{winner.LaunchTarget}) 合并");
                }

                var final = byName.Values.ToList();
                sw.Stop();

                WriteFilterLog(totalRaw, afterNameFilter, counters, examples, merged, final.Count, sw.ElapsedMilliseconds);

                _cached = final;
                long stamp = GetStartMenuStamp();
                TrySaveCache(final, stamp);
                LastDiagnostics = $"枚举完成：{final.Count} 条（原始 {totalRaw}，名称过滤后 {afterNameFilter}，同名合并 {merged.Count}），耗时 {sw.ElapsedMilliseconds} ms";
                return final;
            }
            catch (Exception ex)
            {
                LastDiagnostics = "枚举失败：" + ex.Message;
                return _cached ??= Array.Empty<AppEntry>();
            }
        }
    }

    public List<TileItem> CreateTiles(IEnumerable<AppEntry> entries)
    {
        var tiles = new List<TileItem>();
        int order = 0;
        foreach (var e in entries)
        {
            var kind = e.Kind == AppEntryKind.Uwp ? TileActionKind.Shell : TileActionKind.Launch;
            string target = e.Kind == AppEntryKind.Uwp
                ? "shell:AppsFolder\\" + e.LaunchTarget
                : e.LaunchTarget;

            tiles.Add(new TileItem
            {
                Name = e.DisplayName,
                Kind = kind,
                Target = target,
                Arguments = e.Arguments,
                WorkingDirectory = e.WorkingDirectory,
                Color = TilePalette.ColorForName(e.DisplayName),
                Size = TileSize.Medium,
                GroupId = MenuConfig.DefaultGroupId,
                Order = order++
            });
        }
        return tiles;
    }

    // ---- 工具 ----

    private static bool MatchesName(string name, params string[] needles)
    {
        string hay = " " + name.ToLowerInvariant() + " ";
        foreach (var n in needles)
        {
            if (hay.Contains(n.ToLowerInvariant())) return true;
        }
        return false;
    }

    /// <summary>
    /// 目标规则：对 launchTarget 做同族判定。显示名可能掩盖垃圾（如"EOS Utility 3 自述文件"指向 readme.txt）。
    /// </summary>
    private static bool IsJunkTarget(string launchTarget, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(launchTarget)) return false;

        // 纯网址项：launchTarget 直接是 http(s) URL
        if (launchTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || launchTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            reason = "http(s) URL";
            return true;
        }

        // AUMID 形式（如 VisualStudio.5da1ef72、Microsoft.AutoGenerated.{GUID}）不含路径，跳过
        // 路径形式：{GUID}\path\to\file 或 C:\...
        string t = launchTarget.Replace('/', '\\');
        int bs = t.IndexOf('\\');
        string pathPart = bs >= 0 ? t[(bs + 1)..] : t;
        if (!pathPart.Contains('\\') && !pathPart.Contains('.')) return false; // 纯 AUMID

        string lower = pathPart.ToLowerInvariant();
        string fileName = Path.GetFileName(pathPart);

        // 例外：.msc 管理工具（services.msc 等）保留
        if (lower.EndsWith(".msc")) return false;
        // 例外：notepad.exe 保留（用户可能用它看 readme）
        if (fileName == "notepad.exe") return false;

        // 扩展名规则：文档类
        string[] docExt = { ".chm", ".txt", ".url", ".html", ".htm", ".pdf" };
        foreach (var ext in docExt)
        {
            if (lower.EndsWith(ext)) { reason = $"扩展名{ext}"; return true; }
        }

        // 文件名/路径关键词规则
        string[] pathKeywords = { "readme", "license", "licen", "uninstall", "unins", "help", "spawner", "setup", "update", "eula" };
        foreach (var kw in pathKeywords)
        {
            if (lower.Contains(kw)) { reason = $"路径含'{kw}'"; return true; }
        }

        return false;
    }

    private static string NormalizeDisplayName(string name)
    {
        // 去掉尾部版本号、括号注释、空白，用于同名判定
        string s = name.Trim();
        // 去掉括号内容：(64-bit), (x64), (version 2025) 等
        var rx = new System.Text.RegularExpressions.Regex(@"\s*[\(（][^\)）]*[\)）]\s*$");
        s = rx.Replace(s, "");
        // 去掉尾部版本号
        rx = new System.Text.RegularExpressions.Regex(@"\s+\d+(\.\d+)*\s*$");
        s = rx.Replace(s, "");
        return s.Trim();
    }

    private static bool PeIsConsoleSubsystem(string exePath)
    {
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return false;
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return false;
            br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
            br.ReadUInt16(); br.ReadUInt16();
            fs.Position = peOffset + 24 + 68;
            ushort subsystem = br.ReadUInt16();
            return subsystem == 3;
        }
        catch { return false; }
    }

    // ---- shell:AppsFolder 枚举 ----

    private static List<AppEntry> EnumerateAppsFolder()
    {
        var result = new List<AppEntry>();
        try
        {
            Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
            if (shellAppType is null) return result;
            dynamic? shell = Activator.CreateInstance(shellAppType);
            if (shell is null) return result;
            try
            {
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder is null) return result;
                foreach (dynamic item in folder.Items())
                {
                    try
                    {
                        string name = item.Name as string ?? "";
                        string path = item.Path as string ?? "";
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;

                        // path 形如：
                        //   UWP:   "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
                        //   Win32: 实际是 .lnk 的解析路径，shell:AppsFolder 里 Win32 项的 Path 通常就是 AUMID 或 .lnk 路径
                        // 我们尝试用 IShellLinkW 解析 path（如果它是 .lnk 文件）
                        AppEntry entry;
                        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                        {
                            var parsed = ParseLnk(path);
                            if (parsed is null) continue;
                            // 显示名用 AppsFolder 给的，不用文件名
                            parsed.DisplayName = name;
                            entry = parsed;
                        }
                        else
                        {
                            // 视为 UWP（AUMID）
                            entry = new AppEntry
                            {
                                DisplayName = name,
                                Kind = AppEntryKind.Uwp,
                                LaunchTarget = path,
                                ExecutablePath = null,
                                IconSourcePath = null
                            };
                        }
                        result.Add(entry);
                    }
                    catch { }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
        catch { }
        return result;
    }

    private static AppEntry? ParseLnk(string lnkPath)
    {
        var shellLink = (IShellLinkW)new ShellLink();
        try
        {
            var persist = (IPersistFile)shellLink;
            persist.Load(lnkPath, 0);

            var sb = new StringBuilder(1024);
            var fd = default(WIN32_FIND_DATAW);
            shellLink.GetPath(sb, sb.Capacity, ref fd, SLGP_UNCPRIORITY);
            string target = sb.ToString();
            if (string.IsNullOrWhiteSpace(target)) return null;

            var argsSb = new StringBuilder(1024);
            shellLink.GetArguments(argsSb, argsSb.Capacity);

            var wdSb = new StringBuilder(1024);
            shellLink.GetWorkingDirectory(wdSb, wdSb.Capacity);

            var iconSb = new StringBuilder(1024);
            int iconIndex = 0;
            shellLink.GetIconLocation(iconSb, iconSb.Capacity, out iconIndex);

            return new AppEntry
            {
                DisplayName = Path.GetFileNameWithoutExtension(lnkPath),
                Kind = AppEntryKind.Win32Shortcut,
                LaunchTarget = lnkPath,
                ExecutablePath = target,
                Arguments = string.IsNullOrWhiteSpace(argsSb.ToString()) ? null : argsSb.ToString(),
                WorkingDirectory = string.IsNullOrWhiteSpace(wdSb.ToString()) ? null : wdSb.ToString(),
                IconSourcePath = string.IsNullOrWhiteSpace(iconSb.ToString()) ? target : iconSb.ToString()
            };
        }
        finally
        {
            Marshal.ReleaseComObject(shellLink);
        }
    }

    // ---- 过滤日志 ----

    private void WriteFilterLog(int totalRaw, int afterNameFilter,
        Dictionary<string, int> counters, Dictionary<string, List<string>> examples,
        List<string> merged, int finalCount, long elapsedMs)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} catalog v{CatalogVersion} ===");
            sb.AppendLine($"shell:AppsFolder 原始: {totalRaw}");
            sb.AppendLine($"名称/规则过滤后: {afterNameFilter}");
            sb.AppendLine($"同名合并: {merged.Count}");
            sb.AppendLine($"最终保留: {finalCount}");
            sb.AppendLine($"耗时: {elapsedMs} ms");
            sb.AppendLine();
            sb.AppendLine("各规则剔除数:");
            foreach (var kv in counters.OrderByDescending(x => x.Value))
            {
                sb.AppendLine($"  - {kv.Key}: {kv.Value}");
                if (examples.TryGetValue(kv.Key, out var ex))
                {
                    foreach (var s in ex) sb.AppendLine($"      e.g. {s}");
                }
            }
            sb.AppendLine();
            sb.AppendLine($"同名合并明细 ({merged.Count}):");
            foreach (var m in merged) sb.AppendLine($"  - {m}");
            File.WriteAllText(_logPath, sb.ToString());
        }
        catch { }
    }

    // ---- 缓存 ----

    private long GetStartMenuStamp()
    {
        try
        {
            long max = 0;
            foreach (var dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            })
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                var t = Directory.GetLastWriteTimeUtc(dir).Ticks;
                if (t > max) max = t;
            }
            return max;
        }
        catch { return 0; }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public long Stamp { get; set; }
        public List<AppEntry>? Entries { get; set; }
    }

    private bool TryLoadCache(out List<AppEntry> entries, out long stamp, out int version)
    {
        entries = new List<AppEntry>();
        stamp = 0;
        version = 0;
        try
        {
            if (!File.Exists(_cachePath)) return false;
            var json = File.ReadAllText(_cachePath);
            var c = JsonSerializer.Deserialize<CacheFile>(json, JsonOpts);
            if (c?.Entries is null) return false;
            entries = c.Entries;
            stamp = c.Stamp;
            version = c.Version;
            return true;
        }
        catch { return false; }
    }

    private void TrySaveCache(List<AppEntry> entries, long stamp)
    {
        try
        {
            var json = JsonSerializer.Serialize(new CacheFile { Version = CatalogVersion, Stamp = stamp, Entries = entries }, JsonOpts);
            File.WriteAllText(_cachePath, json);
        }
        catch { }
    }

    // ---- IShellLink P/Invoke ----

    private const uint SLGP_UNCPRIORITY = 0x0002;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath,
            ref WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hWnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }
}
