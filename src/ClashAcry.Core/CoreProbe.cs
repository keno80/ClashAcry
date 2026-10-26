using System.Net.Sockets;
namespace ClashAcry.Core;

// 裁定 R-8a：非 sealed + 实例方法 virtual，测试才能把"孤儿内核占着端口"与"我们的内核会来应答"分开表达。
public class CoreProbe(MihomoClient client)
{
    public virtual async Task<bool> ControllerAlive(CancellationToken ct)
    { try { await client.GetVersionAsync(ct); return true; } catch { return false; } }

    /// spawn 前的占用预检。默认就是真探测；测试覆盖它（见 R-8a）。
    public virtual Task<bool> ControllerPortOccupied(string host, int port, CancellationToken ct)
        => TcpAlive(host, port, ct);

    public static async Task<bool> TcpAlive(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1000);
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch { return false; }
    }
}
