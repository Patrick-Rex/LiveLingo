using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LiveLingo.Core.Subtitles;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Windows.Tests.Processes;

/// <summary>使用实际受监管 Python 和本机 HTTP 验证整个模型组合生命周期，不加载 GPU。</summary>
public sealed class LocalPipelineModelsTests
{
    /// <summary>ASR 就绪与 Ollama 预热共享一个总限时，超时必须回收两个实际进程树。</summary>
    /// <returns>实际控制管道、HTTP 和退出核对任务。</returns>
    [Fact]
    public async Task StartupBudgetCoversAsrAndTranslationTogether()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LiveLingo.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        const string python = @"D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe";
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        var owned = new List<OwnedWorkerConnection>();
        var models = new LocalPipelineModels(epoch =>
        {
            var connection = new OwnedWorkerConnection(python, ["-u", Path.Combine(root.FullName, "tests", "model_eval", "ipc_worker_fixture.py"), "slowready", epoch.ToString()], root.FullName, "slow-asr-fixture");
            owned.Add(connection); return connection;
        }, () =>
        {
            var connection = new OwnedWorkerConnection(python, ["-u", Path.Combine(root.FullName, "tests", "model_eval", "ollama_worker_fixture.py"), port.ToString()], root.FullName, "slow-http-fixture");
            owned.Add(connection); return connection;
        }, new($"http://127.0.0.1:{port}/"), new("test", "test", "test", "test", "test"), new PipelineOptions { StartupTimeout = TimeSpan.FromSeconds(3) });
        var started = Stopwatch.StartNew();
        try
        {
            var error = await Record.ExceptionAsync(() => models.ResetAsync(new PipelineIdentity(Guid.NewGuid(), 1, 1, 1), new(), CancellationToken.None));
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(2, owned.Count);
        }
        finally { await models.DisposeAsync(); }
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
        Assert.All(owned.SelectMany(x => x.ObserveMembers()), identity =>
        {
            var actual = WindowsTargets.Query(identity.ProcessId).Identity;
            Assert.True(actual is null || !identity.SameInstance(actual));
        });
    }
}
