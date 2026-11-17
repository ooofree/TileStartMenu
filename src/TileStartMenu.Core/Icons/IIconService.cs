using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Icons;

/// <summary>
/// 把磁贴目标的图标提取成 PNG 缓存文件，供界面直接以图片形式渲染（避免每次绘制都做提取）。
/// </summary>
public interface IIconService
{
    /// <summary>
    /// 返回 PNG 绝对路径；目标没有图标或提取失败时返回 null（界面回退到文字首字块）。
    /// 实现需要按 target+mtime 做缓存，重复调用不重复提取。
    /// </summary>
    string? GetIconPngPath(TileItem tile);

    /// <summary>清空缓存（磁贴目标改变后调用）。</summary>
    void Invalidate(TileItem tile);

    /// <summary>
    /// 异步请求图标：在后台线程提取（并发受限），完成后在 ThreadPool 线程回调 onReady。
    /// 若缓存已存在，立即同步返回路径；否则入队，回调里给路径或 null。
    /// 失败静默，不抛异常。
    /// </summary>
    void RequestIcon(TileItem tile, Action<string?> onReady);
}
