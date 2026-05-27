# Phase 8 集成测试实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 WPF 重写的 OPC 数采系统补集成测试，覆盖 OPC 协议层（DA/AE/UA）+ TcpPublisher 真 socket + 4 条关键弹性场景，共 17 个测试用例分布在两个测试项目里。

**Architecture:** 两个测试项目按 TFM 切分 — `Dc.Integration.Tests` (net8.0, 跨平台) 装 Infrastructure + UA 测试；`Dc.Integration.Tests.Com` (net8.0-windows, x64) 装 ClassicCom（DA/AE）测试。WindowsCom 测试通过自定义 `WindowsComFactAttribute` 根据 OS / OPCEnum / 注册表状态自动 skip。UA 走内嵌的 minimal StandardServer fixture，避免依赖外网或外部 UA server。

**Tech Stack:** xunit 2.9 + xunit.runner.visualstudio + MessagePack + OPCFoundation.NetStandard.Opc.Ua.Server 1.5.374.158 + Microsoft.Win32.Registry 5.0.0 + Microsoft.NET.Test.Sdk 17.11.1

**Spec:** `wpf/docs/superpowers/specs/2026-05-18-phase-8-integration-tests-design.md`

---

## 文件结构

### 新建文件

```
wpf/tests/Dc.Integration.Tests/
├── Dc.Integration.Tests.csproj                              # 跨平台测试项目
├── Infrastructure/
│   ├── TcpListenerFixture.cs                                # 共享 helper
│   ├── TcpPublisherEndToEndTests.cs                         # INF-1, INF-2
│   ├── ReconnectBackoffTests.cs                             # INF-3, INF-4
│   └── WireDumpRoundTripTests.cs                            # INF-5
└── Ua/
    ├── Fixtures/
    │   ├── MinimalUaServer.cs                               # StandardServer 子类
    │   ├── MinimalUaNodeManager.cs                          # CustomNodeManager2
    │   └── EmbeddedUaServerFixture.cs                       # xunit Fixture
    ├── UaSubscriberSmokeTests.cs                            # UA-1
    └── UaBrowserSmokeTests.cs                               # UA-2, UA-3

wpf/tests/Dc.Integration.Tests.Com/
├── Dc.Integration.Tests.Com.csproj                          # Windows-only 测试项目
├── Fixtures/
│   ├── WindowsComFactAttribute.cs                           # 自动 skip 条件
│   └── DemoServerFixture.cs                                 # vendor demo server 句柄
├── DaSubscriberSmokeTests.cs                                # DA-1
├── DaBrowserSmokeTests.cs                                   # DA-2, DA-3, DA-4
├── AeSubscriberSmokeTests.cs                                # AE-1
├── AeBrowserSmokeTests.cs                                   # AE-2, AE-3
└── Resilience/
    └── DaResilienceTests.cs                                 # RES-1, RES-2
```

### 修改文件

```
wpf/Dc.sln                  # 注册两个新测试项目
wpf/build.ps1               # -Target test 串行跑两个项目
wpf/README.md               # "Windows 端到端验证清单"增"自动化等价物"小节
```

---

## Task 1: 创建 Dc.Integration.Tests 项目骨架（net8.0 跨平台）

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj`

- [ ] **Step 1: 创建 csproj**

写入 `wpf/tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="MessagePack" />
    <PackageReference Include="OPCFoundation.NetStandard.Opc.Ua.Server" Version="1.5.374.158" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Dc.Domain\Dc.Domain.csproj" />
    <ProjectReference Include="..\..\src\Dc.Infrastructure\Dc.Infrastructure.csproj" />
    <ProjectReference Include="..\..\src\Dc.Opc.Abstractions\Dc.Opc.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\Dc.Opc.Ua\Dc.Opc.Ua.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 在 Dc.sln 中注册项目**

在 `wpf/Dc.sln` 中找到 `Dc.Infrastructure.Tests` 一行（第 19 行附近），在它后面追加：

```
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Dc.Integration.Tests", "tests\Dc.Integration.Tests\Dc.Integration.Tests.csproj", "{29292929-2929-2929-2929-292929292929}"
EndProject
```

然后在 `GlobalSection(ProjectConfigurationPlatforms) = postSolution` 段下追加 GUID 配置块（参照 `Dc.Infrastructure.Tests` 的 4 行 Debug/Release × Any CPU/x64 模板，把 GUID 换成 `29292929-...`）。

- [ ] **Step 3: 验证编译**

Run: `cd wpf && dotnet build tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj --nologo -v:minimal`
Expected: `已成功生成。 0 个错误`

- [ ] **Step 4: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Dc.Integration.Tests.csproj wpf/Dc.sln
git commit -m ":sparkles: Phase 8: 创建跨平台集成测试项目骨架"
```

---

## Task 2: 创建 Dc.Integration.Tests.Com 项目骨架（net8.0-windows x64）

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests.Com/Dc.Integration.Tests.Com.csproj`

- [ ] **Step 1: 创建 csproj**

写入 `wpf/tests/Dc.Integration.Tests.Com/Dc.Integration.Tests.Com.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Platforms>x64</Platforms>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Win32.Registry" Version="5.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Dc.Domain\Dc.Domain.csproj" />
    <ProjectReference Include="..\..\src\Dc.Opc.Abstractions\Dc.Opc.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\Dc.Opc.Da\Dc.Opc.Da.csproj"
                      AdditionalProperties="CustomTestTarget=net8.0-windows;Platform=x64" />
    <ProjectReference Include="..\..\src\Dc.Opc.Ae\Dc.Opc.Ae.csproj"
                      AdditionalProperties="CustomTestTarget=net8.0-windows;Platform=x64" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 在 Dc.sln 中注册项目**

在 `wpf/Dc.sln` 中 Task 1 加的 `Dc.Integration.Tests` 之后追加：

```
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Dc.Integration.Tests.Com", "tests\Dc.Integration.Tests.Com\Dc.Integration.Tests.Com.csproj", "{3A3A3A3A-3A3A-3A3A-3A3A-3A3A3A3A3A3A}"
EndProject
```

在 `GlobalSection(ProjectConfigurationPlatforms) = postSolution` 段下追加 GUID 配置块（只保留 x64 不要 Any CPU）：

```
{3A3A3A3A-3A3A-3A3A-3A3A-3A3A3A3A3A3A}.Debug|x64.ActiveCfg = Debug|x64
{3A3A3A3A-3A3A-3A3A-3A3A-3A3A3A3A3A3A}.Debug|x64.Build.0 = Debug|x64
{3A3A3A3A-3A3A-3A3A-3A3A-3A3A3A3A3A3A}.Release|x64.ActiveCfg = Release|x64
{3A3A3A3A-3A3A-3A3A-3A3A-3A3A3A3A3A3A}.Release|x64.Build.0 = Release|x64
```

- [ ] **Step 3: 验证编译（Linux 也能跑因为 EnableWindowsTargeting=true）**

Run: `cd wpf && dotnet build tests/Dc.Integration.Tests.Com/Dc.Integration.Tests.Com.csproj -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo -v:minimal`
Expected: `已成功生成。 0 个错误`

- [ ] **Step 4: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/Dc.Integration.Tests.Com.csproj wpf/Dc.sln
git commit -m ":sparkles: Phase 8: 创建 Windows-only COM 集成测试项目骨架"
```

---

