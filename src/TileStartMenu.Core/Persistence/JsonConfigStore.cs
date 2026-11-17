using System.Text.Json;
using System.Text.Json.Serialization;
using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Persistence;

/// <summary>基于 %LOCALAPPDATA%\TileStartMenu\menu.json 的配置存储，写入原子化。</summary>
public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly object _gate = new();
    private MenuConfig? _current;

    public JsonConfigStore(string? rootDir = null)
    {
        RootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TileStartMenu");
        ConfigPath = Path.Combine(RootDir, "menu.json");
        IconCacheDir = Path.Combine(RootDir, "icons");
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(IconCacheDir);
    }

    public string RootDir { get; }
    public string ConfigPath { get; }
    public string IconCacheDir { get; }

    public MenuConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= LoadCore();
            }
        }
    }

    public event EventHandler? Changed;

    public MenuConfig Load()
    {
        lock (_gate)
        {
            _current = LoadCore();
            return _current;
        }
    }

    public void Save(MenuConfig config)
    {
        lock (_gate)
        {
            config.Normalize();
            var json = JsonSerializer.Serialize(config, JsonOptions);

            // 原子写：先写临时文件，再替换，避免断电/崩溃留下半截文件
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            if (File.Exists(ConfigPath))
            {
                File.Replace(tmp, ConfigPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, ConfigPath);
            }

            _current = config;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private MenuConfig LoadCore()
    {
        if (!File.Exists(ConfigPath))
        {
            var seeded = CreateDefault();
            try
            {
                Save(seeded);
            }
            catch (IOException)
            {
                // 首次写入失败不致命，内存里仍有一份可用配置
            }
            return seeded;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<MenuConfig>(json, JsonOptions);
            if (cfg is null) return CreateDefault();
            cfg.Normalize();
            return cfg;
        }
        catch (Exception)
        {
            // 配置文件损坏：备份后重建，避免程序因配置打不开而无法使用
            try
            {
                File.Copy(ConfigPath, ConfigPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            }
            catch (IOException) { }
            var seeded = CreateDefault();
            try { Save(seeded); } catch (IOException) { }
            return seeded;
        }
    }

    /// <summary>首次运行的默认磁贴，全部指向本机真实存在的程序/位置。</summary>
    public static MenuConfig CreateDefault()
    {
        var cfg = new MenuConfig();
        cfg.Groups.Add(new TileGroup { Id = MenuConfig.DefaultGroupId, Name = "常用", Order = 0 });
        cfg.Groups.Add(new TileGroup { Id = "g-tools", Name = "工具", Order = 1 });
        cfg.Groups.Add(new TileGroup { Id = "g-web", Name = "网页", Order = 2 });

        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        cfg.Tiles.AddRange(new[]
        {
            new TileItem { Name = "资源管理器", Kind = TileActionKind.Launch, Target = "explorer.exe", Color = "#0078D4", Size = TileSize.Medium, GroupId = MenuConfig.DefaultGroupId, Order = 0 },
            new TileItem { Name = "记事本", Kind = TileActionKind.Launch, Target = "notepad.exe", Color = "#2D7D9A", Size = TileSize.Small, GroupId = MenuConfig.DefaultGroupId, Order = 1 },
            new TileItem { Name = "计算器", Kind = TileActionKind.Launch, Target = "calc.exe", Color = "#7A5EA8", Size = TileSize.Small, GroupId = MenuConfig.DefaultGroupId, Order = 2 },
            new TileItem { Name = "设置", Kind = TileActionKind.Shell, Target = "ms-settings:", Color = "#3A3A3A", Size = TileSize.Small, GroupId = MenuConfig.DefaultGroupId, Order = 3 },
            new TileItem { Name = "命令提示符", Kind = TileActionKind.Launch, Target = "cmd.exe", Color = "#1B1B1B", Size = TileSize.Small, GroupId = "g-tools", Order = 0 },
            new TileItem { Name = "PowerShell", Kind = TileActionKind.Launch, Target = "powershell.exe", Color = "#0B3A6E", Size = TileSize.Medium, GroupId = "g-tools", Order = 1 },
            new TileItem { Name = "用户目录", Kind = TileActionKind.Folder, Target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Color = "#B7472A", Size = TileSize.Small, GroupId = "g-tools", Order = 2 },
            new TileItem { Name = "Bing", Kind = TileActionKind.Url, Target = "https://www.bing.com", Color = "#0B7A5A", Size = TileSize.Medium, GroupId = "g-web", Order = 0 },
            new TileItem { Name = "GitHub", Kind = TileActionKind.Url, Target = "https://github.com", Color = "#24292E", Size = TileSize.Wide, GroupId = "g-web", Order = 1 }
        });

        cfg.Normalize();
        return cfg;
    }
}
