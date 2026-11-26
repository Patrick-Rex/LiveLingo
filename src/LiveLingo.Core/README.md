# LiveLingo.Core

平台无关的音频区间、目标身份、会话状态机、字幕版本和桌面服务契约，不引用 Windows、Avalonia 或 MAF。

字幕归约器校验会话、采集代次和源版本，拒绝迟到结果；真实重复按身份及出现次序保留。语义块身份、音频范围和定稿标准不因短句翻译而改变。识别原文按句末、换行或子句停顿划分，连续无标点长段也能按阅读长度分句。同块修订按句序和完整原文一对一匹配已译短句，保留变动位置之后仍匹配的译文。待重译的位置可展示已有原译对并标明上一版，只有新版本译文参与定稿。对齐必须能确定上下文归属，不能通过硬切句或拼补模型文字隐藏失败。DesktopSettings 提供当前用户的资源路径、独立解释器和受支持的识别配置。桌面契约提供设置校验和工作区布局计算；SubtitleDisplayMode 定义双语、仅译文和仅原文显示，旧设置缺省双语，未知模式拒绝加载。

`AsrLanguages` 统一四种语言的配置名、界面标签和模型语言代码。`DesktopSettings` 默认识别 `Japanese`、译为 `Chinese`。转写代码 `zh` 表示中文语音，翻译目标 `zh-Hans` 表示简体中文。旧桌面设置中的源语言 `auto` 或缺失字段按日语读取。短句边界按所选语言判断，英语与韩语的稳定性比较保留词间分隔。

DesktopSettings 保存可选词库的开关、所选表和各表内容，旧设置默认关闭。每表的 LocalOnly 缺省为 false。每份表有独立的目标语言，旧表默认简体中文。TranslationGlossaryText 对目标语言不符或仅限本地却选用翻译 API 的词表返回空串。TranslationGlossary 编译当前表，每个短句先匹配完整译文，再按原词长度选择不重叠位置的相关术语。

History 提供会话摘要、采集代次内条目身份、固定正文快照和独立保存状态，以及记录、分页查询、删除、文本导出、保留期清理契约。记录器接收权威字幕快照，也可登记直播积压导致的未识别范围。范围使用所属采集代次内的 16 kHz 半开采样区间。存储与恢复实现位于 Runtime。

测试位于 tests/LiveLingo.Core.Tests，统一命令见 [测试说明](../../tests/README.md)。

DesktopMaintenance 定义带组件标识的环境初检结果、路径发现、用户触发的依赖安装及更新服务中立契约，不引用 Velopack、网络或平台进程 API；具体安装与检查位于 Windows 宿主。

ModelBackend 为识别和翻译提供各自独立的本地/API 选择，OpenAiApiSettings 使用 base_url、api_key、model。DesktopSettings 只校验所选来源，未使用的 API 默认空配置，旧设置保持双本地；数据和缓存约束始终有效。HistorySessionInfo.SourceLanguage 和 TargetLanguage 保存会话实际使用的识别语言和翻译目标，AsrModel 可选保存识别来源和模型。读取缺少目标语言的旧档案时沿用简体中文，无需改写历史文件。