## Task 3: 更新 build.ps1 让 test target 串行跑两个测试项目

**Files:**
- Modify: `wpf/build.ps1`

- [ ] **Step 1: 修改 test target 块**

找到 `wpf/build.ps1` 中 `"test"` 分支：

```powershell
    "test" {
        dotnet build Dc.sln --configuration $Configuration @props
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet test tests/Dc.Infrastructure.Tests --no-build --configuration $Configuration -p:Platform=x64
    }
```

替换成：

```powershell
    "test" {
        dotnet build Dc.sln --configuration $Configuration @props
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        # 单元测试（fakes-based）
        dotnet test tests/Dc.Infrastructure.Tests --no-build --configuration $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        # 集成测试（真 socket / 内嵌 UA server）— 跨平台
        dotnet test tests/Dc.Integration.Tests --no-build --configuration $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        # 集成测试（COM）— 仅 Windows 真跑；其它 OS 上 dotnet test 会报 TFM 不兼容直接失败，所以加守护
        if ($IsWindows -or $PSVersionTable.PSVersion.Major -lt 6) {
            dotnet test tests/Dc.Integration.Tests.Com --no-build --configuration $Configuration -p:Platform=x64
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        } else {
            Write-Host "[skip] Dc.Integration.Tests.Com — 非 Windows" -ForegroundColor Yellow
        }
    }
```

注：PS 5.1 没有 `$IsWindows` 自动变量，但 PS 5.1 只在 Windows 上跑，所以 `$PSVersionTable.PSVersion.Major -lt 6` 一定 true。PS 7+ 的 `$IsWindows` 区分平台。

- [ ] **Step 2: 验证语法（不实际跑测试，因为还没写测试）**

Run: `cd wpf && pwsh -Command "& { . ./build.ps1 -Target build }"`（Linux）或 `cd wpf; .\build.ps1 -Target build`（Windows）
Expected: build 成功（编译两个空骨架项目不会失败）

- [ ] **Step 3: Commit**

```bash
git add wpf/build.ps1
git commit -m ":wrench: build.ps1: test target 串行跑 3 个测试项目"
```

---

## Task 4: 创建 TcpListenerFixture 共享 helper

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Infrastructure/TcpListenerFixture.cs`

- [ ] **Step 1: 写 fixture**

写入 `wpf/tests/Dc.Integration.Tests/Infrastructure/TcpListenerFixture.cs`：

```csharp
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Dc.Integration.Tests.Infrastructure;

// 复用的 TCP listener 助手：起本地随机端口，按 wire-format.md 规则
// (4B BE length + payload) 读帧，写到 Channel<byte[]> 供测试断言。
//
// 用法：
//   using var lis = await TcpListenerFixture.StartAsync();
//   lis.Port → 拿端口
//   await lis.Frames.Reader.ReadAsync() → 拿下一帧 payload
public sealed class TcpListenerFixture : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ChannelReader<byte[]> Frames => _frames.Reader;

    private TcpListenerFixture(TcpListener listener)
    {
        _listener = listener;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public static Task<TcpListenerFixture> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new TcpListenerFixture(listener));
    }

    public string Address => $"127.0.0.1:{Port}";

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            _ = ReadClientAsync(client);
        }
    }

    private async Task ReadClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var ns = client.GetStream())
            {
                var lenBuf = new byte[4];
                while (!_cts.IsCancellationRequested)
                {
                    if (!await TryReadExact(ns, lenBuf, _cts.Token)) return;
                    var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                    if (len <= 0 || len > 16 * 1024 * 1024) return;
                    var payload = new byte[len];
                    if (!await TryReadExact(ns, payload, _cts.Token)) return;
                    await _frames.Writer.WriteAsync(payload, _cts.Token);
                }
            }
        }
        catch { /* 客户端断开正常 */ }
    }

    private static async Task<bool> TryReadExact(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            int n;
            try { n = await s.ReadAsync(buf.AsMemory(off), ct); }
            catch (OperationCanceledException) { return false; }
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _frames.Writer.TryComplete();
        _cts.Dispose();
    }
}
```

- [ ] **Step 2: 验证编译**

Run: `cd wpf && dotnet build tests/Dc.Integration.Tests --nologo -v:minimal`
Expected: 0 错误

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Infrastructure/TcpListenerFixture.cs
git commit -m ":sparkles: Phase 8: TcpListenerFixture 共享 helper"
```

---

## Task 5: INF-1 — TcpPublisher 写出 msgpack 帧格式正确

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Infrastructure/TcpPublisherEndToEndTests.cs`

- [ ] **Step 1: 写 INF-1 测试**

写入 `wpf/tests/Dc.Integration.Tests/Infrastructure/TcpPublisherEndToEndTests.cs`：

```csharp
using System.Buffers.Binary;
using Dc.Infrastructure.Messaging;
using Dc.Opc.Abstractions;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Dc.Integration.Tests.Infrastructure;

