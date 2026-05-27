# Phase 8 — 集成测试设计

**Date**: 2026-05-18
**Owner**: adamyu
**Status**: approved, awaiting implementation plan

## 1. 目的

为 WPF 重写后的 OPC 数采系统补集成测试，覆盖：

- **OPC 协议层**：DA / AE / UA 三个订阅器 + 浏览器对真服务器的端到端流程
- **基础设施层**：TcpPublisher 对真 socket、WireDump 接收链路、重连退避
- **关键弹性场景**：broker 挂掉、demo server 杀掉、OPCEnum 不可用的 CLSID 兜底

现有 `tests/Dc.Infrastructure.Tests` 用 `Fakes/FakeOpcSubscriber` 等替身，覆盖了 orchestrator/serializer/persistence 的单元行为。本期补"打真东西"的层级。

## 2. 范围与边界

### 在范围内

- 新建独立测试项目 `tests/Dc.Integration.Tests`
- 协议无关的 TCP / WireDump 帧来回测试
- UA 子集：用 vendor 自带 ReferenceServer 起内嵌服务器
- DA / AE 子集：打 `vendor/ClassicClient/x86/DemoServer/OpcDaAeServer.exe`，需 Windows + COM + 已 `/regserver`
- 4 条核心弹性场景

### 不在范围内

- WPF UI 自动化（VS Test 的 UI Automation 复杂度暴涨，留 v3）
- 多机器 DCOM 跨主机（开发本地不易模拟）
- 性能 / 压测（吞吐基准单列项目）
- CI runner 编排（CI 任务整体延后到决定开源时）

## 3. 项目结构

**两个测试项目**（因为 `net8.0-windows` 程序集 .NET runtime 不能在 Linux 上加载，无法靠 `WindowsComFact.Skip` 兜过 — 必须 TFM 隔离）：

```
tests/
├── Dc.Integration.Tests/             # net8.0，跨平台
│   ├── Dc.Integration.Tests.csproj
│   ├── Infrastructure/
│   │   ├── TcpPublisherEndToEndTests.cs
│   │   ├── ReconnectBackoffTests.cs
│   │   └── WireDumpRoundTripTests.cs
│   └── Ua/
│       ├── UaSubscriberSmokeTests.cs
│       ├── UaBrowserSmokeTests.cs
│       └── Fixtures/
│           └── EmbeddedUaServerFixture.cs
└── Dc.Integration.Tests.Com/         # net8.0-windows + x64，Windows-only
    ├── Dc.Integration.Tests.Com.csproj
    ├── DaSubscriberSmokeTests.cs
    ├── DaBrowserSmokeTests.cs
    ├── AeSubscriberSmokeTests.cs
    ├── AeBrowserSmokeTests.cs
    ├── Resilience/
    │   └── DaSubscriberResilienceTests.cs
    └── Fixtures/
        ├── DemoServerFixture.cs
        └── WindowsComFactAttribute.cs
```

### Dc.Integration.Tests.csproj（跨平台）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="MessagePack" />
    <PackageReference Include="OPCFoundation.NetStandard.Opc.Ua.Server"
                      Version="1.5.374.158" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Dc.Domain\Dc.Domain.csproj" />
    <ProjectReference Include="..\..\src\Dc.Infrastructure\Dc.Infrastructure.csproj" />
    <ProjectReference Include="..\..\src\Dc.Opc.Abstractions\Dc.Opc.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\Dc.Opc.Ua\Dc.Opc.Ua.csproj" />
  </ItemGroup>
</Project>
```

### Dc.Integration.Tests.Com.csproj（Windows-only）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Platforms>x64</Platforms>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
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

### 加入 Dc.sln

两个项目都加。Linux 上 `dotnet build Dc.sln` 时 `Dc.Integration.Tests.Com.csproj` 因为 `EnableWindowsTargeting=true`（Directory.Build.props 已有）能编译过，只是不能 `dotnet test`。

## 4. WindowsCom 跳过策略

不引入新的 xunit framework，复用 `FactAttribute.Skip`。

```csharp
// ClassicCom/Fixtures/WindowsComFactAttribute.cs
using System.Runtime.Versioning;
using Microsoft.Win32;
using Xunit;

