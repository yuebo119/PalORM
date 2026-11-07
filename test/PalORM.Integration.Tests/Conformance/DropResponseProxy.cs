using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PalORM.Integration.Tests.Conformance;

/// <summary>进程内 TCP 丢响应代理（CS-17 真实网络路径）。
/// <para><b>为什么需要</b>：驱动包装层注入能模拟「命令已执行后抛异常」，但那是进程内约定，
/// 不覆盖真实链路。本代理在 TCP 边界转发字节，在<b>观察到服务器已经返回响应</b>的那一刻切断
/// 连接，使「服务器已提交、客户端没收到 OK」成为真实网络行为，而不是测试桩约定。</para>
/// <para><b>触发判据</b>：客户端→服务器方向出现 <c>marker</c>（被测 SQL 前缀）时置「已武装」；
/// 服务器→客户端方向下一个数据块到达即丢弃并关闭双向连接。用「服务器响应已到达」而不是
/// 固定延迟，保证被丢弃的一定是<b>执行之后</b>的响应（固定延迟在慢机器上可能早于执行）。</para>
/// <para><b>协议无关</b>：纯字节转发，不解析 MySQL 协议；TLS 也能透传（本测试不用 TLS）。</para></summary>
internal sealed class DropResponseProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly string _marker;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _dropTriggered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;
    private readonly StringBuilder _clientTraffic = new();
    private int _armed;

    private DropResponseProxy(string upstreamHost, int upstreamPort, string marker)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _marker = marker;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync, _cts.Token);
    }

    internal static DropResponseProxy Start(string upstreamHost, int upstreamPort, string marker)
        => new(upstreamHost, upstreamPort, marker);

    /// <summary>代理监听端口（客户端连接串改指向这里）。</summary>
    internal int Port { get; }

    /// <summary>响应已被丢弃的完成信号（用例在断言前等待它，避免读到「尚未执行」的中间态）。</summary>
    internal Task DropTriggered => _dropTriggered.Task;

    /// <summary>最近一段客户端→服务器流量（ASCII，截断）。诊断用：标记未命中时用来确认实际 SQL 文本。</summary>
    internal string LastClientTraffic => _clientTraffic.ToString();

    private async Task AcceptLoopAsync()
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            using TcpClient upstream = new();
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _cts.Token).ConfigureAwait(false);
            Task toServer = PumpAsync(client.GetStream(), upstream.GetStream(), watchMarker: true);
            Task toClient = PumpAsync(upstream.GetStream(), client.GetStream(), watchMarker: false);
            await Task.WhenAny(toServer, toClient).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 代理自身的连接终止属预期（用例结束或已丢弃响应）
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, bool watchMarker)
    {
        byte[] buffer = new byte[8192];
        var seen = new StringBuilder();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int read = await from.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read == 0) return;

                // 服务器→客户端方向：已武装则丢弃这一块（它承载被中断命令的响应）
                if (!watchMarker && Volatile.Read(ref _armed) == 1)
                {
                    _dropTriggered.TrySetResult();
                    await _cts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (watchMarker)
                {
                    string chunk = Encoding.ASCII.GetString(buffer, 0, read);
                    seen.Append(chunk);
                    _clientTraffic.Append(chunk);
                    if (_clientTraffic.Length > 8192) _clientTraffic.Remove(0, _clientTraffic.Length - 4096);
                    // 先武装再转发：若先转发，服务器可能在本线程 await Write 期间就把响应发回来，
                    // 反向泵会在 armed 置位前把 OK 包正常转发，丢弃时机被错过（实测 30s 等待超时）。
                    if (seen.ToString().Contains(_marker, StringComparison.Ordinal))
                        Volatile.Write(ref _armed, 1);
                }

                await to.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                await to.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 取消/对端关闭：正常退出路径
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 关闭阶段的超时/取消无需上抛
        }
        _cts.Dispose();
    }
}