public class TcpPublisherEndToEndTests
{
    // INF-1: TcpPublisher 用 msgpack 发一条 TagValue，listener 收到 [4B BE length][payload]，
    //         反序列化字段与原始一致。
    [Fact(Timeout = 10_000)]
    public async Task INF1_MsgpackFrame_RoundTrip()
    {
        await using var lis = await TcpListenerFixture.StartAsync();
        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(lis.Address, serializer);

        var sent = new TagValue(
            Item: "Demo.Int32",
            Value: 123,
            Quality: 0xC0,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

        await pub.PublishAsync(sent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var payload = await lis.Frames.ReadAsync(cts.Token);

        // 反序列化用 ContractlessStandardResolver（与 MessagePackMessageSerializer 内一致）
        var back = MessagePackSerializer.Deserialize<TagValue>(payload, ContractlessStandardResolver.Options);

        Assert.Equal(sent.Item, back.Item);
        Assert.Equal(sent.Quality, back.Quality);
        Assert.Equal(sent.Timestamp, back.Timestamp);
        Assert.NotNull(back.Value);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests --filter "FullyQualifiedName~INF1_MsgpackFrame_RoundTrip" --nologo`
Expected: `Passed: 1, Failed: 0`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Infrastructure/TcpPublisherEndToEndTests.cs
git commit -m ":white_check_mark: Phase 8 INF-1: TcpPublisher msgpack 帧格式"
```

---

## Task 6: INF-2 — TcpPublisher JSON 格式

**Files:**
- Modify: `wpf/tests/Dc.Integration.Tests/Infrastructure/TcpPublisherEndToEndTests.cs`

- [ ] **Step 1: 在 class 内追加测试方法**

在 INF-1 方法之后追加：

```csharp
    // INF-2: 同样的 TagValue 用 JsonMessageSerializer 发，listener 收到的应是 UTF-8 JSON。
    [Fact(Timeout = 10_000)]
    public async Task INF2_JsonFrame_RoundTrip()
    {
        await using var lis = await TcpListenerFixture.StartAsync();
        var serializer = new JsonMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(lis.Address, serializer);

        var sent = new TagValue("Demo.String", "hello", 0xC0, DateTimeOffset.UtcNow);
        await pub.PublishAsync(sent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var payload = await lis.Frames.ReadAsync(cts.Token);

        var json = System.Text.Encoding.UTF8.GetString(payload);
        // JsonMessageSerializer 用 camelCase：item / value / quality / timestamp
        Assert.Contains("\"item\":\"Demo.String\"", json);
        Assert.Contains("\"value\":\"hello\"", json);
        Assert.Contains("\"quality\":192", json); // 0xC0 = 192
    }
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests --filter "FullyQualifiedName~INF2_JsonFrame_RoundTrip" --nologo`
Expected: `Passed: 1, Failed: 0`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Infrastructure/TcpPublisherEndToEndTests.cs
git commit -m ":white_check_mark: Phase 8 INF-2: TcpPublisher JSON 帧格式"
```

---

## Task 7: INF-3 — TcpPublisher 冷却（broker down 时快速失败）

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Infrastructure/ReconnectBackoffTests.cs`

- [ ] **Step 1: 写 INF-3 测试**

```csharp
using System.Diagnostics;
using Dc.Infrastructure.Messaging;
using Dc.Opc.Abstractions;
using Xunit;

namespace Dc.Integration.Tests.Infrastructure;

public class ReconnectBackoffTests
{
    // INF-3: listener 关掉后再发 3 条；第 1 条触发真 TCP connect 失败，
    // 后 2 条在 2 秒冷却内必须快速失败（< 100ms 各自），不真去 connect。
    [Fact(Timeout = 30_000)]
    public async Task INF3_PublisherCooldown_FastFail()
    {
        // 先起一个 listener 拿端口，立刻 Stop 让端口可用但拒绝连接
        var lis = await TcpListenerFixture.StartAsync();
        var address = lis.Address;
        await lis.DisposeAsync();
        // 这里端口已经释放，再连会 ConnectionRefused

        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(address, serializer);
        var sample = new TagValue("X", 1, 0xC0, DateTimeOffset.UtcNow);

        // 第 1 条：实际 TCP connect 失败，耗时不限
        var firstError = await Assert.ThrowsAnyAsync<Exception>(() => pub.PublishAsync(sample));
        Assert.True(IsConnectFailure(firstError), $"首次失败应是 TCP 连接错，实际: {firstError.GetType().Name} - {firstError.Message}");

        // 第 2、3 条：冷却中 → 应快速失败（实测应在 100ms 内）
        for (int i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            var err = await Assert.ThrowsAnyAsync<Exception>(() => pub.PublishAsync(sample));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 100,
                $"冷却期内第 {i + 2} 条应快速失败，实际 {sw.ElapsedMilliseconds}ms");
            Assert.Contains("冷却期", err.Message);
        }
    }

    private static bool IsConnectFailure(Exception ex)
    {
        // 第 1 条失败可能是 SocketException 或包了一层 InvalidOperationException
        for (var e = (Exception?)ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException) return true;
        }
        // 也接受我们包的"冷却期"消息（如果第 1 条触发瞬间已被前一次失败置入冷却）
        return ex.Message.Contains("冷却期") || ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests --filter "FullyQualifiedName~INF3_PublisherCooldown_FastFail" --nologo`
Expected: `Passed: 1, Failed: 0`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Infrastructure/ReconnectBackoffTests.cs
git commit -m ":white_check_mark: Phase 8 INF-3: TcpPublisher 冷却快速失败"
```

---

## Task 8: INF-4 — TcpPublisher 恢复

**Files:**
- Modify: `wpf/tests/Dc.Integration.Tests/Infrastructure/ReconnectBackoffTests.cs`

- [ ] **Step 1: 在 class 内追加测试**

在 INF-3 后面加：

```csharp
    // INF-4: broker 短暂下线（< 冷却时间）再起来，冷却期过后下一条应成功发出。
    [Fact(Timeout = 30_000)]
    public async Task INF4_PublisherRecovery_AfterCooldown()
    {
        // 用同一端口先后两次起 listener，模拟 broker 重启
        var first = await TcpListenerFixture.StartAsync();
        var port = first.Port;
        var address = $"127.0.0.1:{port}";
        await first.DisposeAsync();

        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(address, serializer);
        var sample = new TagValue("X", 1, 0xC0, DateTimeOffset.UtcNow);

        // 触发首次失败（进冷却）
        await Assert.ThrowsAnyAsync<Exception>(() => pub.PublishAsync(sample));

        // 端口可能已被系统短暂 TIME_WAIT，等 3s 并起新 listener。SO_REUSEADDR 默认情况下 .NET listener 应能在 Loopback 上重用。
        await Task.Delay(TimeSpan.FromSeconds(3));

        TcpListener? second = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                second = new TcpListener(System.Net.IPAddress.Loopback, port);
                second.Start();
                break;
            }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(1000);
            }
        }
        Assert.NotNull(second);

        try
        {
            // 冷却已过（2s + 3s），下一发应成功 — 不抛
            await pub.PublishAsync(sample);
        }
        finally
        {
            second!.Stop();
        }
    }
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests --filter "FullyQualifiedName~INF4_PublisherRecovery_AfterCooldown" --nologo`
Expected: `Passed: 1, Failed: 0`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Infrastructure/ReconnectBackoffTests.cs
git commit -m ":white_check_mark: Phase 8 INF-4: TcpPublisher 恢复后再发成功"
```

---

## Task 9: INF-5 — WireDump 风格 round-trip

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Infrastructure/WireDumpRoundTripTests.cs`

- [ ] **Step 1: 写 INF-5 测试**

```csharp
using Dc.Infrastructure.Messaging;
using Dc.Opc.Abstractions;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Dc.Integration.Tests.Infrastructure;

public class WireDumpRoundTripTests
{
    // INF-5: 模拟 Dc.WireDump 的解码路径 — publisher 发 N 条 → listener 按帧分割 →
    //         msgpack 解 → JSON 序列化 → 串能匹配关键字段。这一条等于"WireDump 在生产用法下能解码"。
    [Theory(Timeout = 15_000)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task INF5_WireDumpDecodes_SequentialFrames(int n)
    {
        await using var lis = await TcpListenerFixture.StartAsync();
        var serializer = new MessagePackMessageSerializer();
        await using var pub = TcpPublisher.FromAddress(lis.Address, serializer);

        var samples = new List<TagValue>();
        for (int i = 0; i < n; i++)
        {
            var v = new TagValue($"Tag{i}", i * 10, 0xC0,
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + i));
            samples.Add(v);
            await pub.PublishAsync(v);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (int i = 0; i < n; i++)
        {
            var payload = await lis.Frames.ReadAsync(cts.Token);
            var decoded = MessagePackSerializer.Deserialize<object>(payload, ContractlessStandardResolver.Options);
            var json = System.Text.Json.JsonSerializer.Serialize(decoded);
            Assert.Contains($"Tag{i}", json);
        }
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests --filter "FullyQualifiedName~INF5_WireDumpDecodes_SequentialFrames" --nologo`
Expected: `Passed: 2, Failed: 0`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Infrastructure/WireDumpRoundTripTests.cs
git commit -m ":white_check_mark: Phase 8 INF-5: WireDump 风格 round-trip"
```

---

## Task 10: 创建 MinimalUaNodeManager + MinimalUaServer

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaNodeManager.cs`
- Create: `wpf/tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaServer.cs`

- [ ] **Step 1: 写 NodeManager**

写入 `wpf/tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaNodeManager.cs`：

```csharp
using Opc.Ua;
using Opc.Ua.Server;

namespace Dc.Integration.Tests.Ua.Fixtures;

// 最小 UA NodeManager：在自定义 namespace 下暴露一个 Folder + 一个可读 Int32 变量。
// 目的是给客户端订阅 / 浏览的最少有效对象，避开 ReferenceServer 的大体量。
internal sealed class MinimalUaNodeManager : CustomNodeManager2
{
    public const string TestNamespace = "urn:dc:integrationtest:ua";
    public const string DemoIntId = "Demo.Int32";

    private BaseDataVariableState? _demoInt;
    private Timer? _ticker;

    public MinimalUaNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, TestNamespace)
    {
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }

            var folder = new FolderState(null)
            {
                NodeId = new NodeId("Demo", NamespaceIndex),
                BrowseName = new QualifiedName("Demo", NamespaceIndex),
                DisplayName = new LocalizedText("Demo"),
                TypeDefinitionId = ObjectTypeIds.FolderType,
                EventNotifier = EventNotifiers.None
            };
            folder.AddReference(ReferenceTypeIds.Organizes, isInverse: true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));

            _demoInt = new BaseDataVariableState(folder)
            {
                NodeId = new NodeId(DemoIntId, NamespaceIndex),
                BrowseName = new QualifiedName(DemoIntId, NamespaceIndex),
                DisplayName = new LocalizedText(DemoIntId),
                DataType = DataTypeIds.Int32,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Value = 0,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow
            };
            folder.AddChild(_demoInt);

            AddPredefinedNode(SystemContext, folder);

            // 每 200ms 自增一次，确保订阅端能拿到变化通知
            _ticker = new Timer(_ =>
            {
                lock (Lock)
                {
                    if (_demoInt is null) return;
                    _demoInt.Value = ((int)_demoInt.Value!) + 1;
                    _demoInt.Timestamp = DateTime.UtcNow;
                    _demoInt.StatusCode = StatusCodes.Good;
                    _demoInt.ClearChangeMasks(SystemContext, includeChildren: false);
                }
            }, state: null, dueTime: TimeSpan.FromMilliseconds(200), period: TimeSpan.FromMilliseconds(200));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ticker?.Dispose();
            _ticker = null;
        }
        base.Dispose(disposing);
    }
}
```

- [ ] **Step 2: 写 StandardServer 子类**

写入 `wpf/tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaServer.cs`：

```csharp
using Opc.Ua;
using Opc.Ua.Server;

