using System.Collections.Immutable;
using LiveLingo.Core.Desktop;
using LiveLingo.Core.History;
using LiveLingo.Core.Subtitles;
using LiveLingo.Desktop.Windows;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Windows.Tests;

/// <summary>用隔离文件验证桌面历史宿主的目录、失败与关闭生命周期，不启动采集或模型。</summary>
public sealed class DesktopHistoryTests
{
    /// <summary>正常会话结束后保存应用与模型信息，重新打开仍能读取完整正文。</summary><returns>宿主保存验证任务。</returns>
    [Fact]
    public async Task CompletedSessionSurvivesHostRestart()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            await using (var history = new DesktopHistory())
            {
                var recorder = Assert.IsAssignableFrom<IHistoryRecorder>(history.Begin(id, Target(), Settings(directory)));
                recorder.Offer(Snapshot(id, "完成的正文"));
                await history.CompleteAsync(recorder, null);
                Assert.Null(history.FailedRecorder); Assert.Null(history.MaintenanceError);
            }
            await using var reloaded = new JsonTranslationHistoryStore(directory);
            var document = await reloaded.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal("player.exe", document.Summary.Info.ApplicationName);
            Assert.Equal("Japanese", document.Summary.Info.SourceLanguage);
            Assert.Equal("translategemma:12b", document.Summary.Info.TranslationModel);
            Assert.Equal(HistorySessionState.Completed, document.Summary.State);
            Assert.Equal("完成的正文", Assert.Single(document.Entries).Segment.Source);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>切换已应用目录后，迟到的旧会话冲刷仍写旧库，新会话只进入新库。</summary><returns>目录切换验证任务。</returns>
    [Fact]
    public async Task SwitchingDirectoryPreservesRecorderOwnership()
    {
        var directory = NewDirectory(); var firstPath = Path.Combine(directory, "first"); var secondPath = Path.Combine(directory, "second");
        try
        {
            await using var history = new DesktopHistory();
            var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
            var first = Assert.IsAssignableFrom<IHistoryRecorder>(history.Begin(firstId, Target(), Settings(firstPath)));
            var firstStore = Assert.IsAssignableFrom<ITranslationHistoryStore>(history.Store);
            history.Configure(Settings(secondPath));
            var secondStore = Assert.IsAssignableFrom<ITranslationHistoryStore>(history.Store);
            Assert.NotSame(firstStore, secondStore);
            first.Offer(Snapshot(firstId, "属于旧目录"));
            await history.CompleteAsync(first, null);
            var second = Assert.IsAssignableFrom<IHistoryRecorder>(history.Begin(secondId, Target(), Settings(secondPath)));
            second.Offer(Snapshot(secondId, "属于新目录"));
            await history.CompleteAsync(second, null);
            Assert.Equal(firstId, Assert.Single((await firstStore.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions).SessionId);
            Assert.Equal(secondId, Assert.Single((await secondStore.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions).SessionId);
            history.Configure(Settings(firstPath));
            Assert.Same(firstStore, history.Store);
            Assert.Equal("属于旧目录", Assert.Single((await firstStore.ReadAsync(firstId, TestContext.Current.CancellationToken)).Entries).Segment.Source);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>显式验证根覆盖全部设置目录，合成记录始终不会写进用户历史库。</summary><returns>验证隔离检查任务。</returns>
    [Fact]
    public async Task VerificationRootRemainsIsolatedWhenSettingsChange()
    {
        var directory = NewDirectory(); var verification = Path.Combine(directory, "verification");
        var userFirst = Path.Combine(directory, "user-first"); var userSecond = Path.Combine(directory, "user-second");
        try
        {
            await using var history = new DesktopHistory(verification);
            var id = Guid.NewGuid();
            var recorder = Assert.IsAssignableFrom<IHistoryRecorder>(history.Begin(id, Target(), Settings(userFirst)));
            var originalStore = history.Store;
            history.Configure(Settings(userSecond));
            recorder.Offer(Snapshot(id, "合成验证内容"));
            await history.CompleteAsync(recorder, null);
            Assert.Same(originalStore, history.Store);
            Assert.Equal(Path.Combine(verification, "history"), history.Location);
            Assert.False(Directory.Exists(userFirst)); Assert.False(Directory.Exists(userSecond));
            Assert.Equal(id, Assert.Single((await history.Store!.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions).SessionId);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>删除最早失败会话后仍显示下一失败，新会话保存成功也不清除旧失败，明确重试后才恢复。</summary><returns>故障聚合与恢复验证任务。</returns>
    [Fact]
    public async Task OlderFailuresRemainVisibleUntilDeletedOrRecovered()
    {
        var directory = NewDirectory(); var obstruction = Path.Combine(directory, "history");
        try
        {
            await File.WriteAllTextAsync(obstruction, "occupied", TestContext.Current.CancellationToken);
            await using var history = new DesktopHistory();
            var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
            var first = Assert.IsAssignableFrom<IHistoryRecorder>(history.Begin(firstId, Target(), Settings(directory)));
            first.Offer(Snapshot(firstId, "首场未保存")); await history.CompleteAsync(first, null);
            var second = Assert.IsAssignableFrom<IHistoryRecorder>(history.Begin(secondId, Target(), Settings(directory)));
            second.Offer(Snapshot(secondId, "次场未保存")); await history.CompleteAsync(second, null);
            Assert.Same(first, history.FailedRecorder);
            await history.Store!.DeleteSessionsAsync([firstId], TestContext.Current.CancellationToken);
            Assert.Same(second, history.FailedRecorder);
            Assert.True(second.Status.CanRetry);
            File.Delete(obstruction);
            var successfulId = Guid.NewGuid();
            var successful = Assert.IsAssignableFrom<IHistoryRecorder>(history.Begin(successfulId, Target(), Settings(directory)));
            successful.Offer(Snapshot(successfulId, "新会话保存成功")); await history.CompleteAsync(successful, null);
            Assert.Same(second, history.FailedRecorder);
            await second.RetryAsync(TestContext.Current.CancellationToken);
            Assert.Null(history.FailedRecorder);
            Assert.Equal("次场未保存", Assert.Single((await history.Store.ReadAsync(secondId, TestContext.Current.CancellationToken)).Entries).Segment.Source);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>配置30天保留期触发后台清理，过期已结束记录删除，近期记录和活动记录保留。</summary><returns>启动清理验证任务。</returns>
    [Fact]
    public async Task StartupCleanupKeepsRecentAndActiveSessions()
    {
        var directory = NewDirectory(); var now = DateTimeOffset.UtcNow;
        try
        {
            var oldId = await SeedAsync(directory, now.AddDays(-31));
            var recentId = await SeedAsync(directory, now.AddDays(-29));
            await using var history = new DesktopHistory();
            history.Configure(Settings(directory));
            var activeId = Guid.NewGuid();
            var active = history.Store!.BeginSession(new(activeId, "active.exe", "Japanese", "translategemma:12b", now.AddDays(-60)));
            active.Offer(Snapshot(activeId, "仍在录制")); await active.RetryAsync(TestContext.Current.CancellationToken);
            history.Configure(Settings(directory) with { HistoryRetentionDays = 30 });
            await WaitForAsync(() => !Directory.Exists(Path.Combine(directory, "history", oldId.ToString("N"))));
            var page = await history.Store.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(page.Sessions, x => x.SessionId == recentId);
            Assert.Contains(page.Sessions, x => x.SessionId == activeId && x.State == HistorySessionState.Recording);
            Assert.DoesNotContain(page.Sessions, x => x.SessionId == oldId);
            await history.CompleteAsync(active, null);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>宿主退出会收敛未显式完成的记录，重新载入标识为中断并保留最后正文。</summary><returns>宿主退出验证任务。</returns>
    [Fact]
    public async Task DisposingHostFlushesOutstandingSession()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            await using (var history = new DesktopHistory())
            {
                var recorder = Assert.IsAssignableFrom<IHistoryRecorder>(history.Begin(id, Target(), Settings(directory)));
                recorder.Offer(Snapshot(id, "关闭前最后内容"));
            }
            await using var reloaded = new JsonTranslationHistoryStore(directory);
            var document = await reloaded.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(HistorySessionState.Interrupted, document.Summary.State);
            Assert.Equal("关闭前最后内容", Assert.Single(document.Entries).Segment.Source);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>创建本项测试独占的临时目录，沿用开发脚本配置的非C盘临时根。</summary><returns>新目录绝对路径。</returns>
    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "desktop-history-" + Guid.NewGuid().ToString("N"));
        DesktopSettings.ValidateDataPath(directory); Directory.CreateDirectory(directory); return directory;
    }

    /// <summary>创建无需真实进程查询的历史来源身份。</summary><returns>固定可读来源。</returns>
    private static DesktopTarget Target() => new("player.exe", new(123, DateTimeOffset.UtcNow, Path.Combine(Path.GetTempPath(), "player.exe")));

    /// <summary>创建不会自动清理测试夹具的配置。</summary><param name="directory">独占数据根。</param><returns>历史宿主配置。</returns>
    private static DesktopSettings Settings(string directory) => new() { DataPath = directory, AsrLanguage = "Japanese", HistoryRetentionDays = 0 };

    /// <summary>创建包含一条定稿的完整权威快照。</summary><param name="id">本场身份。</param><param name="text">合成正文。</param><returns>完整字幕快照。</returns>
    private static SubtitleSnapshot Snapshot(Guid id, string text) => new(new(id, 1, 1, 1), 1, false,
        [new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1, text, "译文：" + text, 1, SubtitlePhase.Final, TranslationState.Complete, null, BoundaryDecision.Pending)],
        ImmutableHashSet<Guid>.Empty);

    /// <summary>建立指定结束时间的已保存记录，用于检验宿主触发的保留期清理。</summary>
    /// <param name="directory">数据目录。</param><param name="endedAt">固定结束时间。</param><returns>新会话身份。</returns>
    private static async Task<Guid> SeedAsync(string directory, DateTimeOffset endedAt)
    {
        await using var store = new JsonTranslationHistoryStore(directory, new FixedTimeProvider(endedAt));
        var id = Guid.NewGuid();
        var recorder = store.BeginSession(new(id, "seed.exe", "Japanese", "translategemma:12b", endedAt.AddMinutes(-1)));
        recorder.Offer(Snapshot(id, "保留期夹具")); await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>在有限预算内等待后台维护结果，测试取消时立即退出。</summary><param name="condition">可观测完成条件。</param><returns>等待任务。</returns>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, budget.Token);
    }

    /// <summary>固定存储完成时刻，避免以修改真实系统时间构造历史夹具。</summary><param name="now">测试所需时间。</param>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => now;
    }
}
