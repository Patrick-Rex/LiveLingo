using LiveLingo.Core.Subtitles;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Pipeline;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Windows.Tests.Processes;

/// <summary>真实匿名管道与 Python 协议夹具验证宿主；不启动 torch 或 GPU。</summary>
public sealed class AsrWorkerClientTests
{
    /// <summary>真实管道扩窗必须重传已释放的宿主样本，同时保持单调帧序号及精确范围。</summary>
    /// <returns>默认窗口后回退并再次前移的缓存验证任务。</returns>
    [Fact]
    public async Task ExpandedWindowReuploadsReleasedSamplesThroughActualPipe()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LiveLingo.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        OwnedWorkerConnection? owned = null;
        await using var manager = new AsrWorkerManager(epoch => owned = new OwnedWorkerConnection(
            @"D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe",
            ["-u", Path.Combine(root.FullName, "tests", "model_eval", "ipc_worker_fixture.py"), "expanded", epoch.ToString()],
            root.FullName, "worker-expanded-test"), new("test", "test", "test", "test", "test"),
            new() { AlignmentRecoverySamples = 1920000 });
        using var cache = new ContinuousAudioStore(new FileAudioSpillStore(Path.Combine(@"D:\Soft Ware\LiveLingo\cache\tmp",
            "worker-expanded-" + Guid.NewGuid().ToString("N"))), 1920000, 0, 1920000);
        var samples = new float[16000]; Array.Fill(samples, 0.25f);
        for (var start = 0; start < 1920000; start += 16000) cache.Append(start, samples);
        var identity = new PipelineIdentity(Guid.NewGuid(), 1, 1, 1);
        var key = new SegmentKey(Guid.NewGuid(), 1);
        await manager.StartAsync(1, CancellationToken.None);
        long sequence = 0;
        foreach (var range in new LiveLingo.Core.Audio.SampleRange[] { new(480000, 960000), new(0, 960000), new(480000, 1920000), new(0, 1920000) })
        {
            var request = new AsrRequest(new(Guid.NewGuid(), ++sequence, PipelineRequestKind.Recognition, identity, key,
                new(0, range.End), 0), range, range.Start, "auto", 2048);
            using var lease = cache.Acquire(range);
            var result = await manager.RecognizeAsync(request, lease, CancellationToken.None);
            Assert.Equal($"{range.Start}:{range.Length}", result.Text);
        }
        Assert.Equal(0, manager.Restarts);
        await manager.DisposeAsync();
        var after = WindowsTargets.Query(owned!.Process.Identity.ProcessId).Identity;
        Assert.True(after is null || !owned.Process.Identity.SameInstance(after));
    }

    /// <summary>每个音频包都及时完成也不能重置整个识别窗口的请求预算。</summary>
    /// <returns>真实缓慢管道上传超时及实际 worker 退出验证任务。</returns>
    [Fact]
    public async Task RecognitionDeadlineIncludesAllAudioUploads()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LiveLingo.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        OwnedWorkerConnection? owned = null;
        var manager = new AsrWorkerManager(epoch => owned = new OwnedWorkerConnection(
            @"D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe",
            ["-u", Path.Combine(root.FullName, "tests", "model_eval", "ipc_worker_fixture.py"), "slowaudio", epoch.ToString()],
            root.FullName, "worker-upload-deadline-test"), new("test", "test", "test", "test", "test"),
            new PipelineOptions { RequestTimeout = TimeSpan.FromMilliseconds(750) });
        var directory = Path.Combine(@"D:\Soft Ware\LiveLingo\cache\tmp", "worker-deadline-" + Guid.NewGuid().ToString("N"));
        using var cache = new ContinuousAudioStore(new FileAudioSpillStore(directory), 480000, 0, 480000);
        for (var start = 0; start < 480000; start += 16000) cache.Append(start, new float[16000]);
        var identity = new PipelineIdentity(Guid.NewGuid(), 1, 1, 1);
        var request = new AsrRequest(new(Guid.NewGuid(), 1, PipelineRequestKind.Recognition, identity,
            new(Guid.NewGuid(), 1), new(0, 480000), 0), new(0, 480000), 0, "auto", 512);
        try
        {
            await manager.StartAsync(1, CancellationToken.None);
            using var lease = cache.Acquire(request.WindowRange);
            var error = await Assert.ThrowsAsync<TimeoutException>(() => manager.RecognizeAsync(request, lease, CancellationToken.None));
            Assert.Equal("asr_window_deadline", error.Message);
            var after = WindowsTargets.Query(owned!.Process.Identity.ProcessId).Identity;
            Assert.True(after is null || !owned.Process.Identity.SameInstance(after));
        }
        finally { await manager.DisposeAsync(); }
    }

    /// <summary>失败实例退出后新代次只重放当前有效窗口，既有历史和旧请求都不重放。</summary>
    /// <returns>两次真实 Python 生命周期完成后的任务。</returns>
    [Fact]
    public async Task RestartReplaysOnlyCurrentRetainedWindow()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LiveLingo.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var owned = new List<OwnedWorkerConnection>();
        var directory = Path.Combine(@"D:\Soft Ware\LiveLingo\cache\tmp", "worker-replay-" + Guid.NewGuid().ToString("N"));
        using var cache = new ContinuousAudioStore(new FileAudioSpillStore(directory), 64000, 64000, 48000);
        for (var start = 0; start < 64000; start += 16000) cache.Append(start, new float[16000]);
        var manager = new AsrWorkerManager(epoch =>
        {
            var connection = new OwnedWorkerConnection(@"D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe",
                ["-u", Path.Combine(root.FullName, "tests", "model_eval", "ipc_worker_fixture.py"), epoch == 1 ? "badidentity" : "replay", epoch.ToString()],
                root.FullName, "worker-replay-test");
            owned.Add(connection); return connection;
        }, new("test", "test", "test", "test", "test"), new());
        var identity = new PipelineIdentity(Guid.NewGuid(), 1, 1, 1);
        var request = new AsrRequest(new(Guid.NewGuid(), 1, PipelineRequestKind.Recognition, identity, new(Guid.NewGuid(), 1), new(0, 48000), 0),
            new(0, 48000), 0, "auto", 512);
        try
        {
            await manager.StartAsync(1, CancellationToken.None);
            using (var lease = cache.Acquire(request.WindowRange))
                await Assert.ThrowsAsync<InvalidDataException>(() => manager.RecognizeAsync(request, lease, CancellationToken.None));
            await manager.StartAsync(2, CancellationToken.None);
            request = request with { Ticket = request.Ticket with { Id = Guid.NewGuid(), Identity = identity with { WorkerEpoch = 2 } }, WindowRange = new(16000, 48000), BodyStartSample = 16000 };
            using var current = cache.Acquire(request.WindowRange);
            var result = await manager.RecognizeAsync(request, current, CancellationToken.None);
            Assert.Equal("16000:32000", result.Text);
            Assert.Equal(2, result.WorkerEpoch);
            Assert.Equal(1, manager.Restarts);
        }
        finally { await manager.DisposeAsync(); }
        Assert.All(owned, connection =>
        {
            var before = connection.Process.Identity;
            var after = WindowsTargets.Query(before.ProcessId).Identity;
            Assert.True(after is null || !before.SameInstance(after));
        });
    }

    /// <summary>验证成功、损坏帧、身份错误和超时均清理实际自有进程。</summary>
    /// <param name="mode">受控夹具行为。</param><returns>实际进程退出后完成。</returns>
    [Theory]
    [InlineData("normal")]
    [InlineData("badframe")]
    [InlineData("badidentity")]
    [InlineData("hang")]
    public async Task ProtocolAndTimeoutRetireActualWorker(string mode)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LiveLingo.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var python = @"D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe";
        Assert.True(File.Exists(python), "需要已准备的 cu130 Python；本测试不加载模型。");
        await using var connection = new OwnedWorkerConnection(python,
            ["-u", Path.Combine(root.FullName, "tests", "model_eval", "ipc_worker_fixture.py"), mode], root.FullName, "ipc-fixture");
        var owned = connection.Process.Identity;
        var client = new AsrWorkerClient(connection, 1, new("test", "test", "test", "test", "test"),
            new PipelineOptions { RequestTimeout = TimeSpan.FromMilliseconds(500), StartupTimeout = TimeSpan.FromSeconds(10) });
        try
        {
            if (mode == "badframe")
                await Assert.ThrowsAsync<InvalidDataException>(() => client.InitializeAsync(CancellationToken.None));
            else
            {
                await client.InitializeAsync(CancellationToken.None);
                await client.PingAsync(CancellationToken.None);
                Assert.True(client.StderrBytes >= 1200000);
                var identity = new PipelineIdentity(Guid.NewGuid(), 1, 1, 1);
                await client.AppendAudioAsync(identity, 1, new(0, 16000), new float[16000], CancellationToken.None);
                var request = new AsrRequest(new(Guid.NewGuid(), 1, PipelineRequestKind.Recognition, identity,
                    new(Guid.NewGuid(), 1), new(0, 16000), 0), new(0, 16000), 0, "auto", 512);
                if (mode == "normal")
                {
                    var result = await client.RecognizeAsync(request, CancellationToken.None);
                    Assert.Equal(request, result.Correlation);
                    Assert.Equal("はい、はい", result.Text);
                    await client.ReleaseBeforeAsync(identity, 16000, CancellationToken.None);
                }
                else
                {
                    var failure = await Record.ExceptionAsync(() => client.RecognizeAsync(request, CancellationToken.None));
                    Assert.True(failure is InvalidDataException or TimeoutException, failure?.ToString());
                    Assert.True(client.Faulted);
                    var current = WindowsTargets.Query(owned.ProcessId).Identity;
                    Assert.True(current is null || !owned.SameInstance(current));
                }
            }
        }
        finally { await client.DisposeAsync(); }
        var after = WindowsTargets.Query(owned.ProcessId).Identity;
        Assert.True(after is null || !owned.SameInstance(after));
    }
}