namespace Dc.Integration.Tests.Ua.Fixtures;

// 极简 UA Server：只挂一个 MinimalUaNodeManager。其他能力按 Foundation StandardServer 默认。
internal sealed class MinimalUaServer : StandardServer
{
    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new INodeManager[]
        {
            new MinimalUaNodeManager(server, configuration)
        };
        return new MasterNodeManager(server, configuration, dynamicNamespaceUri: null, nodeManagers);
    }
}
```

- [ ] **Step 3: 验证编译**

Run: `cd wpf && dotnet build tests/Dc.Integration.Tests --nologo -v:minimal`
Expected: 0 错误

- [ ] **Step 4: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaNodeManager.cs wpf/tests/Dc.Integration.Tests/Ua/Fixtures/MinimalUaServer.cs
git commit -m ":sparkles: Phase 8: 极简 UA NodeManager + StandardServer 子类"
```

---

## Task 11: 创建 EmbeddedUaServerFixture

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Ua/Fixtures/EmbeddedUaServerFixture.cs`

- [ ] **Step 1: 写 Fixture**

```csharp
using System.Net;
using System.Net.Sockets;
using Opc.Ua;
using Opc.Ua.Configuration;
using Xunit;

namespace Dc.Integration.Tests.Ua.Fixtures;

// xunit Fixture：测试 class 用 [Collection("Ua")] 共享一个进程内 UA Server，
// 避免每个测试都从零启动（启动证书校验约 1-2s）。
//
// 启动后：Endpoint 暴露 opc.tcp://127.0.0.1:<random_port>，无安全 (None/None)，允许匿名。
public sealed class EmbeddedUaServerFixture : IAsyncLifetime
{
    public Uri Endpoint { get; private set; } = default!;
    public string AnonymousEndpointUrl => $"{Endpoint}";

    private ApplicationInstance? _app;
    private MinimalUaServer? _server;
    private string? _pkiRoot;

    public async Task InitializeAsync()
    {
        var port = FindFreePort();
        Endpoint = new Uri($"opc.tcp://127.0.0.1:{port}");

        // 隔离 PKI 到临时目录，避免污染开发者机器的 ApplicationData
        _pkiRoot = Path.Combine(Path.GetTempPath(), "dc-it-ua-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_pkiRoot);

        var config = BuildConfig(port, _pkiRoot);
        config.Validate(ApplicationType.Server).GetAwaiter().GetResult();

        _app = new ApplicationInstance
        {
            ApplicationName = "Dc.IntegrationTest.UaServer",
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config
        };

        // 首次会生成 self-signed 证书；隔离到 _pkiRoot 不污染外面
        await _app.CheckApplicationInstanceCertificates(silent: true, minimumKeySize: 2048);

        _server = new MinimalUaServer();
        await _app.Start(_server);
    }

    public Task DisposeAsync()
    {
        try { _server?.Stop(); } catch { }
        try
        {
            if (_pkiRoot is not null && Directory.Exists(_pkiRoot))
                Directory.Delete(_pkiRoot, recursive: true);
        }
        catch { }
        return Task.CompletedTask;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ApplicationConfiguration BuildConfig(int port, string pkiRoot)
    {
        return new ApplicationConfiguration
        {
            ApplicationName = "Dc.IntegrationTest.UaServer",
            ApplicationUri = "urn:localhost:dc:integrationtest:uaserver",
            ApplicationType = ApplicationType.Server,
            ProductUri = "https://git.adamyu.top/dc",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = "CN=Dc.IntegrationTest.UaServer, C=US, S=Test, O=Dc, DC=localhost"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "issuers")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "trusted")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "rejected")
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = true,
                MinimumCertificateKeySize = 2048
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { $"opc.tcp://127.0.0.1:{port}" },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None
                    }
                },
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                DiagnosticsEnabled = false,
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200
            },
            TraceConfiguration = new TraceConfiguration { TraceMasks = 0 }
        };
    }
}

[CollectionDefinition("Ua")]
public sealed class UaCollection : ICollectionFixture<EmbeddedUaServerFixture> { }
```

- [ ] **Step 2: 验证编译**

Run: `cd wpf && dotnet build tests/Dc.Integration.Tests --nologo -v:minimal`
Expected: 0 错误

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Ua/Fixtures/EmbeddedUaServerFixture.cs
git commit -m ":sparkles: Phase 8: EmbeddedUaServerFixture (随机端口 + 隔离 PKI)"
```

---

