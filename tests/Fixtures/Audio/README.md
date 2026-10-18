# M1 受控双音源

使用本机 FFmpeg 生成 44100Hz、stereo、PCM16、10 秒可循环音源：目标 440Hz，独立音源 997Hz，sine 后使用 volume=0.2。运行 `./tests/Fixtures/Audio/Generate.ps1`，默认保存到非 C 盘 `D:\Soft Ware\LiveLingo\data\fixtures\m1-tones-v1`，拒绝覆盖，记录 FFmpeg 版本和每个 WAV 的 SHA-256。

`target.html` 在 Edge 内播放 440Hz；独立音源由明确自有的 ProcessFixture 播放。频谱核验可证明选定范围的音调隔离，不能替代真实日语内容、设备恢复或两小时代表性播放。

输入元数据由 Generate.ps1 保存到输出目录的 manifest.json；音频和该生成清单均不入库。实录频谱检查使用 Python 标准库脚本：

```powershell
python ./tests/Fixtures/Audio/verify_tones.py '<实录 WAV 绝对路径>' --output '<新 spectrum.json 绝对路径>'
```

独立 997Hz 实录追加 --target-frequency 997 --excluded-frequency 440。算法对每个完整秒做 Goertzel 幅度计算，取中位数；阈值为目标幅度 >0.001、非目标/目标 <-40dB。不得将其当作日语识别质量评测。

多标签页夹具使用新目录生成，保留旧双音源输入：

```powershell
./tests/Fixtures/Audio/Generate.ps1 -Output 'D:\Soft Ware\LiveLingo\data\fixtures\unique-scope-tones' -IncludeSecondTab
```

新增 `second.wav` / `second.html` 为 660Hz，与原 440Hz 和独立 997Hz 一起写入该目录 manifest。`scope-check` 或 `field-scope-check` 保存六个四秒阶段的操作时钟及双路录音。验证使用：

```powershell
python ./tests/Fixtures/Audio/verify_scope.py '<证据目录>'
```

每阶段跳过首秒后取一秒稳定 PCM，逐音调检查存在/排除，静音阶段检查 RMS；全段另查包/帧连续、WAV 哈希和清理。输出 `scope-spectrum.json`，拒绝覆盖；没有完整六阶段或任一不符均失败。模拟音源通过不代表实际用户设备或浏览器组合通过。
