using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LiveLingo.Core.Desktop;

namespace LiveLingo.Desktop.Windows;

/// <summary>提供有界的本机环境发现和用户触发的随包脚本执行。</summary>
public sealed partial class DesktopEnvironment
{
    /// <inheritdoc />
    public Task<DesktopSettings> DiscoverAsync(DesktopSettings settings, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.AsrBackend == ModelBackend.OpenAiCompatible && settings.TranslationBackend == ModelBackend.OpenAiCompatible) return settings;
        var roots = new List<string> { Path.GetDirectoryName(settings.DataPath)!, @"D:\Soft Ware\LiveLingo" };
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady).Take(26))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Soft Ware", "LiveLingo"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "LiveLingo"));
        }
        var executableDirectories = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama")
        };
        executableDirectories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Take(128));
        var virtualEnvironment = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
        if (!string.IsNullOrEmpty(virtualEnvironment)) executableDirectories.Add(Path.Combine(virtualEnvironment, "Scripts"));
        var modelRoots = new List<string> { @"D:\Soft Ware\Ollama" };
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            if (Environment.GetEnvironmentVariable("OLLAMA_MODELS", target) is { Length: > 0 } path) modelRoots.Add(path);
        modelRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models"));
        cancellationToken.ThrowIfCancellationRequested();
        return DiscoverCandidates(settings, roots, executableDirectories, modelRoots);
    }, cancellationToken);

    /// <summary>从有界候选位置复用缺失资产，保留用户已有路径和不兼容资产供显式处理。</summary>
    /// <param name="settings">当前配置。</param><param name="applicationRoots">资源根候选。</param>
    /// <param name="executableDirectories">PATH 或已知程序目录。</param><param name="translationRoots">翻译模型目录候选。</param>
    /// <returns>只替换缺失路径的配置副本。</returns>
    public static DesktopSettings DiscoverCandidates(DesktopSettings settings, IEnumerable<string> applicationRoots,
        IEnumerable<string> executableDirectories, IEnumerable<string> translationRoots)
    {
        var roots = applicationRoots.Where(IsResourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
        var executables = executableDirectories.Where(IsLocalPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
        var found = settings;
        if (settings.TranslationBackend == ModelBackend.Local && !File.Exists(settings.OllamaPath))
            found = found with { OllamaPath = executables.Select(x => Path.Combine(x, "ollama.exe")).FirstOrDefault(File.Exists) ?? settings.OllamaPath };
        if (settings.AsrBackend == ModelBackend.Local && !File.Exists(settings.PythonPath))
        {
            var interpreters = roots.Select(x => Path.Combine(x, "runtimes", "win-x64-cu130", "Scripts", "python.exe"))
                .Concat(executables.Where(IsResourcePath).Select(x => Path.Combine(x, "python.exe")));
            found = found with { PythonPath = interpreters.FirstOrDefault(IsRuntimeCandidate) ?? settings.PythonPath };
        }
        if (settings.AsrBackend == ModelBackend.Local && !File.Exists(Path.Combine(settings.ModelPath, "asset-manifest.json")))
            found = found with { ModelPath = roots.Select(x => Path.Combine(x, "models", "Qwen3-ASR-1.7B-7278e1e"))
                .FirstOrDefault(x => Inspect("Qwen", () => CheckQwen(x)).Ready) ?? settings.ModelPath };
        if (settings.TranslationBackend == ModelBackend.Local)
        {
            var tag = settings.TranslationModel.Split(':').Last();
            if (!File.Exists(Path.Combine(settings.OllamaModelsPath, "manifests", "registry.ollama.ai", "library", "translategemma", tag)))
                found = found with { OllamaModelsPath = translationRoots.Where(IsResourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(32)
                    .FirstOrDefault(x => Inspect("TranslateGemma", () => CheckTranslation(x, settings.TranslationModel)).Ready) ?? settings.OllamaModelsPath };
        }
        return found;
    }

    /// <summary>排除网络、相对和格式错误的候选路径。</summary><param name="path">候选路径。</param><returns>是否为本地绝对路径。</returns>
    private static bool IsLocalPath(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    /// <summary>维持大型资源非系统盘约束。</summary><param name="path">候选路径。</param><returns>是否允许复用。</returns>
    private static bool IsResourcePath(string path) => IsLocalPath(path) && !path.StartsWith("C:", StringComparison.OrdinalIgnoreCase);

    /// <summary>仅将具有固定推理包元数据的隔离环境列为候选，随后仍执行版本探测。</summary>
    /// <param name="path">候选解释器。</param><returns>是否具有隔离环境及所需包。</returns>
    private static bool IsRuntimeCandidate(string path)
    {
        if (!File.Exists(path)) return false;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        return File.Exists(Path.Combine(root, "pyvenv.cfg")) &&
            File.Exists(Path.Combine(root, "Lib", "site-packages", "torch-2.10.0+cu130.dist-info", "METADATA")) &&
            File.Exists(Path.Combine(root, "Lib", "site-packages", "qwen_asr-0.0.6.dist-info", "METADATA"));
    }

    /// <inheritdoc />
    public async Task InstallAsync(DependencyComponent component, DesktopSettings settings, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(component)) throw new ArgumentOutOfRangeException(nameof(component));
        settings.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (settings.AsrBackend != ModelBackend.Local && component is DependencyComponent.Runtime or DependencyComponent.QwenModel or DependencyComponent.Driver ||
            settings.TranslationBackend != ModelBackend.Local && component is DependencyComponent.Ollama or DependencyComponent.TranslationModel)
            throw new InvalidOperationException("当前来源不需要此本地组件。");
        var script = Path.Combine(contentRoot, "scripts", "Install-LiveLingoDependencies.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("当前构建没有依赖安装脚本，请使用正式安装包。", script);
        var directory = new DirectoryInfo(Path.Combine(settings.CachePath, "setup"));
        for (var ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装缓存不能经过目录链接。");
        var scratch = Path.Combine(directory.FullName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var snapshot = Path.Combine(scratch, "settings.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        var info = CreateInstallerStartInfo(script, component, snapshot);
        using var process = new Process { StartInfo = info };
        var started = false;
        var output = new StringBuilder(); var gate = new object();
        try
        {
            await File.WriteAllTextAsync(snapshot, JsonSerializer.Serialize(settings with { AsrApi = settings.AsrApi with { ApiKey = "" }, TranslationApi = settings.TranslationApi with { ApiKey = "" } }), timeout.Token).ConfigureAwait(false);
            started = process.Start();
            await Task.WhenAll(PumpInstallerAsync(process.StandardOutput, output, gate, progress, timeout.Token),
                PumpInstallerAsync(process.StandardError, output, gate, progress, timeout.Token),
                process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"安装脚本退出码 {process.ExitCode}：{output}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("安装超过两小时，请检查网络后重试。"); }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            File.Delete(snapshot); Directory.Delete(scratch);
        }
    }

    /// <summary>使用固定组件和独立参数执行随包脚本，不拼接命令或修改系统执行策略。</summary>
    /// <param name="script">随包脚本路径。</param><param name="component">显式选择的组件。</param><param name="snapshot">当前配置快照。</param>
    /// <returns>带有输出重定向的进程参数。</returns>
    public static ProcessStartInfo CreateInstallerStartInfo(string script, DependencyComponent component, string snapshot)
    {
        if (!Enum.IsDefined(component)) throw new ArgumentOutOfRangeException(nameof(component));
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Component", component.ToString(), "-SettingsPath", snapshot }) info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>并行消费安装输出，只保留最后8Ki字符并限制进度通知频率。</summary>
    /// <param name="reader">一个进程输出流。</param><param name="output">共享有界输出。</param><param name="gate">输出同步锁。</param>
    /// <param name="progress">界面进度。</param><param name="token">取消与超时。</param><returns>读取完成任务。</returns>
    private static async Task PumpInstallerAsync(StreamReader reader, StringBuilder output, object gate, IProgress<string> progress, CancellationToken token)
    {
        var buffer = new char[1024]; var timer = Stopwatch.StartNew(); int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            string? message = null;
            lock (gate)
            {
                output.Append(buffer, 0, count);
                if (output.Length > 8192) output.Remove(0, output.Length - 8192);
                if (timer.ElapsedMilliseconds >= 200) { message = output.ToString(); timer.Restart(); }
            }
            if (message is not null) progress.Report(message);
        }
    }
}
