using Xunit;

namespace Dc.Infrastructure.Tests;

// 计时敏感的测试（看门狗心跳超时判定）归入此集合并禁用并行：
// xUnit 默认并行跑各测试类，CI 共享 runner 的 CPU 被打满时，心跳消费线程会饥饿、
// LastHeartbeat 变陈旧，导致看门狗误判超时重启（flaky）。独占执行可消除这种争用。
[CollectionDefinition("Timing-Sensitive", DisableParallelization = true)]
public sealed class TimingSensitiveCollection { }
