using System.IO;
using System.IO.Pipes;
using System.Text;

namespace TileStartMenu.App.AppHost;

/// <summary>
/// 单实例：命名互斥量 + 命名管道。
/// 第一个实例创建互斥量并起管道服务端；第二个实例连上管道写一行命令，然后自行退出。
/// 协议（UTF-8 文本行）：
///   "SHOW"        — 唤起菜单（向后兼容：单字节 1 也视为 SHOW）
///   "PIN|&lt;path&gt;"  — 让已有实例把该路径固定为磁贴
///   "UNPIN|&lt;path&gt;"— 让已有实例取消固定该路径
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "Local\\TileStartMenu.SingleInstance.2026";
    private const string PipeName = "TileStartMenu.BringToFront.2026";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private Thread? _serverThread;

    /// <summary>
    /// 尝试取得单实例所有权。返回 true 表示这是第一个实例（调用方应继续启动）；
    /// 返回 false 表示已有实例在运行（调用方应立即退出）。
    /// </summary>
    public bool TryAcquire()
    {
        bool createdNew;
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out createdNew);
        if (!createdNew)
        {
            // 已有实例：尝试通过管道唤起它
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(timeout: 500);
                WriteLine(client, "SHOW");
                Thread.Sleep(200); // 让对方有时间收到
            }
            catch
            {
                // 对方可能刚要退出，忽略
            }
            return false;
        }
        return true;
    }

    /// <summary>已有实例在跑时，把一条命令转发给它（SHOW / PIN|path / UNPIN|path）。</summary>
    public static void ForwardCommand(string line)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 800);
            WriteLine(client, line);
            Thread.Sleep(300);
        }
        catch { }
    }

    private static void WriteLine(Stream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>启动管道服务端；收到第二个实例请求时回调 onCommand（SHOW / PIN|path / UNPIN|path）。</summary>
    public void StartServer(Action<string> onCommand)
    {
        _cts = new CancellationTokenSource();
        _serverThread = new Thread(() => ServerLoop(onCommand, _cts.Token))
        {
            IsBackground = true,
            Name = "TileStartMenu.SingleInstancePipe"
        };
        _serverThread.Start();
    }

    private static void ServerLoop(Action<string> onCommand, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var waitTask = server.WaitForConnectionAsync(ct);
                waitTask.Wait(ct);
                if (!server.IsConnected) continue;

                // 读一行；老客户端可能只发一个字节 1
                var ms = new MemoryStream();
                int b;
                while ((b = server.ReadByte()) >= 0)
                {
                    if (b == '\n') break;
                    ms.WriteByte((byte)b);
                    if (ms.Length > 8192) break;
                }
                string line = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                if (line.Length == 1 && line[0] == (char)1) line = "SHOW"; // 向后兼容
                if (string.IsNullOrEmpty(line)) line = "SHOW";
                onCommand(line);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                Thread.Sleep(200);
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
    }
}
