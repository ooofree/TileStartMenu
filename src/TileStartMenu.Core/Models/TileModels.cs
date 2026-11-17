using System.Text.Json.Serialization;

namespace TileStartMenu.Core.Models;

/// <summary>磁贴尺寸，对应 Metro 布局网格（最小单元 = 1 格）。</summary>
public enum TileSize
{
    Small,   // 1x1
    Medium,  // 2x2
    Wide,    // 4x2
    Large    // 4x4
}

/// <summary>磁贴执行动作的类型。</summary>
public enum TileActionKind
{
    Launch,  // 启动可执行程序（绝对路径或 PATH 中的 exe 名）
    File,    // 用默认关联程序打开一个已存在的文件
    Folder,  // 打开一个已存在的目录
    Url,     // 用默认浏览器打开 http/https 链接
    Shell    // 交给 explorer 处理的 shell 目标，如 shell:AppsFolder\xxx 或 ms-settings:
}

/// <summary>一个磁贴。</summary>
public sealed class TileItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新磁贴";
    public TileActionKind Kind { get; set; } = TileActionKind.Launch;
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    /// <summary>磁贴底色，格式 "#RRGGBB"。</summary>
    public string Color { get; set; } = "#0078D4";
    public TileSize Size { get; set; } = TileSize.Medium;
    /// <summary>缓存好的 PNG 图标绝对路径；为空时界面显示文字首字。</summary>
    public string? IconPath { get; set; }
    /// <summary>是否固定到磁贴墙。false 表示从墙上移除但记录保留（Unpin 语义），Normalize 不得丢弃。</summary>
    public bool Pinned { get; set; } = true;
    /// <summary>电源动作；!= None 表示这是受保护的电源磁贴（不可 Unpin/删除）。</summary>
    public Power.PowerAction PowerAction { get; set; } = Power.PowerAction.None;
    /// <summary>图标缩放倍数（1.0=标准比例；可选 0.5/0.6/0.75/1.0）。Normalize 会把 &lt;=0 归一成 1.0。</summary>
    public double IconScale { get; set; } = 1.0;
    public string GroupId { get; set; } = "g-default";
    public int Order { get; set; }
    public bool ShowLabel { get; set; } = true;
}

/// <summary>磁贴分组（Win8 风格的分组标题）。</summary>
public sealed class TileGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新分组";
    public int Order { get; set; }
}

/// <summary>应用级设置。</summary>
public sealed class AppSettings
{
    /// <summary>是否接管 Win 键（关闭后恢复系统默认开始菜单行为）。</summary>
    public bool WinKeyEnabled { get; set; } = true;
    /// <summary>开机自启（写入 HKCU Run）。默认关闭，由用户在设置里开启。</summary>
    public bool LaunchAtStartup { get; set; } = false;
    public string BackgroundColor { get; set; } = "#0B0E14";
    public double BackgroundOpacity { get; set; } = 0.92;
    public bool ShowClock { get; set; } = true;
    /// <summary>Win 键短按判定上限（毫秒）：按下后超过该时长视为按住（交给系统处理）。</summary>
    public int MaxTapHoldMs { get; set; } = 700;

    /// <summary>首次运行是否已经用 IAppCatalog 枚举结果生成过"全部应用"磁贴。
    /// 之后永不自动补回用户删掉的磁贴；只有用户主动点"重置为全部应用"才重新生成。</summary>
    public bool AllAppsSeeded { get; set; } = false;

    /// <summary>种子数据结构版本。&lt;2 表示旧的"全量 547 磁贴"产物，启动时迁移到 v4 基础种子。</summary>
    public int SeedVersion { get; set; } = 0;
}

/// <summary>整个菜单的持久化模型，序列化为 menu.json。</summary>
public sealed class MenuConfig
{
    public int Version { get; set; } = 1;
    public List<TileGroup> Groups { get; set; } = new();
    public List<TileItem> Tiles { get; set; } = new();
    public AppSettings Settings { get; set; } = new();

    public const string DefaultGroupId = "g-default";

    /// <summary>修复引用、补齐顺序，保证配置自洽。Load 与 Save 前都会调用。</summary>
    public void Normalize()
    {
        if (Groups.Count == 0)
        {
            Groups.Add(new TileGroup { Id = DefaultGroupId, Name = "常用", Order = 0 });
        }

        for (int i = 0; i < Groups.Count; i++)
        {
            var g = Groups[i];
            if (string.IsNullOrWhiteSpace(g.Id)) g.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(g.Name)) g.Name = "分组";
            g.Order = i;
        }

        var knownGroupIds = Groups.Select(g => g.Id).ToHashSet();
        foreach (var t in Tiles)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) t.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(t.Color)) t.Color = "#0078D4";
            if (!knownGroupIds.Contains(t.GroupId)) t.GroupId = Groups[0].Id;
            if (double.IsNaN(t.IconScale) || t.IconScale <= 0) t.IconScale = 1.0;
        }

        // 每个分组内部按 Order 重新编号，保证拖拽排序结果稳定落地
        foreach (var g in Groups)
        {
            var inGroup = Tiles.Where(t => t.GroupId == g.Id).OrderBy(t => t.Order).ToList();
            for (int i = 0; i < inGroup.Count; i++) inGroup[i].Order = i;
        }
    }
}
