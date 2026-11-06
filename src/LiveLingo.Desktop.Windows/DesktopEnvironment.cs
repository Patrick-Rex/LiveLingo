using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveLingo.Core.Desktop;

namespace LiveLingo.Desktop.Windows;

/// <summary>检查本地资产和运行环境元数据，不加载模型或启动 Ollama 服务。</summary>
/// <param name="contentRoot">当前程序内容目录。</param>
public sealed partial class DesktopEnvironment(string contentRoot) : IDesktopEnvironment
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentCheck>> CheckAsync(DesktopSettings settings, CancellationToken cancellationToken)
    {
        var results = (await Task.Run(() => CheckFiles(settings), cancellationToken).ConfigureAwait(false)).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (results[0].Ready)
        {
            results[0] = (await ProbeAsync("Python 推理环境", settings.PythonPath,
                ["-I", "-c", "import platform,importlib.metadata as m; assert platform.python_version()=='3.13.2'; assert m.version('torch')=='2.10.0+cu130'; assert m.version('qwen-asr')=='0.0.6'; print('Python 3.13.2 / PyTorch 2.10.0+cu130 / qwen-asr 0.0.6')"],
                cancellationToken).ConfigureAwait(false)) with { Component = DependencyComponent.Runtime };
        }
        var smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(smi)) smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        var driver = await ProbeAsync("NVIDIA 驱动", smi, ["--query-gpu=driver_version", "--format=csv,noheader"], cancellationToken).ConfigureAwait(false);
        if (driver.Ready && (string.IsNullOrWhiteSpace(driver.Message) || !driver.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(x => Version.TryParse(x.Trim(), out var version) && version.Major >= 580)))
            driver = new("NVIDIA 驱动", false, "CUDA 13.x 需要 r580 或更新驱动；请运行依赖脚本的 Driver 入口。");
        results.Add(driver with { Component = DependencyComponent.Driver });
        return results;
    }

    /// <summary>检查文件、模型身份和资产尺寸，不读取大型权重正文。</summary>
    /// <param name="settings">资源路径和所选翻译模型。</param><returns>四项本地资产检查结果。</returns>
    public static IReadOnlyList<EnvironmentCheck> CheckFiles(DesktopSettings settings) =>
    [
        new("Python 推理环境", File.Exists(settings.PythonPath), File.Exists(settings.PythonPath) ? "已找到解释器，继续检查环境版本。" : "未找到兼容的隔离环境，点击安装准备 Python 和推理依赖。", DependencyComponent.Runtime),
        Inspect("Qwen 模型", () => CheckQwen(settings.ModelPath)) with { Component = DependencyComponent.QwenModel },
        new("Ollama", File.Exists(settings.OllamaPath), File.Exists(settings.OllamaPath) ? "已找到 Ollama 程序。" : "未找到 Ollama，点击安装。", DependencyComponent.Ollama),
        Inspect("TranslateGemma 模型", () => CheckTranslation(settings.OllamaModelsPath, settings.TranslationModel)) with { Component = DependencyComponent.TranslationModel }
    ];

    /// <summary>把单个资产的读取失败转为可操作消息。</summary><param name="name">依赖名称。</param><param name="check">本地检查。</param><returns>独立结果。</returns>
    private static EnvironmentCheck Inspect(string name, Action check)
    {
        try { check(); return new(name, true, "文件与版本初检通过，启动翻译时继续完整校验。"); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        { return new(name, false, "资产缺失或不兼容：" + error.Message + " 缺失时可点击安装；已有不兼容资产不会被自动覆盖。"); }
    }

    /// <summary>限制清单读取大小，避免意外读取大文件。</summary><param name="path">清单路径。</param><returns>调用方负责释放的文档。</returns>
    private static JsonDocument ReadManifest(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("模型清单过大。");
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    /// <summary>检查固定识别模型 revision 和清单内文件长度。</summary><param name="root">模型根目录。</param>
    private static void CheckQwen(string root)
    {
        using var document = ReadManifest(Path.Combine(root, "asset-manifest.json"));
        var manifest = document.RootElement;
        if (manifest.GetProperty("repository").GetString() != "Qwen/Qwen3-ASR-1.7B" ||
            manifest.GetProperty("revision").GetString() != "7278e1e70fe206f11671096ffdd38061171dd6e5") throw new InvalidDataException("Qwen revision 不匹配。");
        var files = manifest.GetProperty("files");
        if (files.GetArrayLength() == 0) throw new InvalidDataException("Qwen 清单为空。");
        foreach (var file in files.EnumerateArray()) CheckAsset(root, file.GetProperty("path").GetString()!, file.GetProperty("bytes").GetInt64());
        if (!File.Exists(Path.Combine(root, "config.json"))) throw new InvalidDataException("缺少 Qwen 配置。");
    }

    /// <summary>检查所选翻译模型清单摘要与引用的本地 blob 长度。</summary><param name="root">Ollama 模型根。</param><param name="model">模型标签。</param>
    private static void CheckTranslation(string root, string model)
    {
        var tag = model switch { "translategemma:12b" => "12b", "translategemma:4b" => "4b", _ => throw new InvalidDataException("不支持的翻译模型。") };
        var path = Path.Combine(root, "manifests", "registry.ollama.ai", "library", "translategemma", tag);
        using var document = ReadManifest(path);
        var expected = tag == "12b" ? "c2f9a9ca1ec7149f2422581c89937ab0363d9399d9355514d753ee012e99c252" : "c49d986b0764f5881c476eb21435bb62b7abc62347aab3d4a6071e811be510a1";
        if (!Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))).Equals(expected, StringComparison.Ordinal)) throw new InvalidDataException("TranslateGemma 指纹不匹配。");
        foreach (var layer in document.RootElement.GetProperty("layers").EnumerateArray().Append(document.RootElement.GetProperty("config")))
        {
            var digest = layer.GetProperty("digest").GetString()!;
            if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
                throw new InvalidDataException("无效模型 blob 摘要。");
            CheckAsset(root, Path.Combine("blobs", digest.Replace(':', '-')), layer.GetProperty("size").GetInt64());
        }
    }

    /// <summary>拒绝清单越界路径和缺失文件。</summary><param name="root">资产根目录。</param><param name="relative">相对路径。</param><param name="length">预期长度。</param>
    private static void CheckAsset(string root, string relative, long length)
    {
        var directory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || length < 0 || new FileInfo(path).Length != length)
            throw new InvalidDataException("模型文件缺失、越界或长度不匹配。");
    }

    /// <summary>执行有限输出的短检测，取消和超时始终回收本次进程。</summary>
    /// <param name="name">检查名称。</param><param name="executable">绝对程序路径。</param><param name="arguments">独立参数列表。</param>
    /// <param name="cancellationToken">窗口生命周期。</param><returns>检查结果。</returns>
    private static async Task<EnvironmentCheck> ProbeAsync(string name, string executable, string[] arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable)) return new(name, false, "未找到检测程序；请运行依赖安装脚本或核对已安装路径。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var started = false;
        try
        {
            started = process.Start();
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return new(name, process.ExitCode == 0, process.ExitCode == 0 ? (await output.ConfigureAwait(false)).Trim() : "检查失败，请运行依赖安装脚本或核对路径：" + (await error.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(name, false, "检查超时，请核对环境后重试。"); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { return new(name, false, error.Message); }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }

    /// <summary>有界读取检测输出，超限即中止检测。</summary><param name="reader">子进程输出。</param><param name="token">检测预算。</param><returns>最多 16 Ki 字符的结果。</returns>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1024]; int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { if (result.Length + count > 16384) throw new IOException("依赖检测输出过大。"); result.Append(buffer, 0, count); }
        return result.ToString();
    }

}
