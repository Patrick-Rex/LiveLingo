using System.Net.WebSockets;
using System.Text.Json;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Processes;

namespace LiveLingo.Probe;

/// <summary>串行控制本次夹具创建的 Edge 调试端点，不连接用户浏览器。</summary>
internal sealed class FixtureDevTools : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private int nextId;

    /// <summary>解析启动器可能退出后的真实浏览器根，并核实创建时 Job 所有权。</summary>
    /// <param name="job">本次创建浏览器的 Job。</param><param name="profile">本次私有 profile。</param>
    /// <param name="token">等待上限。</param><returns>实际浏览器完整身份。</returns>
    public static async Task<TargetIdentity> ResolveBrowserAsync(OwnedJob job, string profile, CancellationToken token)
    {
        using var browser = await ConnectAsync(profile, token).ConfigureAwait(false);
        var processes = await browser.SendAsync("SystemInfo.getProcessInfo", new { }, token).ConfigureAwait(false);
        var pid = processes.GetProperty("processInfo").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "browser").GetProperty("id").GetInt32();
        var identity = WindowsTargets.Query(pid).Identity ?? throw new IOException("无法核实浏览器主进程。");
        if (!job.Contains(identity)) throw new IOException("实际浏览器不属于本次 Job。");
        return identity;
    }

    /// <summary>从新建私有 profile 的端口文件连接本地浏览器。</summary>
    /// <param name="profile">本次新建的 profile 目录。</param><param name="token">夹具总时限。</param>
    /// <returns>已连接并由调用者释放的串行客户端。</returns>
    public static async Task<FixtureDevTools> ConnectAsync(string profile, CancellationToken token)
    {
        var client = new FixtureDevTools();
        try
        {
            var file = Path.Combine(profile, "DevToolsActivePort");
            string[] lines = [];
            while (lines.Length < 2)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(file)) lines = await File.ReadAllLinesAsync(file, token).ConfigureAwait(false);
                if (lines.Length < 2) await Task.Delay(100, token).ConfigureAwait(false);
            }
            if (!int.TryParse(lines[0], out var port) || port is < 1 or > 65535 || !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                throw new InvalidDataException("私有 Edge 调试端点无效。");
            await client.socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), token).ConfigureAwait(false);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>发送单个有界请求；事件忽略，错误、断连和超限均显式失败。</summary>
    /// <param name="method">协议方法。</param><param name="parameters">方法参数。</param>
    /// <param name="token">夹具总时限。</param><param name="sessionId">可选标签页会话。</param>
    /// <returns>独立于接收缓冲的结果。</returns>
    public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token, string? sessionId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var id = ++nextId;
        var request = sessionId is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters })
            : JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters, sessionId });
        await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        var buffer = new byte[16384];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (received.MessageType != WebSocketMessageType.Text) throw new IOException("夹具调试连接意外结束。");
                if (message.Length + received.Count > 1024 * 1024) throw new IOException("夹具调试响应超过 1MiB。");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out var error)) throw new IOException(error.ToString());
            return root.GetProperty("result").Clone();
        }
    }

    /// <summary>终止仅属于本夹具的调试连接；浏览器进程由 Job 回收。</summary>
    public void Dispose() => socket.Dispose();
}
