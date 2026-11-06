using System.Net;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace LiveLingo.Desktop.Windows;

/// <summary>GitHub API 限流时使用同仓库公开发布清单，并让下载保持与检查来源一致。</summary>
/// <param name="primary">支持历史差分的 GitHub 来源。</param><param name="fallback">同仓库最新稳定发布的公开下载来源。</param>
public sealed class GitHubReleaseSource(IUpdateSource primary, IUpdateSource fallback) : IUpdateSource
{
    private IUpdateSource? selected;

    /// <inheritdoc />
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        selected = null;
        try
        {
            var feed = await primary.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
            selected = primary;
            return feed;
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var feed = await fallback.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
            selected = fallback;
            return feed;
        }
    }

    /// <inheritdoc />
    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default) =>
        (selected ?? throw new InvalidOperationException("请先成功检查更新。"))
            .DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
}
