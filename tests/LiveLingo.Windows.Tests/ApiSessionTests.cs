using System.Runtime.CompilerServices;
using LiveLingo.Core.Desktop;
using LiveLingo.Desktop.Windows;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Windows.Tests;

/// <summary>验证生产宿主在全 API 下不读取本地模型资源或创建进程。</summary>
public sealed class ApiSessionTests
{
    /// <summary>缺少 profile、worker 和全部本地模型路径仍可启动空输入会话并正常清理。</summary><returns>真实宿主生命周期检查任务。</returns>
    [Fact]
    public async Task AllApiSessionNeedsNoLocalAssetsOrProcesses()
    {
        var root = Path.Combine(Path.GetTempPath(), "api-session-" + Guid.NewGuid().ToString("N"));
        var api = new OpenAiApiSettings { Model = "test-model", BaseUrl = "https://example.invalid/v1" };
        var settings = new DesktopSettings { AsrBackend = ModelBackend.OpenAiCompatible, TranslationBackend = ModelBackend.OpenAiCompatible,
            AsrApi = api, TranslationApi = api, PythonPath = "", ModelPath = "", OllamaPath = "", OllamaModelsPath = "", DataPath = root, CachePath = Path.Combine(root, "cache") };
        var session = new DesktopSession(Path.Combine(root, "missing-content")) { VerificationInput = EmptyInput };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += update => { if (!update.Active) stopped.TrySetResult(); };
        try
        {
            await session.StartAsync(Guid.NewGuid(), new("fixture", new(123, DateTimeOffset.UtcNow, @"D:\fixture.exe")), settings, TestContext.Current.CancellationToken);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(session.LastError); Assert.Empty(session.LastOwned); Assert.Empty(session.RemainingTemporary);
        }
        finally
        {
            await session.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>提供无采集、无远程调用的已结束输入。</summary>
    /// <param name="id">本次会话。</param><param name="token">生命周期取消。</param><returns>空输入序列。</returns>
    private static async IAsyncEnumerable<PipelineInput> EmptyInput(Guid id, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield(); token.ThrowIfCancellationRequested(); yield break;
    }
}
