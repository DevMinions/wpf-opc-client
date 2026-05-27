# Dc — 通用 OPC 数据采集客户端

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D6.svg)](#)

.NET 8 + WPF 实现的 OPC 数据采集客户端：浏览 OPC 地址空间、配置采集任务、实时订阅 OPC UA / DA / AE 数据，并通过 TCP 发布到下游 broker。

**核心原则**：通用、解耦、可扩展 —— 不假定特定 broker 协议、不绑定特定 OPC SDK。

---

## 界面截图

> Fluent Design · 亮/暗主题跟随系统 · 以下为 Windows 端实跑截图。

![仪表盘](docs/screenshots/dashboard.png)

| 采集任务（master-detail + 6 tab） | 浏览节点 |
|---|---|
| ![采集任务](docs/screenshots/workspace.png) | ![浏览节点](docs/screenshots/browse.png) |
| **实时数据** | **诊断** |
| ![实时数据](docs/screenshots/livedata.png) | ![诊断](docs/screenshots/diagnostics.png) |
| **运行日志** | **设置** |
| ![日志](docs/screenshots/logs.png) | ![设置](docs/screenshots/settings.png) |

---

## 功能

| 模块 | 说明 |
|---|---|
| 配置管理 | 任务 / 分组 / Tag / 系统配置 4 张表，EF Core + SQLite |
| OPC UA 订阅 + 浏览 | OPC Foundation .NET Standard SDK，证书信任链 |
| OPC DA 订阅 + 浏览 + IP 扫描 | Technosoftware DaAeHdaClient（COM/DCOM，需 Windows） |
| OPC AE 订阅 + Area/Source 浏览 | 同上 |
| 实时数据视图 | 事件驱动 + 三态质量码着色（Good/Uncertain/Bad） |
| 任务编排 | `TaskOrchestrator`：启停 / 热增删 Tag / 心跳监控 / 超时自动重启 |
| TCP 发布 | MessagePack / JSON 可切换，wire v1.1（magic + format-id），冷却重连 + 可选离线队列 |
| 诊断面板 | 每任务速率 / 错误 / 重启 / 心跳，sparkline 趋势 |
| Excel 导入/导出 Tag | ClosedXML，按 GroupName 解析 |
| 系统托盘 + 单实例锁 · 滚动日志（Serilog） | |

路线图见 [`ROADMAP.md`](./ROADMAP.md)。

---

## 架构

Clean Architecture，依赖单向（UI → Infrastructure → Abstractions/Domain）：

```
src/
├── Dc.Domain/             # 实体（无外部依赖）
├── Dc.Opc.Abstractions/   # IOpcSubscriber / IOpcBrowser / TagValue / OpcProtocol
├── Dc.Opc.Ua/             # UA 订阅器 + 浏览器（OPC Foundation）
├── Dc.Opc.Da/             # DA 订阅器 + 浏览器（Technosoftware）
├── Dc.Opc.Ae/             # AE 订阅器 + 浏览器（Technosoftware）
├── Dc.Infrastructure/     # EF Core + 序列化 + TCP 发布 + 编排
└── Dc.App/                # WPF 主程序（MVVM + DI）
tests/                     # xUnit 单元 + 集成测试
tools/Dc.WireDump/         # 接收端调试工具（解析 wire 帧）
```

### 关键设计取舍

- **DbContext 直接用，不包 Repository** —— EF Core 本身即 Repository+UoW。
- **序列化器与 Publisher 分离 + 泛型** —— `PublishAsync<T>(T)` 不限定消息类型，broker 端协议由部署方决定。
- **`TaskOrchestrator` 单对象 API** —— 一个对象管全部任务生命周期 + 热更新 + 看门狗。
- **`IOpcSubscriber` 只暴露 `ChannelReader<T>`** —— 外部只读拉数据，订阅器内部状态不可外写。
- **质量码用 `0xC0/0x40/0x00` 位运算解析**（非 `quality > 0`）。
- **数据库列 `dc_` 前缀 + snake_case**（`EFCore.NamingConventions`）。

---

## 构建

需要 .NET 8 SDK。首次拉取需初始化子模块（Technosoftware DA/AE/HDA 客户端）：

```bash
git clone --recursive https://github.com/DevMinions/wpf-opc-client.git
cd wpf-opc-client
# 已 clone 未带 --recursive：
git submodule update --init --recursive

dotnet build Dc.sln -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet test tests/Dc.Infrastructure.Tests
```

Windows 上可用脚本：

```powershell
.\build.ps1                 # 构建
.\build.ps1 -Target test    # 跑测试
.\build.ps1 -Target run     # 启动应用
```

**为什么需要两个 `-p` 参数**：
- `Platform=x64` —— vendor `DaAeHdaClient.Com` 的 `NETCORE` 宏只在 x86/x64 平台定义，否则编译报 `FILETIME` 找不到。
- `CustomTestTarget=net8.0-windows` —— 让 vendor 只编 net8 一份（默认编 5 个 TFM 含 net9/net10），无对应 SDK 会 NETSDK1045。

> Linux/WSL/macOS 可**构建**非 GUI 项目并跑跨平台测试，但 **WPF 应用与 OPC DA/AE（COM）只能在 Windows 运行**。

---

## 运行（Windows）

```powershell
dotnet run --project src/Dc.App
```

首次运行会在工作目录创建：`sqlite.db`（空库）、`logs/dc-yyyyMMdd.log`、`pki/`（OPC UA 自签证书）。

### 采集 OPC UA

1. 采集任务 → 新建：协议选 **UA**，节点填 `opc.tcp://host:4840`，TCP 地址填下游 broker 的 `host:port`
2. 浏览节点 → 输入 server URI → 连接 → 复制感兴趣的 NodeId
3. 在该任务下新建分组 → 新建 Tag（Item 填 NodeId）
4. 选中任务 → 启动 → 实时数据看值流入

### 采集 OPC DA（本地 demo server）

vendor 子模块自带 Technosoftware Demo Server，可直接当 DA/AE 测试源：

```powershell
# 管理员 PowerShell
cd vendor\ClassicClient\x86\DemoServer
.\OpcDaAeServer.exe /regserver   # 首次注册 COM
.\OpcDaAeServer.exe              # 启动（保持窗口开着）
```

然后：采集任务 → 协议 **DA**，节点 `localhost`，ProgID `SampleCompany.DaSample`；浏览节点页协议选 DA 会出现「扫描 OPC 服务器」栏，扫到后自动回填。

> 备选 demo server：KEPServerEX Demo、Matrikon OPC Simulation Server。
> 跨机 DA 依赖 OPCEnum + DCOM 权限，配置较繁琐，建议先用本机 demo server 走通流程。

### 配置

同目录 `appsettings.json` 可外部化（无需重编译）：

```json
{
  "Database": { "Path": "sqlite.db" },
  "Messaging": { "Format": "msgpack" },
  "Orchestrator": { "WatchdogIntervalSeconds": 30, "HeartbeatTimeoutSeconds": 120 },
  "OpcUa": { "AutoAcceptUntrustedCertificates": false, "MinimumCertificateKeySize": 2048 }
}
```

---

## 打包安装包

需装 [Inno Setup 6](https://jrsoftware.org/isdl.php)（默认路径，或设环境变量 `INNO_SETUP_DIR`）。

```powershell
.\build.ps1 -Target installer                 # 出 build/installer/Dc-Setup-x64-<version>.exe
.\build.ps1 -Target installer -Version 1.2.3
```

产出 self-contained 安装包（用户机不需预装 .NET 运行时），含发布产物 + 脚本 + 文档 + LICENSE。安装时自动检测 OPCEnum，缺失会提示安装 OPC Foundation Core Components。

> 未带：代码签名（产线建议 EV 证书避免 SmartScreen）、MSI 格式、自动更新。

---

## OPC UA 证书管理

默认安全基线：`AutoAcceptUntrustedCertificates = false`、`MinimumCertificateKeySize = 2048`。

首次连接陌生 UA server 会被拒绝，其证书写入 `pki/rejected/certs/`。人工审核后移到 `pki/trusted/certs/`，重启即可连接。

| 目录 | 内容 |
|---|---|
| `pki/own/` | 客户端自己的证书（自动生成） |
| `pki/trusted/` | 信任的 server 证书（人工放入） |
| `pki/issuer/` | 信任的 CA 颁发者证书 |
| `pki/rejected/` | 被拒证书（供审核） |

dev 环境可设 `"AutoAcceptUntrustedCertificates": true` 跳过（不推荐用于生产）。

---

## 已知限制

- **远程 DA 发现/连接受 DCOM 配置约束** —— 跨机 DA 强依赖 OPCEnum + DCOM 权限。
- **TCP Publisher 单连接** —— 带 2s 重连冷却 + 发送超时；离线队列为可选（默认关）。
- **MessagePack 不带类型信息** —— 接收端需知消息类型，见 [`docs/wire-format.md`](docs/wire-format.md)。
- **安装包未代码签名** —— SmartScreen 严格环境首次运行会拦截。

---

## 贡献

欢迎 issue 与 PR。提交前请确保 `dotnet test tests/Dc.Infrastructure.Tests` 通过。详见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## License

**GPL-3.0**（见 [LICENSE](LICENSE)）。

项目依赖的 `OPCFoundation.NetStandard.Opc.Ua.Client` 与 `Technosoftware.DaAeHdaClient` 均为 GPL / 商业双轨许可，非购买方默认走 GPL，因此本项目整体以 GPL-3.0 分发——**任何人可自由使用、修改、再分发，但衍生作品须同样开源（GPL）**。如需闭源商用，需分别购买 OPC Foundation 与 Technosoftware 的商业许可。

第三方依赖完整许可证清单见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
