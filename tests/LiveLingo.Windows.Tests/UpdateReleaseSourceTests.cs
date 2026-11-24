using System.Net;
using System.Text;
using LiveLingo.Desktop.Windows;
using Velopack.Logging;
using Velopack.Sources;

namespace LiveLingo.Windows.Tests;

/// <summary>通过实际静态源解析清单并验证增量链与固定下载位置，不访问网络。</summary>
public sealed class UpdateReleaseSourceTests
{
    /// <summary>检查保留多个版本的完整包和连续差分，所有下载均使用更新专用 Release。</summary>
    /// <returns>清单解析与下载路由验证任务。</returns>
    [Fact]
    public async Task FeedAndEveryPackageUseDedicatedReleaseWithoutLosingDeltaChain()
    {
        var downloader = new FakeDownloader();
        var source = DesktopUpdates.CreateUpdateSource(downloader);
        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, "LiveLingo", "win-x64");

        Assert.Equal(5, feed.Assets.Length);
        Assert.Equal(2, feed.Assets.Count(asset => asset.Type.ToString() == "Delta"));
        Assert.Equal(["0.2.5", "0.2.6", "0.2.7"], feed.Assets.Select(asset => asset.Version.ToString()).Distinct());
        Assert.Equal(DesktopUpdates.UpdateBaseUrl + "releases.win-x64.json", Assert.Single(downloader.MetadataRequests).GetLeftPart(UriPartial.Path));
        foreach (var asset in feed.Assets)
        {
            await source.DownloadReleaseEntry(NullVelopackLogger.Instance, asset, "unused", _ => { }, TestContext.Current.CancellationToken);
            Assert.Equal(DesktopUpdates.UpdateBaseUrl + asset.FileName, downloader.PackageRequests[^1].AbsoluteUri);
        }
        Assert.Equal(feed.Assets.Length, downloader.PackageRequests.Count);
        Assert.Equal(TestContext.Current.CancellationToken, downloader.DownloadToken);
    }

    /// <summary>更新入口缺失或拒绝访问时报告原始失败，不查询 GitHub API 或普通版本附件。</summary>
    /// <param name="status">更新清单返回的 HTTP 错误。</param><returns>失败传播验证任务。</returns>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task UnavailableFeedDoesNotFallBackToInstallerRelease(HttpStatusCode status)
    {
        var error = new HttpRequestException("feed unavailable", null, status);
        var downloader = new FakeDownloader { Error = error };
        var source = DesktopUpdates.CreateUpdateSource(downloader);

        Assert.Same(error, await Assert.ThrowsAsync<HttpRequestException>(() =>
            source.GetReleaseFeed(NullVelopackLogger.Instance, "LiveLingo", "win-x64")));
        Assert.Equal(DesktopUpdates.UpdateBaseUrl + "releases.win-x64.json", Assert.Single(downloader.MetadataRequests).GetLeftPart(UriPartial.Path));
        Assert.Empty(downloader.PackageRequests);
    }

    /// <summary>取消检查立即传播，不访问替代地址或开始下载。</summary><returns>取消验证任务。</returns>
    [Fact]
    public async Task CancelledFeedDoesNotStartAnotherRequest()
    {
        var downloader = new FakeDownloader { Error = new OperationCanceledException() };
        var source = DesktopUpdates.CreateUpdateSource(downloader);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.GetReleaseFeed(NullVelopackLogger.Instance, "LiveLingo", "win-x64"));
        Assert.Single(downloader.MetadataRequests); Assert.Empty(downloader.PackageRequests);
    }

    /// <summary>返回与 Velopack 发布格式相同的多版本清单，并记录请求而不创建文件。</summary>
    private sealed class FakeDownloader : IFileDownloader
    {
        private const string Feed = """
            {"Assets":[
              {"PackageId":"LiveLingo","Version":"0.2.5","Type":"Full","FileName":"LiveLingo-0.2.5-win-x64-full.nupkg","SHA1":"1111111111111111111111111111111111111111","Size":100},
              {"PackageId":"LiveLingo","Version":"0.2.6","Type":"Full","FileName":"LiveLingo-0.2.6-win-x64-full.nupkg","SHA1":"2222222222222222222222222222222222222222","Size":110},
              {"PackageId":"LiveLingo","Version":"0.2.6","Type":"Delta","FileName":"LiveLingo-0.2.6-win-x64-delta.nupkg","SHA1":"3333333333333333333333333333333333333333","Size":10},
              {"PackageId":"LiveLingo","Version":"0.2.7","Type":"Full","FileName":"LiveLingo-0.2.7-win-x64-full.nupkg","SHA1":"4444444444444444444444444444444444444444","Size":120},
              {"PackageId":"LiveLingo","Version":"0.2.7","Type":"Delta","FileName":"LiveLingo-0.2.7-win-x64-delta.nupkg","SHA1":"5555555555555555555555555555555555555555","Size":10}
            ]}
            """;

        /// <summary>获取模拟的清单下载错误；为空时返回完整清单。</summary>
        public Exception? Error { get; init; }
        /// <summary>获取实际清单请求地址。</summary>
        public List<Uri> MetadataRequests { get; } = [];
        /// <summary>获取实际包请求地址。</summary>
        public List<Uri> PackageRequests { get; } = [];
        /// <summary>获取最近一次包下载接收的取消令牌。</summary>
        public CancellationToken DownloadToken { get; private set; }

        /// <inheritdoc />
        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            MetadataRequests.Add(new(url));
            return Error is null ? Task.FromResult(Feed) : Task.FromException<string>(Error);
        }

        /// <inheritdoc />
        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            Encoding.UTF8.GetBytes(await DownloadString(url, headers, timeout));

        /// <inheritdoc />
        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            cancelToken.ThrowIfCancellationRequested();
            PackageRequests.Add(new(url)); DownloadToken = cancelToken;
            return Task.CompletedTask;
        }
    }
}