[SupportedOSPlatform("windows")]
public sealed class WindowsComFactAttribute : FactAttribute
{
    public WindowsComFactAttribute(string requiredProgId = "SampleCompany.DaSample")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "OPC DA/AE 仅 Windows";
            return;
        }
        if (!File.Exists(@"C:\Windows\SysWOW64\OpcEnum.exe"))
        {
            Skip = "OPCEnum 未安装（装 OPC Core Components）";
            return;
        }
        if (Registry.ClassesRoot.OpenSubKey(requiredProgId) is null)
        {
            Skip = $"{requiredProgId} 未 /regserver";
            return;
        }
    }
}
```

Linux 跑 → 全部 ClassicCom tests skip；Windows 缺前置 → 单测 skip 而不是 fail。

## 5. Fixtures

### 5.1 DemoServerFixture

```csharp
// 不调用 /regserver — 担心改变开发者环境。
// 仅验证状态 + 提示注册步骤。
public sealed class DemoServerFixture : IDisposable
{
    public string DaProgId  { get; } = "SampleCompany.DaSample";
    public string DaClsid   { get; } = "{5CEE2576-AA37-4D54-B02D-ECABE09A1C1E}";
    public string AeProgId  { get; } = "SampleCompany.AeSample";
    public string AeClsid   { get; } = "{71EFE996-DA6C-4256-8523-230647CFC0D0}";
    public string Host      { get; } = "localhost";

    public void Dispose() { /* 不卸载，避免破坏环境 */ }
}
```

### 5.2 EmbeddedUaServerFixture

```csharp
public sealed class EmbeddedUaServerFixture : IAsyncLifetime
{
    public Uri Endpoint { get; private set; } = default!;
    private ApplicationInstance? _app;

    public async Task InitializeAsync()
    {
        // 随机端口避免冲突
        var port = GetFreeTcpPort();
        Endpoint = new Uri($"opc.tcp://127.0.0.1:{port}");

        // 用 vendor 提供的 ReferenceServer 配置 + 随机端口覆盖
        // 配置文件嵌入资源；运行时改 BaseAddresses
        _app = new ApplicationInstance { ApplicationType = ApplicationType.Server };
        var config = await LoadAndPatchConfig(port);
        await _app.CheckApplicationInstanceCertificate(false, 2048);
        await _app.Start(new ReferenceServer { /* ... */ });
    }

    public async Task DisposeAsync() => _app?.Stop();

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
```

### 5.3 TcpListenerFixture（基础设施测试用）

```csharp
public sealed class TcpListenerFixture : IAsyncLifetime
{
    public int Port { get; private set; }
    public ChannelReader<byte[]> Frames { get; private set; } = default!;

