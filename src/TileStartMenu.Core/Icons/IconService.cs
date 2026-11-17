using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Icons;

/// <summary>
/// 真实图标提取：优先用 IShellItemImageFactory 取 256×256 高清彩色图标（Win32 exe / .lnk / UWP AUMID 通吃），
/// ExtractIconEx 大图标作为后备。结果用 PngBitmapEncoder 落盘到 IconCacheDir。
/// 异步队列 + 并发受限；失败写 icon-debug.log，返回 null 不抛异常。
/// </summary>
public sealed class IconService : IIconService
{
    // ---- Win32 常量 ----
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_ICONONLY = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        // 仅占位，实际不直接调用这些方法（通过强制转换 QI 到 IShellItemImageFactory）
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint uflags, out int piOrder);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage([In] SIZE size, [In] uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public uint xHotspot; public uint yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType; public int bmWidth; public int bmHeight; public int bmWidthBytes; public ushort bmPlanes; public ushort bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes;
        public ushort biBitCount; public uint biCompression; public uint biSizeImage;
        public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr h, int cb, out BITMAP pv);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[]? lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private readonly object _gate = new();
    private readonly SemaphoreSlim _iconSemaphore = new(2, 2);

    public IconService(string cacheDir)
    {
        CacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public string CacheDir { get; }

    public string? GetIconPngPath(TileItem tile)
    {
        if (tile is null) return null;
        lock (_gate)
        {
            try
            {
                var resolved = ResolveTarget(tile);
                if (resolved is not IconTarget target) return null;

                var pngPath = Path.Combine(CacheDir, target.CacheKey + ".png");
                if (File.Exists(pngPath)) return pngPath;

                IntPtr hbitmap = IntPtr.Zero;
                try
                {
                    hbitmap = LoadHighResBitmap(target.ParseName, out int w, out int h);
                    if (hbitmap == IntPtr.Zero)
                    {
                        // 后备：ExtractIconEx
                        IntPtr hIcon = LoadIconFallback(target);
                        if (hIcon == IntPtr.Zero)
                        {
                            Diag($"FAIL {tile.Target}: no bitmap, no icon");
                            return null;
                        }
                        try { SaveIconAsPng(hIcon, pngPath); }
                        finally { DestroyIcon(hIcon); }
                    }
                    else
                    {
                        SaveBitmapAsPng(hbitmap, w, h, pngPath);
                    }
                }
                finally
                {
                    if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
                }

                return File.Exists(pngPath) ? pngPath : null;
            }
            catch (Exception ex)
            {
                Diag($"EXCEPTION {tile.Target}: {ex.Message}");
                return null;
            }
        }
    }

    public void Invalidate(TileItem tile)
    {
        try
        {
            var resolved = ResolveTarget(tile);
            if (resolved is not IconTarget target) return;
            var pngPath = Path.Combine(CacheDir, target.CacheKey + ".png");
            if (File.Exists(pngPath)) File.Delete(pngPath);
        }
        catch { }
    }

    public void RequestIcon(TileItem tile, Action<string?> onReady)
    {
        var cached = GetIconPngPath(tile);
        if (cached is not null) { try { onReady(cached); } catch { } return; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _iconSemaphore.Wait();
                try { var p = GetIconPngPath(tile); try { onReady(p); } catch { } }
                finally { _iconSemaphore.Release(); }
            }
            catch { }
        });
    }

    // ---- 目标解析 ----

    private readonly record struct IconTarget(string ParseName, string CacheKey);

    private IconTarget? ResolveTarget(TileItem tile)
    {
        var target = (tile.Target ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(target)) return null;

        string parseName;
        switch (tile.Kind)
        {
            case TileActionKind.Launch:
            {
                var exe = ResolveExecutable(target);
                if (exe is null) return null;
                parseName = exe;
                break;
            }
            case TileActionKind.File:
                if (!File.Exists(target)) return null;
                parseName = target;
                break;
            case TileActionKind.Folder:
                if (!Directory.Exists(target)) return null;
                parseName = target;
                break;
            case TileActionKind.Shell when target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase):
                // UWP：直接用 shell:AppsFolder\AUMID 作为 parse name
                parseName = target;
                break;
            case TileActionKind.Url:
            case TileActionKind.Shell:
            default:
                return null;
        }

        string mtime = "0";
        try
        {
            if (File.Exists(parseName)) mtime = File.GetLastWriteTimeUtc(parseName).Ticks.ToString();
            else if (Directory.Exists(parseName)) mtime = Directory.GetLastWriteTimeUtc(parseName).Ticks.ToString();
        }
        catch { }

        var raw = tile.Kind + "|" + parseName + "|" + mtime;
        var bytes = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return new IconTarget(parseName, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    // ---- 高清图标（IShellItemImageFactory）----

    private static IntPtr LoadHighResBitmap(string parseName, out int width, out int height)
    {
        width = height = 0;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(parseName, IntPtr.Zero, ref iid, out object? obj);
            if (hr != 0 || obj is null) return IntPtr.Zero;

            try
            {
                var factory = (IShellItemImageFactory)obj;
                var size = new SIZE { cx = 256, cy = 256 };
                factory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out IntPtr hbmp);
                if (hbmp == IntPtr.Zero) return IntPtr.Zero;

                if (GetObjectW(hbmp, Marshal.SizeOf<BITMAP>(), out BITMAP bm) != 0)
                {
                    width = bm.bmWidth;
                    height = bm.bmHeight;
                }
                return hbmp;
            }
            finally
            {
                Marshal.ReleaseComObject(obj);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr LoadIconFallback(IconTarget target)
    {
        try
        {
            if (File.Exists(target.ParseName))
            {
                ExtractIconExW(target.ParseName, 0, out IntPtr large, out IntPtr small, 1);
                if (large != IntPtr.Zero) { if (small != IntPtr.Zero) DestroyIcon(small); return large; }
                return small;
            }
            if (Directory.Exists(target.ParseName))
            {
                var info = new SHFILEINFO();
                SHGetFileInfoW(target.ParseName, FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                return info.hIcon;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    // ---- 位图/HICON → PNG ----

    private static void SaveBitmapAsPng(IntPtr hbmp, int w, int h, string pngPath)
    {
        if (w <= 0 || h <= 0)
        {
            // 用 GetObject 拿不到尺寸，退回 GetDIBits 自己算
            if (GetObjectW(hbmp, Marshal.SizeOf<BITMAP>(), out BITMAP bm) != 0) { w = bm.bmWidth; h = bm.bmHeight; }
            if (w <= 0 || h <= 0) return;
        }

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = BI_RGB
        };
        byte[] pixels = new byte[w * h * 4];

        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            IntPtr memDc = CreateCompatibleDC(hdc);
            try
            {
                IntPtr old = SelectObject(memDc, hbmp);
                try { GetDIBits(memDc, hbmp, 0, (uint)h, pixels, ref bmi, DIB_RGB_COLORS); }
                finally { SelectObject(memDc, old); }
            }
            finally { DeleteDC(memDc); }
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        var source = BitmapSource.Create(w, h, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);
        using var fs = new FileStream(pngPath, FileMode.Create, FileAccess.ReadWrite);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        enc.Save(fs);
    }

    private static void SaveIconAsPng(IntPtr hIcon, string pngPath)
    {
        if (!GetIconInfo(hIcon, out ICONINFO ii)) return;
        try
        {
            if (ii.hbmColor == IntPtr.Zero) return;
            if (GetObjectW(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return;
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0) return;

            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = BI_RGB
            };
            byte[] pixels = new byte[w * h * 4];
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                IntPtr memDc = CreateCompatibleDC(hdc);
                try
                {
                    IntPtr old = SelectObject(memDc, ii.hbmColor);
                    try { GetDIBits(memDc, ii.hbmColor, 0, (uint)h, pixels, ref bmi, DIB_RGB_COLORS); }
                    finally { SelectObject(memDc, old); }
                }
                finally { DeleteDC(memDc); }
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }

            var source = BitmapSource.Create(w, h, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);
            using var fs = new FileStream(pngPath, FileMode.Create, FileAccess.ReadWrite);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(source));
            enc.Save(fs);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    private static string? ResolveExecutable(string target)
    {
        if (File.Exists(target)) return target;
        var candidates = new List<string>();
        if (Path.HasExtension(target)) candidates.Add(target);
        else { candidates.Add(target + ".exe"); candidates.Add(target + ".cmd"); candidates.Add(target + ".bat"); }
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
            if (Path.IsPathRooted(c)) continue;
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in pathDirs)
            {
                var full = Path.Combine(Environment.ExpandEnvironmentVariables(dir), c);
                if (File.Exists(full)) return full;
            }
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var inSys = Path.Combine(sys, c);
            if (File.Exists(inSys)) return inSys;
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var inWin = Path.Combine(winDir, c);
            if (File.Exists(inWin)) return inWin;
        }
        return null;
    }

    private static void Diag(string msg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TileStartMenu", "icon-debug.log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }
}
