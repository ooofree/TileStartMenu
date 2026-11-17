using System.IO.Pipes;

namespace TileStartMenu.App.AppHost;

/// <summary>
/// 单实例：命名互斥量 + 命名管道。
/// 第一个实例创建互斥量并起管道服务端；第二个实例连上管道写一个字节，请求已有实例显示菜单，然后自行退出。
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
                client.WriteByte(1);
                client.Flush();
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

    /// <summary>启动管道服务端；收到第二个实例请求时回调 onRequestBringToFront。</summary>
    public void StartServer(Action onRequestBringToFront)
    {
        _cts = new CancellationTokenSource();
        _serverThread = new Thread(() => ServerLoop(onRequestBringToFront, _cts.Token))
        {
            IsBackground = true,
            Name = "TileStartMenu.SingleInstancePipe"
        };
        _serverThread.Start();
    }

    private static void ServerLoop(Action onRequest, CancellationToken ct)
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
                server.ReadByte(); // 丢弃
                onRequest();
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                Thread.Sleep(200); // 出错后稍微退避
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
