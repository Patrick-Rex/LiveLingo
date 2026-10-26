using System.Collections.Immutable;
using System.Text.Json;
using LiveLingo.Core.History;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Storage;
using Microsoft.Extensions.Time.Testing;

namespace LiveLingo.Runtime.Tests.Storage;

/// <summary>使用隔离真实文件验证历史快照、恢复、删除、导出与保留期，不启动模型。</summary>
public sealed class TranslationHistoryTests
{
    /// <summary>同代次修订和撤销正确替换，真实重复及恢复前正文跨重启保留。</summary><returns>历史持久化验证任务。</returns>
    [Fact]
    public async Task RevisionsRetirementsAndCaptureBoundariesSurviveRestart()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            await using (var store = new JsonTranslationHistoryStore(directory))
            {
                var recorder = store.BeginSession(Info(id));
                var first = Segment("同一句", 0); var second = Segment("同一句", 16000); var retired = Segment("已撤销", 32000);
                recorder.Offer(Snapshot(id, 1, 1, [first, second, retired]));
                var revised = first with { Source = "已修订", SourceRevision = 2, SubtitleRevision = 2, TranslatedSourceRevision = 1 };
                recorder.Offer(Snapshot(id, 1, 2, [revised, second], [retired.Key.Id]));
                recorder.Offer(Snapshot(id, 1, 1, [first, second, retired]));
                var next = Segment("恢复以后", 0);
                recorder.Offer(Snapshot(id, 2, 3, [next], [first.Key.Id, second.Key.Id, retired.Key.Id]));
                await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
                Assert.Null(recorder.Status.Error); Assert.False(recorder.Status.CanRetry);
            }
            await using var reloaded = new JsonTranslationHistoryStore(directory);
            var document = await reloaded.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(3, document.Entries.Count); Assert.Equal(new long[] { 1, 1, 2 }, document.Entries.Select(x => x.CaptureEpoch));
            Assert.Equal("已修订", document.Entries[0].Segment.Source); Assert.Equal("同一句", document.Entries[1].Segment.Source);
            Assert.Equal(16000, document.Entries[1].Segment.Range.Start); Assert.Equal(32000, document.Entries[1].Segment.Range.End);
            Assert.Equal(HistorySessionState.Completed, document.Summary.State);
            Assert.DoesNotContain(document.Entries, x => x.Segment.Source == "已撤销");
            var files = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories);
            Assert.Equal(3, files.Length);
            foreach (var path in files)
            {
                var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
                Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
                Assert.Equal((byte)'\n', bytes[^1]); Assert.DoesNotContain((byte)'\r', bytes);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>同文不同身份保持两条，页面摘要可搜索及分页而不读取损坏正文。</summary><returns>列表与真实重复验证任务。</returns>
    [Fact]
    public async Task SummariesPageAndSearchWithoutLoadingBodies()
    {
        var directory = NewDirectory(); var now = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        try
        {
            await using var store = new JsonTranslationHistoryStore(directory);
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            for (var i = 0; i < ids.Length; i++)
            {
                var recorder = store.BeginSession(Info(ids[i]) with { ApplicationName = i == 0 ? "player.exe" : "browser.exe", StartedAt = now.AddMinutes(i) });
                recorder.Offer(Snapshot(ids[i], 1, 1, [Segment("重复", 0), Segment("重复", 16000)]));
                await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            var page = await store.ListAsync(1, 1, "browser", TestContext.Current.CancellationToken);
            Assert.Equal(2, page.TotalCount); Assert.Equal(ids[1], Assert.Single(page.Sessions).SessionId);
            var document = await store.ReadAsync(ids[0], TestContext.Current.CancellationToken);
            Assert.Equal(2, document.Entries.Count); Assert.NotEqual(document.Entries[0].Key, document.Entries[1].Key);
            var body = Directory.GetFiles(Path.Combine(store.RootPath, ids[0].ToString("N")), "epoch-*.json").Single();
            await File.WriteAllTextAsync(body, "broken", TestContext.Current.CancellationToken);
            Assert.Equal(3, (await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).TotalCount);
            await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(ids[0], TestContext.Current.CancellationToken));
            Assert.Equal("broken", await File.ReadAllTextAsync(body, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>保存故障保持可导出的最新内存内容，修复目录后明确重试不会丢修订。</summary><returns>故障恢复验证任务。</returns>
    [Fact]
    public async Task StorageFailureKeepsLatestSnapshotAndCanRetry()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            var obstruction = Path.Combine(directory, "history");
            await File.WriteAllTextAsync(obstruction, "occupied", TestContext.Current.CancellationToken);
            await using var store = new JsonTranslationHistoryStore(directory);
            var recorder = store.BeginSession(Info(id)); var changes = new List<HistorySaveStatus>(); recorder.StatusChanged += changes.Add;
            var original = Segment("旧内容", 0);
            recorder.Offer(Snapshot(id, 1, 1, [original]));
            await recorder.RetryAsync(TestContext.Current.CancellationToken);
            Assert.True(recorder.Status.CanRetry); Assert.Contains("历史未完整保存", recorder.Status.Error);
            recorder.Offer(Snapshot(id, 1, 2, [original with { Source = "最新内容", SubtitleRevision = 2 }]));
            var text = await store.ExportAsync(id, HistoryExportFormat.Text, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("最新内容", text); Assert.Contains(changes, x => x.SessionId == id && x.CanRetry);
            File.Delete(obstruction);
            await recorder.RetryAsync(TestContext.Current.CancellationToken);
            Assert.Null(recorder.Status.Error);
            await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("最新内容", Assert.Single((await store.ReadAsync(id, TestContext.Current.CancellationToken)).Entries).Segment.Source);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>停止后的失败会话删除会永久撤销重试，不会再次创建已删除历史。</summary><returns>删除与待写竞争验证任务。</returns>
    [Fact]
    public async Task DeletingFailedStoppedSessionPreventsRetryResurrection()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            var obstruction = Path.Combine(directory, "history");
            await File.WriteAllTextAsync(obstruction, "occupied", TestContext.Current.CancellationToken);
            await using var store = new JsonTranslationHistoryStore(directory);
            var recorder = store.BeginSession(Info(id)); recorder.Offer(Snapshot(id, 1, 1, [Segment("正文", 0)]));
            await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(recorder.Status.CanRetry);
            await store.DeleteSessionsAsync([id], TestContext.Current.CancellationToken);
            File.Delete(obstruction);
            await recorder.RetryAsync(TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(store.RootPath)); Assert.False(recorder.Status.CanRetry);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>定向删除、清空及过期清理都在释放存储锁后发布错误清除状态。</summary>
    /// <param name="operation">删除入口。</param><returns>删除通知回归任务。</returns>
    [Theory]
    [InlineData("delete")]
    [InlineData("clear")]
    [InlineData("cleanup")]
    public async Task RemovalPublishesClearedStatusOutsideStorageLocks(string operation)
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "history"), "occupied", TestContext.Current.CancellationToken);
            await using var store = new JsonTranslationHistoryStore(directory, time);
            var recorder = store.BeginSession(Info(id) with { StartedAt = time.GetUtcNow() });
            recorder.Offer(Snapshot(id, 1, 1, [Segment("尚未保存", 0)]));
            await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(recorder.Status.CanRetry);
            var notified = new List<HistorySaveStatus>(); var listCompletedInsideNotification = false;
            recorder.StatusChanged += status =>
            {
                notified.Add(status);
                // 此处历史根为文件，查询无磁盘异步读取；若删除仍持有文件门，查询不会同步完成。
                var query = store.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
                listCompletedInsideNotification = query.IsCompletedSuccessfully;
            };
            if (operation == "delete") await store.DeleteSessionsAsync([id], TestContext.Current.CancellationToken);
            else if (operation == "clear") await store.ClearAsync(TestContext.Current.CancellationToken);
            else { time.Advance(TimeSpan.FromDays(8)); await store.CleanupAsync(7, TestContext.Current.CancellationToken); }
            var notification = Assert.Single(notified);
            Assert.Equal(id, notification.SessionId); Assert.Null(notification.Error); Assert.False(notification.CanRetry);
            Assert.True(listCompletedInsideNotification);
            await recorder.RetryAsync(TestContext.Current.CancellationToken); Assert.Single(notified);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>已落盘的部分保存会话仍保留通知登记，删除整场时宿主能清除永久缺失警告。</summary><returns>部分保存删除通知任务。</returns>
    [Fact]
    public async Task DeletingPersistedPartialSessionClearsItsWarning()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            await using var store = new JsonTranslationHistoryStore(directory);
            var recorder = store.BeginSession(Info(id));
            recorder.Offer(Snapshot(id, 1, 1, [Segment("已保留", 0)]));
            recorder.Offer(Snapshot(id, 1, 2, Enumerable.Range(0, 4097).Select(x => Segment("超量", x * 16000L)).ToImmutableArray()));
            await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(recorder.Status.Error); Assert.False(recorder.Status.CanRetry);
            var partial = await store.ReadAsync(id, TestContext.Current.CancellationToken);
            await store.DeleteEntriesAsync(id, [Assert.Single(partial.Entries).Key], TestContext.Current.CancellationToken);
            Assert.Empty((await store.ReadAsync(id, TestContext.Current.CancellationToken)).Entries);
            Assert.NotNull(recorder.Status.Error);
            var states = new List<HistorySaveStatus>(); recorder.StatusChanged += states.Add;
            await store.DeleteSessionsAsync([id], TestContext.Current.CancellationToken);
            Assert.Null(Assert.Single(states).Error); Assert.Null(recorder.Status.Error);
            Assert.False(Directory.Exists(Path.Combine(store.RootPath, id.ToString("N"))));
            await recorder.RetryAsync(TestContext.Current.CancellationToken); Assert.Single(states);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>活动会话禁止定向删除，清空仅移除已结束记录，单条和多选删除实际去除文件正文。</summary><returns>历史管理验证任务。</returns>
    [Fact]
    public async Task DeleteAndClearPreserveActiveSession()
    {
        var directory = NewDirectory(); var stoppedId = Guid.NewGuid(); var activeId = Guid.NewGuid();
        try
        {
            await using var store = new JsonTranslationHistoryStore(directory);
            var stopped = store.BeginSession(Info(stoppedId));
            stopped.Offer(Snapshot(stoppedId, 1, 1, [Segment("删除甲", 0), Segment("删除乙", 16000), Segment("保留", 32000)]));
            await stopped.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            var active = store.BeginSession(Info(activeId)); active.Offer(Snapshot(activeId, 1, 1, [Segment("正在翻译", 0)]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteSessionsAsync([stoppedId, activeId], TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteEntriesAsync(activeId, [new(1, Guid.NewGuid())], TestContext.Current.CancellationToken));
            var original = await store.ReadAsync(stoppedId, TestContext.Current.CancellationToken);
            await store.DeleteEntriesAsync(stoppedId, original.Entries.Take(2).Select(x => x.Key).ToArray(), TestContext.Current.CancellationToken);
            Assert.Equal("保留", Assert.Single((await store.ReadAsync(stoppedId, TestContext.Current.CancellationToken)).Entries).Segment.Source);
            var bodyPath = Directory.GetFiles(Path.Combine(store.RootPath, stoppedId.ToString("N")), "epoch-*.json").Single();
            var body = await File.ReadAllTextAsync(bodyPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("删除", body);
            await store.ClearAsync(TestContext.Current.CancellationToken);
            Assert.Equal(activeId, Assert.Single((await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions).SessionId);
            await active.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            await store.ClearAsync(TestContext.Current.CancellationToken);
            Assert.Empty((await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>未正常完成的磁盘摘要以中断状态恢复，最后成功正文仍可读取且坏摘要保留。</summary><returns>恢复验证任务。</returns>
    [Fact]
    public async Task InterruptedAndCorruptSessionsRemainVisibleWithoutDestructiveRecovery()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            await using var original = new JsonTranslationHistoryStore(directory);
            var recorder = original.BeginSession(Info(id)); recorder.Offer(Snapshot(id, 1, 1, [Segment("最后成功内容", 0)]));
            await recorder.RetryAsync(TestContext.Current.CancellationToken);
            await using var recovered = new JsonTranslationHistoryStore(directory);
            Assert.Equal(HistorySessionState.Interrupted, Assert.Single((await recovered.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions).State);
            Assert.Equal("最后成功内容", Assert.Single((await recovered.ReadAsync(id, TestContext.Current.CancellationToken)).Entries).Segment.Source);
            var corruptId = Guid.NewGuid(); var corruptDirectory = Path.Combine(original.RootPath, corruptId.ToString("N")); Directory.CreateDirectory(corruptDirectory);
            var corruptPath = Path.Combine(corruptDirectory, "summary.json");
            await File.WriteAllTextAsync(corruptPath, "{}", TestContext.Current.CancellationToken);
            var list = await recovered.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(list.Warnings); Assert.Single(list.Sessions); Assert.Equal("{}", await File.ReadAllTextAsync(corruptPath, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>导出冻结点击时版本，保留待更新标记，Markdown 不执行模型输出标记。</summary><returns>导出快照验证任务。</returns>
    [Fact]
    public async Task ExportFreezesActiveSnapshotAndPreservesRevisionStatus()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            await using var store = new JsonTranslationHistoryStore(directory);
            var recorder = store.BeginSession(Info(id));
            var segment = Segment("[链接](https://example.test)\r\n<script>", 0) with { SourceRevision = 2, TranslatedSourceRevision = 1 };
            recorder.Offer(Snapshot(id, 1, 1, [segment, Segment("未选中", 16000)]));
            var frozen = await store.ExportAsync(id, HistoryExportFormat.Markdown, [new(1, segment.Key.Id)], TestContext.Current.CancellationToken);
            recorder.Offer(Snapshot(id, 1, 2, [segment with { Source = "新版本", SubtitleRevision = 2 }]));
            Assert.Contains("译文待更新", frozen); Assert.Contains("\\[链接\\]", frozen); Assert.Contains("\\<script\\>", frozen);
            Assert.DoesNotContain("未选中", frozen); Assert.DoesNotContain("新版本", frozen); Assert.DoesNotContain('\r', frozen);
            Assert.Contains("翻译中", frozen); Assert.Contains("时间从本片段起算", frozen);
            Assert.Contains("识别语言：Japanese", frozen); Assert.Contains("翻译模型：translategemma:12b", frozen);
            Assert.Contains("结束：尚未结束", frozen); Assert.Contains(id.ToString(), frozen);
            await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            var completed = await store.ExportAsync(id, HistoryExportFormat.Text, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("结束：", completed); Assert.DoesNotContain("尚未结束", completed); Assert.Contains("状态：已结束", completed);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>三种容量边界的内容缺失永久标记为部分保存，重试和重启均不能伪装为完整记录。</summary>
    /// <param name="capacity">触发的容量边界。</param><returns>容量状态回归任务。</returns>
    [Theory]
    [InlineData("epochEntries")]
    [InlineData("sessionEntries")]
    [InlineData("captureEpochs")]
    public async Task CapacityLossRemainsPartialAfterCompletionRetryAndRestart(string capacity)
    {
        var directory = NewDirectory(); var id = Guid.NewGuid(); var retained = 1;
        try
        {
            await using (var store = new JsonTranslationHistoryStore(directory))
            {
                var recorder = store.BeginSession(Info(id));
                if (capacity == "epochEntries")
                {
                    recorder.Offer(Snapshot(id, 1, 1, [Segment("已保留", 0)]));
                    recorder.Offer(Snapshot(id, 1, 2, Enumerable.Range(0, 4097).Select(x => Segment("超量", x * 16000L)).ToImmutableArray()));
                    // 发生缺失后仍接收能够容纳的后续快照，错误不能被成功写入覆盖。
                    recorder.Offer(Snapshot(id, 2, 3, [Segment("继续翻译", 0)])); retained = 2;
                }
                else if (capacity == "sessionEntries")
                {
                    for (var epoch = 1; epoch <= 3; epoch++) recorder.Offer(Snapshot(id, epoch, epoch, Enumerable.Range(0, 4096).Select(x => Segment("已保留", x * 16000L)).ToImmutableArray()));
                    recorder.Offer(Snapshot(id, 4, 4, [Segment("未保留", 0)])); retained = 12288;
                }
                else
                {
                    for (var epoch = 1; epoch <= 257; epoch++) recorder.Offer(Snapshot(id, epoch, epoch, [Segment("已保留", 0)]));
                    recorder.Offer(Snapshot(id, 258, 258, [Segment("未保留", 0)])); retained = 257;
                }
                Assert.Contains("部分保存", recorder.Status.Error);
                await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
                await recorder.RetryAsync(TestContext.Current.CancellationToken);
                Assert.Contains("部分保存", recorder.Status.Error); Assert.False(recorder.Status.CanRetry); Assert.False(recorder.Status.IsSaving);
                var saved = await store.ReadAsync(id, TestContext.Current.CancellationToken);
                Assert.Equal(HistorySessionState.PartiallySaved, saved.Summary.State); Assert.Equal(retained, saved.Entries.Count);
                Assert.Contains("部分保存", saved.Summary.Error);
            }
            await using var reopened = new JsonTranslationHistoryStore(directory);
            var restored = await reopened.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(HistorySessionState.PartiallySaved, restored.Summary.State); Assert.Contains("重试无法恢复", restored.Summary.Error);
            Assert.Equal(retained, restored.Entries.Count);
            var export = await reopened.ExportAsync(id, HistoryExportFormat.Text, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("状态：部分保存", export); Assert.Contains("记录提示：历史仅部分保存", export);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>旧写入完成通知中到达的最终修订仍启动新写入，停止等待新任务而非已经结束的旧任务。</summary><returns>写入与停止竞争回归任务。</returns>
    [Fact]
    public async Task CompletionWaitsForRevisionOfferedAtWriterHandoff()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            await using var store = new JsonTranslationHistoryStore(directory);
            var recorder = store.BeginSession(Info(id)); var segment = Segment("第一次", 0);
            var completion = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously); var observed = 0;
            recorder.StatusChanged += status =>
            {
                if (status.IsSaving || status.Error is not null || Interlocked.Exchange(ref observed, 1) != 0) return;
                recorder.Offer(Snapshot(id, 1, 2, [segment with { Source = "最终修订", SubtitleRevision = 2 }]));
                completion.SetResult(recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken));
            };
            recorder.Offer(Snapshot(id, 1, 1, [segment]));
            var finish = await completion.Task.WaitAsync(TestContext.Current.CancellationToken); await finish;
            var document = await store.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal("最终修订", Assert.Single(document.Entries).Segment.Source); Assert.Equal(HistorySessionState.Completed, document.Summary.State);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>保留期只删除已结束且过期的会话，永久和活动会话都受到保护。</summary><returns>时间驱动清理任务。</returns>
    [Fact]
    public async Task RetentionUsesEndTimeAndNeverDeletesActiveSessions()
    {
        var directory = NewDirectory(); var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            await using var store = new JsonTranslationHistoryStore(directory, time);
            var oldId = Guid.NewGuid(); var activeId = Guid.NewGuid(); var recentId = Guid.NewGuid();
            var old = store.BeginSession(Info(oldId) with { StartedAt = time.GetUtcNow() });
            old.Offer(Snapshot(oldId, 1, 1, [Segment("过期", 0)])); await old.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            var active = store.BeginSession(Info(activeId) with { StartedAt = time.GetUtcNow() }); active.Offer(Snapshot(activeId, 1, 1, [Segment("仍活动", 0)]));
            time.Advance(TimeSpan.FromDays(31));
            var recent = store.BeginSession(Info(recentId) with { StartedAt = time.GetUtcNow() });
            recent.Offer(Snapshot(recentId, 1, 1, [Segment("新近", 0)])); await recent.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            await store.CleanupAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(3, (await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).TotalCount);
            await store.CleanupAsync(30, TestContext.Current.CancellationToken);
            var summaries = (await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions;
            Assert.Equal(2, summaries.Count); Assert.Contains(summaries, x => x.SessionId == activeId); Assert.Contains(summaries, x => x.SessionId == recentId);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CleanupAsync(14, TestContext.Current.CancellationToken));
            await active.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>长期会话中断时按最近保存时间保留，缺失新字段的旧摘要使用文件修改时间兼容恢复。</summary>
    /// <param name="legacy">是否模拟缺少保存时间的旧格式。</param><param name="hasEndTime">是否模拟已记录结束时间但之后才保存成功的错误会话。</param><returns>中断保留期回归任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InterruptedRetentionUsesLastSaveAndHonorsExpiryBoundary(bool legacy, bool hasEndTime)
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        var savedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero); var time = new FakeTimeProvider(savedAt);
        try
        {
            await using (var original = new JsonTranslationHistoryStore(directory, time))
            {
                var recorder = original.BeginSession(Info(id) with { StartedAt = savedAt.AddDays(-120) });
                recorder.Offer(Snapshot(id, 1, 1, [Segment("最近保存", 0)]));
                await recorder.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            var path = Path.Combine(directory, "history", id.ToString("N"), "summary.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
            var summary = json["Summary"]!.AsObject(); summary["State"] = hasEndTime ? 2 : 0;
            summary["EndedAt"] = hasEndTime ? System.Text.Json.Nodes.JsonValue.Create(savedAt.AddDays(-90)) : null;
            if (legacy) Assert.True(summary.Remove("LastSavedAt"));
            await File.WriteAllTextAsync(path, json.ToJsonString(), TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, savedAt.UtcDateTime);
            await using var recovered = new JsonTranslationHistoryStore(directory, time);
            var restored = Assert.Single((await recovered.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions);
            Assert.Equal(HistorySessionState.Interrupted, restored.State); Assert.Equal(savedAt, restored.LastSavedAt);
            await recovered.CleanupAsync(30, TestContext.Current.CancellationToken);
            Assert.Single((await recovered.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions);
            time.Advance(TimeSpan.FromDays(30));
            await recovered.CleanupAsync(30, TestContext.Current.CancellationToken);
            Assert.Single((await recovered.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions);
            time.Advance(TimeSpan.FromTicks(1));
            await recovered.CleanupAsync(30, TestContext.Current.CancellationToken);
            Assert.Empty((await recovered.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Sessions);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>取消查询或删除不修改已保存内容，关闭服务冲刷仍活动的最后版本。</summary><returns>取消与退出验证任务。</returns>
    [Fact]
    public async Task CancellationPreservesFilesAndDisposalFlushesLastVersion()
    {
        var directory = NewDirectory(); var id = Guid.NewGuid();
        try
        {
            var store = new JsonTranslationHistoryStore(directory);
            var recorder = store.BeginSession(Info(id)); recorder.Offer(Snapshot(id, 1, 1, [Segment("退出末次内容", 0)]));
            await store.DisposeAsync();
            await using var reopened = new JsonTranslationHistoryStore(directory);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopened.DeleteSessionsAsync([id], cancelled.Token));
            var result = await reopened.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal("退出末次内容", Assert.Single(result.Entries).Segment.Source); Assert.Equal(HistorySessionState.Interrupted, result.Summary.State);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>创建测试独占数据根，调用方最终删除。</summary><returns>隔离绝对目录。</returns>
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "translation-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }

    /// <summary>构造不包含任何私人路径的会话信息。</summary><param name="id">会话身份。</param><returns>测试会话。</returns>
    private static HistorySessionInfo Info(Guid id) => new(id, "test-player.exe", "Japanese", "translategemma:12b", DateTimeOffset.UtcNow);

    /// <summary>创建有原译文版本关系的字幕。</summary><param name="source">原文。</param><param name="start">代次内采样起点。</param><returns>独立字幕块。</returns>
    private static SubtitleSegment Segment(string source, long start) => new(new(Guid.NewGuid(), 1), new(start, start + 16000), 1, 1, source, "中文", 1, SubtitlePhase.Revised, TranslationState.Complete, null, BoundaryDecision.Pending);

    /// <summary>构造携带完整有效列表的权威快照。</summary>
    /// <param name="id">会话身份。</param><param name="epoch">采集代次。</param><param name="sequence">状态序号。</param><param name="segments">当前有效段。</param><param name="retired">已撤销身份。</param><returns>固定快照。</returns>
    private static SubtitleSnapshot Snapshot(Guid id, long epoch, long sequence, ImmutableArray<SubtitleSegment> segments, ImmutableHashSet<Guid>? retired = null) => new(new(id, epoch, epoch, 1), sequence, false, segments, retired ?? []);
}