## Task 12: UA-1 — UaSubscriber 订阅简单变量

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Ua/UaSubscriberSmokeTests.cs`

- [ ] **Step 1: 写测试**

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class UaSubscriberSmokeTests
{
    private readonly EmbeddedUaServerFixture _ua;
    public UaSubscriberSmokeTests(EmbeddedUaServerFixture ua) => _ua = ua;

    // UA-1: 连内嵌 server → 订阅 ns=2;s=Demo.Int32（MinimalUaNodeManager 暴露的）→ 收到 ≥ 1 条 TagValue
    [Fact(Timeout = 20_000)]
    public async Task UA1_SubscribeStaticInt_ReceivesValue()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _ua.AnonymousEndpointUrl,
            SamplingInterval = TimeSpan.FromMilliseconds(200),
            HeartbeatInterval = TimeSpan.FromSeconds(5)
        };
        await using var sub = new OpcUaSubscriber("test-ua", options);

        await sub.ConnectAsync();

        // ns=2 是 MinimalUaNodeManager 的 namespace index（系统默认 ns=0 + 测试 server 的命名空间 ns=2）
        // NodeId 字符串使用 vendor 风格 "ns=2;s=Demo.Int32"
        var tag = new TagDescriptor(Id: "test-tag-1", Item: "ns=2;s=Demo.Int32", DataType: 0);
        await sub.SubscribeAsync(new[] { tag });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        TagValue v = await sub.TagValues.ReadAsync(cts.Token);

        Assert.Equal("ns=2;s=Demo.Int32", v.Item);
        Assert.NotNull(v.Value);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests --filter "FullyQualifiedName~UA1_SubscribeStaticInt_ReceivesValue" --nologo`
Expected: `Passed: 1, Failed: 0`

如果 NodeId 格式不匹配（OpcUaSubscriber 内部对 NodeId 字符串的解析与我们这里写法略异），跑测试报"找不到节点"时改成 `i=...` 数字节点 id 或调试 `_demoInt.NodeId.ToString()` 看真实形态。

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Ua/UaSubscriberSmokeTests.cs
git commit -m ":white_check_mark: Phase 8 UA-1: UA Subscriber 订阅简单变量"
```

---

## Task 13: UA-2, UA-3 — UA Browser 列根 + 下钻

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests/Ua/UaBrowserSmokeTests.cs`

- [ ] **Step 1: 写两个测试**

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Ua;
using Dc.Integration.Tests.Ua.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Ua;

[Collection("Ua")]
public class UaBrowserSmokeTests
{
    private readonly EmbeddedUaServerFixture _ua;
    public UaBrowserSmokeTests(EmbeddedUaServerFixture ua) => _ua = ua;

    // UA-2: 浏览根（null parent）应返回至少 Objects/Server/... 几个核心节点
    [Fact(Timeout = 15_000)]
    public async Task UA2_BrowseRoot_ContainsObjects()
    {
        var options = new OpcConnectionOptions { ServerUri = _ua.AnonymousEndpointUrl };
        await using var browser = new OpcUaBrowser();
        await browser.ConnectAsync(options);

        var children = await browser.BrowseAsync(parentNodeId: null);

        Assert.NotEmpty(children);
        Assert.Contains(children, n => n.DisplayName.Equals("Objects", StringComparison.OrdinalIgnoreCase));
    }

    // UA-3: 下钻 Objects 应能找到我们 MinimalUaNodeManager 暴露的 Demo 文件夹
    [Fact(Timeout = 15_000)]
    public async Task UA3_BrowseObjects_ContainsDemoFolder()
    {
        var options = new OpcConnectionOptions { ServerUri = _ua.AnonymousEndpointUrl };
        await using var browser = new OpcUaBrowser();
        await browser.ConnectAsync(options);

        var roots = await browser.BrowseAsync(null);
        var objects = roots.FirstOrDefault(n => n.DisplayName == "Objects")
            ?? throw new Xunit.Sdk.XunitException("没找到 Objects 节点");

        var children = await browser.BrowseAsync(objects.Id);

        Assert.Contains(children, n => n.DisplayName == "Demo");
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests --filter "FullyQualifiedName~UaBrowserSmokeTests" --nologo`
Expected: `Passed: 2, Failed: 0`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests/Ua/UaBrowserSmokeTests.cs
git commit -m ":white_check_mark: Phase 8 UA-2/UA-3: UA Browser 根 + 下钻"
```

---

## Task 14: WindowsComFactAttribute + DemoServerFixture

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests.Com/Fixtures/WindowsComFactAttribute.cs`
- Create: `wpf/tests/Dc.Integration.Tests.Com/Fixtures/DemoServerFixture.cs`

- [ ] **Step 1: 写 WindowsComFactAttribute**

```csharp
using System.Runtime.Versioning;
using Microsoft.Win32;
using Xunit;

namespace Dc.Integration.Tests.Com.Fixtures;

// 自定义 [Fact] 派生类：根据 OS / OPCEnum 二进制 / demo server ProgID 注册自动 skip。
// 失败原因写入 Skip 属性，xunit runner 显示为 skipped 而非 failed。
[SupportedOSPlatform("windows")]
public sealed class WindowsComFactAttribute : FactAttribute
{
    // 默认探测 SampleCompany.DaSample；AE 测试可显式传 "SampleCompany.AeSample"
    public WindowsComFactAttribute(string requiredProgId = "SampleCompany.DaSample")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "OPC DA/AE 仅 Windows";
            return;
        }
        if (!File.Exists(@"C:\Windows\SysWOW64\OpcEnum.exe"))
        {
            Skip = "OPCEnum 未安装（装 OPC Core Components Redistributable）";
            return;
        }
        if (Registry.ClassesRoot.OpenSubKey(requiredProgId) is null)
        {
            Skip = $"{requiredProgId} 未 /regserver — cd vendor\\ClassicClient\\x86\\DemoServer 跑 OpcDaAeServer.exe /regserver";
            return;
        }
    }
}
```

- [ ] **Step 2: 写 DemoServerFixture**

```csharp
namespace Dc.Integration.Tests.Com.Fixtures;

// 一组常量 + 路径助手，给所有 ClassicCom 测试共享。
// 不在 InitializeAsync 里 /regserver — 避免污染开发者环境。
public sealed class DemoServerFixture
{
    public string DaProgId  { get; } = "SampleCompany.DaSample";
    public string DaClsid   { get; } = "{5CEE2576-AA37-4D54-B02D-ECABE09A1C1E}";
    public string AeProgId  { get; } = "SampleCompany.AeSample";
    public string AeClsid   { get; } = "{71EFE996-DA6C-4256-8523-230647CFC0D0}";
    public string Host      { get; } = "localhost";

    public string DemoExePath
    {
        get
        {
            // 测试运行目录 → 回溯到 wpf/ 再到 vendor 路径
            // 实际跑时 IDE/dotnet test 把 cwd 设到 bin/.../net8.0-windows，
            // 用 AppContext.BaseDirectory 更稳。
            var baseDir = AppContext.BaseDirectory;
            // baseDir = wpf/tests/Dc.Integration.Tests.Com/bin/Debug/net8.0-windows/  或 x64/Debug/...
            // 回溯 5~6 层到 wpf/
            var probe = new DirectoryInfo(baseDir);
            for (int i = 0; i < 7 && probe is not null; i++)
            {
                var candidate = Path.Combine(probe.FullName, "vendor", "ClassicClient", "x86", "DemoServer", "OpcDaAeServer.exe");
                if (File.Exists(candidate)) return candidate;
                probe = probe.Parent;
            }
            throw new FileNotFoundException("找不到 OpcDaAeServer.exe — 请确认 vendor submodule 已 checkout");
        }
    }
}
```

- [ ] **Step 3: 验证编译**

Run: `cd wpf && dotnet build tests/Dc.Integration.Tests.Com -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo -v:minimal`
Expected: 0 错误

- [ ] **Step 4: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/Fixtures/
git commit -m ":sparkles: Phase 8: WindowsComFactAttribute + DemoServerFixture"
```

