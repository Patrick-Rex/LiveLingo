using LiveLingo.Core.Desktop;
using Velopack;
using Velopack.Sources;

namespace LiveLingo.Desktop.Windows;

/// <summary>从固定公开仓库读取稳定更新，退出清理完成后才启动更新器。</summary>
public sealed class DesktopUpdates : IDesktopUpdates, IDisposable
{
    private readonly ReleaseDownloader downloader = new();
    private readonly UpdateManager manager;
    private UpdateInfo? available;
    private VelopackAsset? pending;

    /// <summary>创建固定 GitHub 来源，禁止隐式跨渠道或版本降级。</summary>
    public DesktopUpdates() => manager = new(new GitHubReleaseSource(
        new GithubSource("https://github.com/Patrick-Rex/LiveLingo", null, false, downloader),
        new SimpleWebSource("https://github.com/Patrick-Rex/LiveLingo/releases/latest/download/", downloader)));
    /// <inheritdoc />
    public string CurrentVersion => manager.CurrentVersion?.ToString() ?? "源码 / ZIP 安装";
    /// <inheritdoc />
    public bool IsInstalled => manager.IsInstalled;
    /// <inheritdoc />
    public string? PendingVersion => (pending ??= IsInstalled ? manager.UpdatePendingRestart : null)?.Version.ToString();

    /// <inheritdoc />
    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled) return null;
        downloader.OperationToken = cancellationToken;
        available = null;
        var result = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        available = result;
        return result?.TargetFullRelease.Version.ToString();
    }

    /// <inheritdoc />
    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        var update = available ?? throw new InvalidOperationException("请先检查新版本。");
        downloader.OperationToken = cancellationToken;
        await manager.DownloadUpdatesAsync(update, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        pending = update.TargetFullRelease;
    }

    /// <summary>清理完成后启动等待本进程退出的更新器；不强制结束 UI 或模型进程。</summary>
    /// <param name="arguments">保留独立设置等原始启动参数。</param>
    public void ApplyAfterExit(string[] arguments)
    {
        var asset = pending ?? manager.UpdatePendingRestart ?? throw new InvalidOperationException("没有已下载的更新。");
        manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true, restartArgs: arguments);
    }

    /// <inheritdoc />
    public void Dispose() => downloader.Dispose();
}

/// <summary>为不接受取消令牌的更新检查补充有界且可取消的网络请求。</summary>
internal sealed class ReleaseDownloader : IFileDownloader, IDisposable
{
    private readonly HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>获取或设置当前串行更新操作的取消令牌。</summary>
    public CancellationToken OperationToken { get; set; }

    /// <inheritdoc />
    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(OperationToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(url, headers);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("更新元数据超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <inheritdoc />
    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        System.Text.Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(OperationToken, cancelToken);
        cancel.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(timeout, 1, 30)));
        using var request = Request(url, headers);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancel.Token).ConfigureAwait(false);
        await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920]; long total = 0; var previous = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, cancel.Token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > 1024L * 1024 * 1024) throw new InvalidDataException("更新包超过 1 GiB 大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancel.Token).ConfigureAwait(false);
            var percent = response.Content.Headers.ContentLength is > 0 ? (int)Math.Min(100, total * 100 / response.Content.Headers.ContentLength.Value) : 0;
            if (percent != previous) { previous = percent; progress(percent); }
        }
        progress(100);
    }

    /// <summary>构造官方发布请求，禁止明文网络下载。</summary><param name="url">来源地址。</param><param name="headers">SDK 请求头。</param><returns>调用方负责释放的请求。</returns>
    private static HttpRequestMessage Request(string url, IDictionary<string, string>? headers)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("更新来源必须使用 HTTPS。");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LiveLingo-Updater/1.0");
        if (headers is not null) foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}
