# LiveLingo.Probe

无 UI 的采集、进程生命周期和字幕管道诊断工具。构建与测试环境见 [项目入口](../../README.md)。

## 采集

```powershell
$probe = './tools/LiveLingo.Probe/bin/Release/net10.0-windows10.0.26100.0/LiveLingo.Probe.exe'
& $probe targets --json
& $probe capture --pid <根PID> --output 'D:\Soft Ware\LiveLingo\data\capture\new-run' --duration-seconds 60
```

采集覆盖根进程的子孙；PID 必须对应当前实例。输出必须是新的非 C 盘目录，包含录音与诊断记录，不应加入 Git。Ctrl+C 请求正常停止，清理失败使用独立退出码。

`lifecycle-check` 检查进程所有权；`controlled-audio-check`、`scope-check`、`recovery-check` 和 `recovery-stop-check` 使用独立音源与浏览器实例。音频生成和频谱检查见 [夹具说明](../../tests/Fixtures/Audio/README.md)。这些检查会播放声音，不能代替实际设备拔插和睡眠验收。

## 受监管模型评测

`model-eval --request <JSON绝对路径> --output <新目录>` 接收 Executable、Arguments、WorkingDirectory、TimeoutSeconds 字段；参数数组不经 shell 展开。进程创建时加入自有 Job，结束时清理其子孙，不终止用户原有 Python/Ollama。评测脚本见 [模型工具](../model_eval/README.md)。

## 完整字幕管道

优先使用仓库脚本：

```powershell
./scripts/Invoke-SubtitlePipeline.ps1 -InputWave '<已有PCM16 WAV绝对路径>'
./scripts/Invoke-SubtitlePipeline.ps1 -TargetPid <根PID> -DurationSeconds 180
```

默认 cu130、Qwen 自动识别、TranslateGemma12B 串行，正常30秒窗口，对齐失败时最多120秒回退。可用 `-AlignmentRecoverySeconds 0` 关闭回退。每次运行使用私有模型服务、已准备资产和独立输出目录。

底层命令为 `pipeline-replay` / `pipeline-capture`；输出中的 `stop.request` 可触发正常取消。报告保留实际字幕版本和资源状态，仅显式评测保存正文。图形使用方式见 [用户指南](../../packaging/USER-GUIDE.md)。
