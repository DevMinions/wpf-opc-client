namespace Dc.App.Tests;

/// <summary>
/// 共享非并行集合：所有会读/写进程级单例 <c>LocalizationManager.Instance</c> 培养区
/// (按 culture 取本地化串、或 SetCulture 锁中文) 的测试归此集合串行执行。
/// 否则并行下一个测试 SetCulture(en) 会污染另一个期望 zh-CN 的测试,造成 flaky。
/// </summary>
[CollectionDefinition("I18nCulture", DisableParallelization = true)]
public sealed class I18nCultureCollection { }
