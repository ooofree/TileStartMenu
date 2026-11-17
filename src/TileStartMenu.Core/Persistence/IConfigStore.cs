using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Persistence;

/// <summary>菜单配置的读写入口。实现必须保证写入是原子的（不产生半截 JSON）。</summary>
public interface IConfigStore
{
    /// <summary>配置文件绝对路径（menu.json）。</summary>
    string ConfigPath { get; }

    /// <summary>图标缓存目录绝对路径。</summary>
    string IconCacheDir { get; }

    /// <summary>当前内存中的配置；首次访问时从磁盘加载（不存在则写入默认配置）。</summary>
    MenuConfig Current { get; }

    /// <summary>强制从磁盘重读。</summary>
    MenuConfig Load();

    /// <summary>原子写入磁盘并更新 Current，随后触发 Changed。</summary>
    void Save(MenuConfig config);

    /// <summary>保存后触发。</summary>
    event EventHandler? Changed;
}