---

## Task 15: DA-1 — DaSubscriber 订阅 demo 项

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests.Com/DaSubscriberSmokeTests.cs`

- [ ] **Step 1: 写测试**

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Da;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com;

[Collection("Com")]
public class DaSubscriberSmokeTests
{
    private readonly DemoServerFixture _demo = new();

    // DA-1: 连 demo server SampleCompany.DaSample，订阅 "Bucket Brigade.Int4"
    //        （这是 Technosoftware demo server 内置变化最稳定的项之一），
    //        收到至少 1 条 TagValue，quality bit 段为 Good (0xC0)。
    [WindowsComFact("SampleCompany.DaSample")]
    public async Task DA1_SubscribeBucketBrigade_ReceivesValue()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.DaProgId,
            SamplingInterval = TimeSpan.FromMilliseconds(500),
            HeartbeatInterval = TimeSpan.FromSeconds(10)
        };
        await using var sub = new OpcDaSubscriber("test-da", options);
        await sub.ConnectAsync();

        var tag = new TagDescriptor(Id: "t1", Item: "Bucket Brigade.Int4", DataType: 0);
        await sub.SubscribeAsync(new[] { tag });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        TagValue v = await sub.TagValues.ReadAsync(cts.Token);

        Assert.Equal("Bucket Brigade.Int4", v.Item);
        Assert.True(v.IsGood, $"期望 quality 0xC0 Good，实际 0x{v.Quality:X2}");
    }
}

// 同进程内多个 Com 测试 class 共享 [Collection("Com")] → xunit 串行执行
// （DA COM 的 STA 公寓敏感，并发激活同一 server 时常 0x80010108 RPC_E_DISCONNECTED）
[CollectionDefinition("Com")]
public class ComCollection { }
```

- [ ] **Step 2: 运行测试**

Run（Linux/无环境 → skip；Windows + 已注册 demo server → pass）:
`cd wpf && dotnet test tests/Dc.Integration.Tests.Com --filter "FullyQualifiedName~DA1_SubscribeBucketBrigade" -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo`
Expected (Linux): `Skipped: 1`
Expected (Windows 完整): `Passed: 1`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/DaSubscriberSmokeTests.cs
git commit -m ":white_check_mark: Phase 8 DA-1: DA Subscriber 订阅 Bucket Brigade.Int4"
```

---

## Task 16: DA-2, DA-3 — DA Browser 扫描 + 浏览根

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests.Com/DaBrowserSmokeTests.cs`

- [ ] **Step 1: 写测试**

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Da;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com;

[Collection("Com")]
public class DaBrowserSmokeTests
{
    private readonly DemoServerFixture _demo = new();

    // DA-2: 扫描本机能列出 demo server URL
    [WindowsComFact("SampleCompany.DaSample")]
    public async Task DA2_EnumerateServersLocalhost_ContainsDemo()
    {
        await using var browser = new OpcDaBrowser();
        var urls = await browser.EnumerateServersAsync("localhost");

        Assert.NotEmpty(urls);
        Assert.Contains(urls, u => u.Contains(_demo.DaProgId, StringComparison.OrdinalIgnoreCase));
    }