    // 起 TcpListener，按 wire-format.md 格式读帧，推给 Channel<byte[]>
    // 测试断言 Frames 内容
}
```

## 6. 测试用例清单

### 6.1 Infrastructure（跨平台，5 个）

| ID | 测试 | 操作 | 期望 |
|---|---|---|---|
| INF-1 | TcpPublisher_帧格式正确 | 发 1 条 msgpack TagValue | listener 收到 [4B BE length][payload]，解码字段匹配 |
| INF-2 | TcpPublisher_JSON 格式 | Format=json | listener 收 utf-8 JSON，反序列化 OK |
| INF-3 | TcpPublisher_冷却 | listener.Stop() → 连发 3 条 | 第 1 条抛真错；后 2 条快速抛"冷却中"（< 50ms） |
| INF-4 | TcpPublisher_恢复 | Stop → 等 3s → Start → 再发 | 成功；`_lastError` 清空 |
| INF-5 | WireDump 端到端 | publisher → 实际起 WireDump-style listener → 解码 | 输出 JSON 行匹配原始值 |

### 6.2 UA（跨平台，3 个）

| ID | 测试 | 期望 |
|---|---|---|
| UA-1 | UaSubscriber_订阅简单变量 | 连内嵌 server → subscribe `Demo.Static.Scalar.Int32` → 收到至少 1 条 TagValue |
| UA-2 | UaBrowser_列根节点 | browse `null` → 返回 `Objects/Server/...` 至少 5 个节点 |
| UA-3 | UaBrowser_下钻 | browse `Objects` → 找 Server | 找到 Server node |

### 6.3 ClassicCom — DA（Windows-only，4 个）

| ID | 测试 | 期望 |
|---|---|---|
| DA-1 | DaSubscriber_订阅 demo 项 | subscribe `Bucket Brigade.Int4` → 收到 ≥ 1 条 TagValue，quality 0xC0 |
| DA-2 | DaBrowser_扫描 localhost | EnumerateServersAsync → 列表含 SampleCompany.DaSample URL |
| DA-3 | DaBrowser_浏览根 | browse `""` → 找到 `Bucket Brigade` 文件夹 |
| DA-4 | DaBrowser_CLSID 兜底 | URL `opcda://localhost/X/{CLSID}` → 跳过 OPCEnum 连上 |

### 6.4 ClassicCom — AE（Windows-only，3 个）

| ID | 测试 | 期望 |
|---|---|---|
| AE-1 | AeSubscriber_订阅 + 通配 | Tag.Item="*" → 收到 ≥ 1 个事件（demo server 会发心跳事件） |
| AE-2 | AeBrowser_列 Area | browse `""` → 至少 1 个 Area |
| AE-3 | AeBrowser_列 Source | browse 某 Area → 至少 1 个 Source |

### 6.5 弹性（Windows-only，2 个）

| ID | 测试 | 操作 | 期望 |
|---|---|---|---|
| RES-1 | DaSubscriber_serverKilled_pipelineSurvives | Process.Kill OpcDaAeServer → 等 5s → 重新激活 | orchestrator restart，再收到值 |
| RES-2 | DaBrowser_噪声端口扫描容错 | 扫描不存在的 host `192.0.2.1` | EnumerateServersAsync 在 10s 内抛或返空，不挂死 |

## 7. 实现约束

- xunit 集合：每个 ClassicCom 测试类标 `[Collection("Com")]` 强制串行（COM apartment 单线程敏感）
- 测试超时：每个测试 `[Fact(Timeout = 30_000)]`，弹性测试拉到 60s
- Heartbeats：UA / DA / AE 订阅器各自的 heartbeat channel 也要 drain，否则 Channel 满阻塞回调
- 资源清理：每个测试都 `await subscriber.DisposeAsync()` + `await server.DisposeAsync()`，避免 COM 进程残留
- 日志：测试加 Serilog 的 `WriteTo.TestOutput(testOutputHelper)` 方便定位

## 8. 测试矩阵预期结果

Linux 上：`dotnet test tests/Dc.Integration.Tests`（不动 `.Com` 项目）。
Windows 上：`build.ps1 -Target test` 跑两个项目。

| 环境 | 跨平台项目 INF+UA | Com 项目 DA+AE+RES | 总数 |
|---|---|---|---|
| Linux | 8 ✓（5 INF + 3 UA） | 9 不加载（TFM 不兼容） | 8 pass |
| Windows 完整环境 | 8 ✓ | 9 ✓（4 DA + 3 AE + 2 RES） | 17 pass |
| Windows 缺 OPCEnum | 8 ✓ | 9 ⊘（WindowsComFact.Skip） | 8 pass / 9 skip |
| Windows 未 /regserver | 8 ✓ | 9 ⊘（WindowsComFact.Skip） | 8 pass / 9 skip |

✓ pass / ⊘ skip / × fail

## 9. 风险与未决

- **Demo server 异常退出**：弹性测试杀进程后重启依赖 demo 自身能 /regserver 已注册的 LocalServer32 重新启动。需在 Windows 实测确认。
- **UA 内嵌服务器证书**：测试启动会生成应用证书在临时目录；ApplicationInstance 默认行为足够，但需确认证书不污染开发者 Application Data。
- **端口冲突**：UA fixture 用随机端口规避，但同一进程多个 fixture 实例需独立分配。xunit `[Collection]` 隔离应足够。
- **xunit Skip 重要性**：未跑的 test 数应在 CI 仪表上可见，不能被解释为"通过"。

## 10. 不做的事

- 不引入新的 mocking 框架（已有 Fakes 模式）
- 不重写现有 `Dc.Infrastructure.Tests` 中的 fake-based 测试
- 不做断网缓存重发（v2 单独的 feature work）
- 不做代码签名 / installer 测试（构建产物层，超出集成测试范围）

## 11. 验收标准

完成 Phase 8 的条件：

1. 两个新项目 `tests/Dc.Integration.Tests`（net8.0）+ `tests/Dc.Integration.Tests.Com`（net8.0-windows x64）加入 `Dc.sln`，整体 `dotnet build Dc.sln` 通过（Linux 也能编译，靠 EnableWindowsTargeting）
2. Linux 上 `dotnet test tests/Dc.Integration.Tests` 跑出 8 pass（不动 Com 项目）
3. Windows 上 `build.ps1 -Target test` 跑两个项目共 17 pass（前置：demo server /regserver + register-da-x64.ps1）
4. WindowsCom 任何一条 skip 给出明确原因（OS / OPCEnum 缺 / ProgID 未注册），不是静默跳过
5. `build.ps1 -Target test` 在 Windows 上必须同时 run 两个测试项目（明确 dotnet test 各自调用一次）
6. README "Windows 端到端验证清单" 段补"自动化等价物"小节，把手工 checklist 映射到集成测试 ID（INF-x / UA-x / DA-x / AE-x / RES-x）

## 附：路线图后续阶段

完成 Phase 8 后剩余 v2 候选（用户已在 brainstorm 中评估过）：

- 运维诊断面板（IT 部署）
- UA 证书严格化（产线就绪）
- 断网缓存重发（产线就绪）
- 批量帧 + 格式头（通信增强）
- CI/CD GitHub Actions（开源时）
- MSI 迁移 / 自动更新（IT 部署）
- i18n 英文化（海外客户）
