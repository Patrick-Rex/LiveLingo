using System.Net;
using LiveLingo.Desktop.Windows;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace LiveLingo.Windows.Tests;

/// <summary>验证限流回退、下载来源一致性和取消传播。</summary>
public sealed class GitHubReleaseSourceTests
{
    /// <summary>API 限流后从公开清单检查并通过同一来源下载。</summary><param name="status">限流响应码。</param><returns>回退验证任务。</returns>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RateLimitUsesFallbackForFeedAndDownload(HttpStatusCode status)
    {
        var primary = new FakeSource { Error = new HttpRequestException("limited", null, status) };
        var fallback = new FakeSource(); var source = new GitHubReleaseSource(primary, fallback);
        Assert.Same(fallback.Feed, await source.GetReleaseFeed(null!, "LiveLingo", "win-x64"));
        await source.DownloadReleaseEntry(null!, new(), "unused", _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal(1, fallback.Downloads); Assert.Equal(0, primary.Downloads);
    }

    /// <summary>正常 GitHub 检查继续保留其历史差分来源。</summary><returns>主来源验证任务。</returns>
    [Fact]
    public async Task SuccessfulPrimaryKeepsDownloadSource()
    {
        var primary = new FakeSource(); var fallback = new FakeSource(); var source = new GitHubReleaseSource(primary, fallback);
        Assert.Same(primary.Feed, await source.GetReleaseFeed(null!, "LiveLingo", "win-x64"));
        await source.DownloadReleaseEntry(null!, new(), "unused", _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal(1, primary.Downloads); Assert.Equal(0, fallback.Checks);
    }

    /// <summary>取消不能触发额外网络请求。</summary><returns>取消验证任务。</returns>
    [Fact]
    public async Task CancellationDoesNotStartFallback()
    {
        var fallback = new FakeSource(); var source = new GitHubReleaseSource(new FakeSource { Error = new OperationCanceledException() }, fallback);
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.GetReleaseFeed(null!, "LiveLingo", "win-x64"));
        Assert.Equal(0, fallback.Checks);
    }

    /// <summary>非限流错误保持真实失败，不掩盖为其他来源成功。</summary><returns>失败验证任务。</returns>
    [Fact]
    public async Task OtherFailureDoesNotStartFallback()
    {
        var fallback = new FakeSource(); var source = new GitHubReleaseSource(new FakeSource { Error = new HttpRequestException("server", null, HttpStatusCode.InternalServerError) }, fallback);
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetReleaseFeed(null!, "LiveLingo", "win-x64"));
        Assert.Equal(0, fallback.Checks);
    }

    /// <summary>记录来源选择而不访问网络或文件。</summary>
    private sealed class FakeSource : IUpdateSource
    {
        public Exception? Error { get; init; }
        public VelopackAssetFeed Feed { get; } = new() { Assets = [] };
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        /// <inheritdoc />
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        { Checks++; return Error is null ? Task.FromResult(Feed) : Task.FromException<VelopackAssetFeed>(Error); }
        /// <inheritdoc />
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        { Downloads++; return Task.CompletedTask; }
    }
}