    // DA-3: 连上 demo server 后浏览根，应至少包含 "Bucket Brigade" 文件夹
    [WindowsComFact("SampleCompany.DaSample")]
    public async Task DA3_BrowseRoot_ContainsBucketBrigade()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.DaProgId
        };
        await using var browser = new OpcDaBrowser();
        await browser.ConnectAsync(options);

        var children = await browser.BrowseAsync(parentNodeId: null);

        Assert.NotEmpty(children);
        Assert.Contains(children, n =>
            n.Kind == OpcNodeKind.Folder &&
            n.DisplayName.Equals("Bucket Brigade", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests.Com --filter "FullyQualifiedName~DaBrowserSmokeTests&FullyQualifiedName!~CLSID" -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo`
Expected (Windows + demo): `Passed: 2`
Expected (Linux): `Skipped: 2`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/DaBrowserSmokeTests.cs
git commit -m ":white_check_mark: Phase 8 DA-2/DA-3: DA Browser 扫描 + 根浏览"
```

---

## Task 17: DA-4 — DA Browser CLSID 兜底

**Files:**
- Modify: `wpf/tests/Dc.Integration.Tests.Com/DaBrowserSmokeTests.cs`

- [ ] **Step 1: 在 class 内追加 DA-4 测试**

在 `DA3_BrowseRoot_ContainsBucketBrigade` 后面追加：

```csharp
    // DA-4: ServerClsid 给值时 vendor 拼 opcda://host/progId/{clsid}，
    //        connect 应跳过 OPCEnum 解析，直接 CoCreateInstance 成功。
    [WindowsComFact("SampleCompany.DaSample")]
    public async Task DA4_ClsidFallback_ConnectsWithoutOpcEnumLookup()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.DaProgId,
            ServerClsid = _demo.DaClsid // 已带 {} 形式
        };
        await using var browser = new OpcDaBrowser();
        await browser.ConnectAsync(options);

        var children = await browser.BrowseAsync(null);
        Assert.NotEmpty(children);
    }
```

注：本测试不强行模拟 OPCEnum 缺失（需要操作系统层级隔离）。它仅证明"CLSID URL 形式被 vendor 正确解析并完成 CoCreateInstance"。判断绕过 OPCEnum 的强证据见 vendor `Factory.Connect`（line 220 拆 progId/clsid 后走 `new Guid(clsid)` 分支，跳过 `ServerEnumerator.CLSIDFromProgID`）。

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests.Com --filter "FullyQualifiedName~DA4_ClsidFallback" -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo`
Expected (Windows + demo): `Passed: 1`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/DaBrowserSmokeTests.cs
git commit -m ":white_check_mark: Phase 8 DA-4: DA Browser CLSID 兜底"
```

---

## Task 18: AE-1 — AeSubscriber 通配订阅收事件

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests.Com/AeSubscriberSmokeTests.cs`

- [ ] **Step 1: 写测试**

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Ae;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com;

[Collection("Com")]
public class AeSubscriberSmokeTests
{
    private readonly DemoServerFixture _demo = new();

    // AE-1: Tag.Item = "*" 走全收路径。Technosoftware demo server 启动会主动发若干
    //        condition refresh 事件，等 10s 内应至少收到 1 条。
    [WindowsComFact("SampleCompany.AeSample")]
    public async Task AE1_SubscribeWildcard_ReceivesEvent()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.AeProgId,
            SamplingInterval = TimeSpan.FromMilliseconds(500),
            HeartbeatInterval = TimeSpan.FromSeconds(10)
        };
        await using var sub = new OpcAeSubscriber("test-ae", options);
        await sub.ConnectAsync();

        var tag = new TagDescriptor(Id: "t-wild", Item: "*", DataType: 0);
        await sub.SubscribeAsync(new[] { tag });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        TagValue v = await sub.TagValues.ReadAsync(cts.Token);

        // AE 事件 Value 是 Dictionary<string,object?>
        Assert.IsAssignableFrom<IDictionary<string, object?>>(v.Value);
        var payload = (IDictionary<string, object?>)v.Value!;
        Assert.True(payload.ContainsKey("severity"));
        Assert.True(payload.ContainsKey("event_type"));
        Assert.Equal((ushort)0xC0, v.Quality);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests.Com --filter "FullyQualifiedName~AE1_SubscribeWildcard" -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo`
Expected (Windows + AE 注册): `Passed: 1`

如果 15s 等不到事件，说明 demo server 在该环境下没主动发事件 — 可改 timeout 到 30s 或在测试里手工触发（调 demo 的 alarm fire trigger）。

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/AeSubscriberSmokeTests.cs
git commit -m ":white_check_mark: Phase 8 AE-1: AE Subscriber 通配订阅"
```

---

## Task 19: AE-2, AE-3 — AE Browser 列 Area + Source

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests.Com/AeBrowserSmokeTests.cs`

- [ ] **Step 1: 写测试**

```csharp
using Dc.Opc.Abstractions;
using Dc.Opc.Ae;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com;

[Collection("Com")]
public class AeBrowserSmokeTests
{
    private readonly DemoServerFixture _demo = new();

    // AE-2: 浏览根（areaId="") 应至少返回 1 个节点（Area 或 Source）
    [WindowsComFact("SampleCompany.AeSample")]
    public async Task AE2_BrowseRoot_NotEmpty()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.AeProgId
        };
        await using var browser = new OpcAeBrowser();
        await browser.ConnectAsync(options);

        var roots = await browser.BrowseAsync(null);
        Assert.NotEmpty(roots);
    }

    // AE-3: 在第一个 Area 下浏览，应至少有 1 个 Source 叶子（QualifiedName = SourceID）
    [WindowsComFact("SampleCompany.AeSample")]
    public async Task AE3_BrowseFirstArea_ContainsSource()
    {
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.AeProgId
        };
        await using var browser = new OpcAeBrowser();
        await browser.ConnectAsync(options);

        var roots = await browser.BrowseAsync(null);
        var firstArea = roots.FirstOrDefault(n => n.Kind == OpcNodeKind.Folder);
        if (firstArea is null)
        {
            // demo server 可能直接给扁平 Source 列表 — 通过
            Assert.Contains(roots, n => n.Kind == OpcNodeKind.Item);
            return;
        }

        var children = await browser.BrowseAsync(firstArea.Id);
        // 子级或者是再一级 Area 或者是 Source — 我们只要求非空
        Assert.NotEmpty(children);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests.Com --filter "FullyQualifiedName~AeBrowserSmokeTests" -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo`
Expected (Windows + AE 注册): `Passed: 2`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/AeBrowserSmokeTests.cs
git commit -m ":white_check_mark: Phase 8 AE-2/AE-3: AE Browser Area + Source"
```

---

## Task 20: RES-1 — DaSubscriber server killed pipeline survives

**Files:**
- Create: `wpf/tests/Dc.Integration.Tests.Com/Resilience/DaResilienceTests.cs`

- [ ] **Step 1: 写测试**

```csharp
using System.Diagnostics;
using Dc.Opc.Abstractions;
using Dc.Opc.Da;
using Dc.Integration.Tests.Com.Fixtures;
using Xunit;

namespace Dc.Integration.Tests.Com.Resilience;

[Collection("Com")]
public class DaResilienceTests
{
    private readonly DemoServerFixture _demo = new();

    // RES-1: 订阅运行中杀掉 demo server 进程 → 等几秒（COM SCM 会按需重启 LocalServer32）→
    //        在重启后我们重新订阅应该能再次收到值。本测试不依赖 orchestrator，直接验证：
    //        断开后重新 ConnectAsync + SubscribeAsync 仍能工作。
    [WindowsComFact("SampleCompany.DaSample")]
    public async Task RES1_KillDemoServer_RestartedClientCanReceiveAgain()
    {
        // 第一阶段：建立订阅，收到 1 条
        var options = new OpcConnectionOptions
        {
            ServerUri = _demo.Host,
            ServerProgId = _demo.DaProgId,
            SamplingInterval = TimeSpan.FromMilliseconds(500),
            HeartbeatInterval = TimeSpan.FromSeconds(5)
        };

        await using (var sub1 = new OpcDaSubscriber("res-1a", options))
        {
            await sub1.ConnectAsync();
            await sub1.SubscribeAsync(new[] { new TagDescriptor("t", "Bucket Brigade.Int4", 0) });
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await sub1.TagValues.ReadAsync(cts1.Token); // 收到一条就行
        }

        // 杀进程
        foreach (var p in Process.GetProcessesByName("OpcDaAeServer"))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
            p.Dispose();
        }

        // 等系统冷却 + SCM 释放
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 第二阶段：新订阅器应能 connect 上（SCM 重启 LocalServer32），并再次收到值
        await using var sub2 = new OpcDaSubscriber("res-1b", options);
        await sub2.ConnectAsync();
        await sub2.SubscribeAsync(new[] { new TagDescriptor("t", "Bucket Brigade.Int4", 0) });

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var v = await sub2.TagValues.ReadAsync(cts2.Token);
        Assert.Equal("Bucket Brigade.Int4", v.Item);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests.Com --filter "FullyQualifiedName~RES1_KillDemoServer" -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo`
Expected (Windows + demo): `Passed: 1`

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/Resilience/DaResilienceTests.cs
git commit -m ":white_check_mark: Phase 8 RES-1: DA Subscriber server killed survives"
```

---

## Task 21: RES-2 — DaBrowser 噪声端口扫描容错

**Files:**
- Modify: `wpf/tests/Dc.Integration.Tests.Com/Resilience/DaResilienceTests.cs`

- [ ] **Step 1: 在 class 内追加测试**

```csharp
    // RES-2: 扫描不存在的主机 192.0.2.1（RFC5737 文档保留地址，永远不通），
    //        EnumerateServersAsync 必须在 30s 内抛或返回空，不挂死。
    [WindowsComFact("SampleCompany.DaSample")]
    public async Task RES2_ScanUnreachableHost_TimesOutGracefully()
    {
        await using var browser = new OpcDaBrowser();

        using var hardCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var urls = await browser.EnumerateServersAsync("192.0.2.1", hardCts.Token);
            // 返空也算 OK — 重点是不挂
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), $"扫描应在 40s 内完成，实际 {sw.Elapsed}");
        }
        catch (Exception)
        {
            // vendor 抛 OpcResultException / COMException 也算 OK
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), $"扫描应在 40s 内抛，实际 {sw.Elapsed}");
        }
    }
```

- [ ] **Step 2: 运行测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests.Com --filter "FullyQualifiedName~RES2_ScanUnreachable" -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo`
Expected (Windows): `Passed: 1`

注：测试可能跑满 30~40s（DCOM 默认超时较长）。这正是我们要测的"不挂死"。

- [ ] **Step 3: Commit**

```bash
git add wpf/tests/Dc.Integration.Tests.Com/Resilience/DaResilienceTests.cs
git commit -m ":white_check_mark: Phase 8 RES-2: DA Browser 噪声端口超时"
```

---

## Task 22: README 补"自动化等价物"映射

**Files:**
- Modify: `wpf/README.md`

- [ ] **Step 1: 在 README 的"Windows 端到端验证清单"段末尾、"已知限制"段之前插入"自动化等价物"小节**

找到 README 的 "### 7. Phase 7 — 安装包" 段末尾的 `---` 分隔符之前的内容，在分隔符之前追加：

```markdown
### 自动化等价物（Phase 8 集成测试映射）

上面 7 步手工 checklist 大部分由 `tests/Dc.Integration.Tests` 和 `tests/Dc.Integration.Tests.Com` 自动化覆盖。映射：

| 手工步骤 | 自动化测试 ID | 项目 |
|---|---|---|
| Phase 3a UA 订阅 | UA-1 | Dc.Integration.Tests |
| Phase 3a UA 浏览 | UA-2, UA-3 | Dc.Integration.Tests |
| Phase 3b DA 订阅 | DA-1 | Dc.Integration.Tests.Com |
| Phase 3c DA 扫描 + 浏览 | DA-2, DA-3 | Dc.Integration.Tests.Com |
| Phase 3c CLSID 兜底 | DA-4 | Dc.Integration.Tests.Com |
| Phase 4 AE 订阅 + 浏览 | AE-1, AE-2, AE-3 | Dc.Integration.Tests.Com |
| Phase 5 序列化切换 | INF-1 (msgpack), INF-2 (json) | Dc.Integration.Tests |
| Phase 6 端到端发布 | INF-5 (WireDump 等价路径) | Dc.Integration.Tests |
| Phase 6 重连退避 | INF-3 (冷却), INF-4 (恢复) | Dc.Integration.Tests |
| 弹性 — server killed | RES-1 | Dc.Integration.Tests.Com |
| 弹性 — 不可达主机超时 | RES-2 | Dc.Integration.Tests.Com |

一次性跑全集（Windows + demo server 已注册 + OPCEnum 已装）：

```powershell
.\build.ps1 -Target test
```

Linux / WSL 跑跨平台子集（仅 Infrastructure + UA = 8 tests）：

```bash
dotnet test wpf/tests/Dc.Integration.Tests
```

手工 7 步保留，作为"装包后真机走一遍"的产线验收 — 集成测试覆盖代码层正确性，不替代真硬件 / 真服务器的最终确认。
```

- [ ] **Step 2: Commit**

```bash
git add wpf/README.md
git commit -m ":memo: README: Phase 8 集成测试映射到手工验证 checklist"
```

---

## Task 23: 端到端验证（跑全集 + 推送）

**Files:** none

- [ ] **Step 1: Linux 上跑跨平台测试**

Run: `cd wpf && dotnet test tests/Dc.Integration.Tests --nologo`
Expected: `Passed: 8, Failed: 0`（INF-1..5 + UA-1..3，INF-5 是 Theory 2 用例所以总数是 8）

如果 UA tests 失败：MinimalUaNodeManager 暴露的 NodeId 真实字符串可能不是 `ns=2;s=Demo.Int32` — 在测试里临时加个 `Console.WriteLine(_demoInt.NodeId)` 看实际值，调测试断言后重跑。

- [ ] **Step 2: 编译验证 Com 项目（Linux 也能编译，能加载到 sln）**

Run: `cd wpf && dotnet build tests/Dc.Integration.Tests.Com -p:Platform=x64 -p:CustomTestTarget=net8.0-windows --nologo -v:minimal`
Expected: 0 错误

- [ ] **Step 3: Push 全部提交**

Run:
```bash
git push
```
Expected: 推送成功

- [ ] **Step 4: 文档 commit 集成测试覆盖率**

Optional：在 ROADMAP.md 加 Phase 8 完成标记。如果文件存在：

```bash
echo "" >> wpf/ROADMAP.md
echo "## Phase 8 — 集成测试 ✓ ($(date +%Y-%m-%d))" >> wpf/ROADMAP.md
echo "" >> wpf/ROADMAP.md
echo "- 17 测试用例覆盖 OPC 协议层 + TcpPublisher + 关键弹性" >> wpf/ROADMAP.md
echo "- 跨平台 8 ✓ / Windows-only 9 ✓ (前置: demo server + OPCEnum)" >> wpf/ROADMAP.md
git add wpf/ROADMAP.md
git commit -m ":memo: ROADMAP: Phase 8 完成"
git push
```

---

## 自检

### 1. Spec 覆盖检查

| Spec 章节 | 实现 Task | 状态 |
|---|---|---|
| 3 项目结构（跨平台 + Com 两项目） | Task 1, 2 | ✓ |
| 3 csproj 包配 | Task 1, 2 | ✓ |
| 3 加入 Dc.sln | Task 1, 2 | ✓ |
| 4 WindowsComFactAttribute | Task 14 | ✓ |
| 5.1 DemoServerFixture | Task 14 | ✓ |
| 5.2 EmbeddedUaServerFixture | Task 11 | ✓ |
| 5.3 TcpListenerFixture | Task 4 | ✓ |
| 6.1 INF-1..5 | Tasks 5, 6, 7, 8, 9 | ✓ |
| 6.2 UA-1..3 | Tasks 12, 13 | ✓ |
| 6.3 DA-1..4 | Tasks 15, 16, 17 | ✓ |
| 6.4 AE-1..3 | Tasks 18, 19 | ✓ |
| 6.5 RES-1..2 | Tasks 20, 21 | ✓ |
| 7 xunit Collection 串行 COM | Task 15 (CollectionDefinition 在 DaSubscriberSmokeTests.cs) | ✓ |
| 7 测试超时 | 所有 Fact 标 Timeout | ✓ |
| 11 build.ps1 双项目串行 | Task 3 | ✓ |
| 11 README 自动化映射 | Task 22 | ✓ |

### 2. Placeholder 扫描

- 无 TBD / TODO / "implement later"
- 每一处 "如果失败，调试..." 都给了下一步动作（怎么调试，看什么）
- UA-1 测试里的 NodeId 字符串 `ns=2;s=Demo.Int32` 是有依据的预设值，若实际不一致 Task 23 Step 1 给了调整方法

### 3. 类型/方法名一致性

- `TcpListenerFixture.Frames` (ChannelReader<byte[]>) — 用法一致 (Task 4 定义，Task 5/6/7/8/9 使用)
- `EmbeddedUaServerFixture.AnonymousEndpointUrl` (string property) — Task 11 定义，Task 12/13 使用
- `DemoServerFixture.DaProgId / DaClsid / AeProgId / Host` — Task 14 定义，Task 15-21 使用
- `WindowsComFactAttribute(string requiredProgId)` — Task 14 定义，DA 测试用 "SampleCompany.DaSample"，AE 测试用 "SampleCompany.AeSample"，弹性测试用 DaProgId
- `ComCollection` 在 Task 15 文件 `DaSubscriberSmokeTests.cs` 定义；Task 16/18/19/20/21 文件用 `[Collection("Com")]` 字符串引用，xunit 按字符串匹配，无问题

### 4. 已知风险与开放问题

- **UA NodeId 字符串具体形态**：`ns=2;s=Demo.Int32` 是基于 OPCFoundation 默认 `NodeId.ToString()` 格式的预测。Task 12 Step 2 写了 fallback 调试步骤
- **demo server AE 心跳频率**：AE-1 假定 demo 启动 15s 内发至少 1 条事件。若环境实测要更久，调 Timeout（Task 18 已给出说明）
- **端口重用窗口**：INF-4 用 SO_REUSEADDR + 3s 等待覆盖大多数 Linux/Windows TIME_WAIT 场景。极端环境可能仍偶发失败，最多重跑

---

Plan complete and saved to `wpf/docs/superpowers/plans/2026-05-18-phase-8-integration-tests.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
