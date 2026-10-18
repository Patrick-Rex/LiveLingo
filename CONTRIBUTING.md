# 贡献指南

## 开发环境

使用 Windows 11 x64、PowerShell 7 和仓库 global.json 指定的 .NET SDK。构建前运行 `. ./scripts/Enter-Development.ps1`，将缓存定向到非 C 盘。模型依赖按 [运行配置](runtime-profiles/README.md) 准备；一般单元测试不加载 GPU 模型。

## 修改与验证

- 将平台无关逻辑保留在 Core/Runtime，Windows API 保留在平台层。
- 保持字幕身份、版本和取消屏障；不要用删除真实重复、丢弃音频或接受过期结果隐藏积压。
- 手写 C# 类型、方法和构造函数添加 XML 文档注释；接口实现可使用有明确继承语义的 `inheritdoc`。
- 遵循 .editorconfig：UTF-8 无 BOM、LF、文件末尾换行；bat/cmd 使用 CRLF。
- 为行为变化补充对应测试，并同步使用说明。构建与测试入口见 [README](README.md) 和 [测试说明](tests/README.md)。
- 区分单元测试、模拟场景、真实设备和 GPU 推理，不将未执行的检查写成通过。
- 自己启动的程序和辅助进程应在验证后退出，保留用户已有应用。

提交说明使用 Conventional Commits，例如 `fix: 拒绝停止后的迟到字幕`。问题报告应包含预期行为、实际行为、复现步骤和依赖版本，移除路径中的个人信息。

## 入库范围

保留源代码、测试源码、生成夹具的脚本、依赖锁和面向使用者的文档。安装包、模型、录音、运行日志、测试结果、本机环境审计、内部计划与个人 agent 配置由 .gitignore 排除，留在本地。不要用 `git add -f` 将这些文件加入仓库。

提交前用 `git status --short` 检查文件列表。新文件应遵循同一边界，公开文档不能链接到忽略的本地资料。

## 许可证

贡献遵循项目 [Apache-2.0 许可证](LICENSE)。引入第三方代码时保留其许可与必要声明；不要加入无权再分发的模型或语料。
