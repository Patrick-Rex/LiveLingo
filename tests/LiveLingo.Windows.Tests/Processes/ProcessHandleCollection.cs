namespace LiveLingo.Windows.Tests.Processes;

/// <summary>隔离主动设置可继承句柄的测试，避免同一测试进程内其他启动器继承人工哨兵。</summary>
[CollectionDefinition("ProcessHandleInheritance", DisableParallelization = true)]
public sealed class ProcessHandleCollection;
