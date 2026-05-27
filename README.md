# Dc — 通用 OPC 数据采集

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-44%20passing-success.svg)](#)

.NET 8 + WPF 实现的 OPC 数据采集客户端。源自原 Wails (Go) 版本的全量重写。

**核心原则**：通用、解耦、可扩展。不假定特定 broker 协议、不绑定特定 OPC SDK、不照搬原项目设计。

---

## 已实现

| 模块 | 状态 |
|---|---|
| 配置管理（任务/分组/Tag/系统配置 4 张表） | ✅ EF Core + SQLite |
| OPC UA 订阅器（跨平台） | ✅ OPCFoundation .NET Standard |
| OPC UA 浏览器（NodeId 发现） | ✅ |
| 实时数据视图 | ✅ 事件驱动 + 质量码着色 |
| Excel 导入/导出 Tag | ✅ ClosedXML，按 GroupName 解析 |
| 任务编排（启停/心跳监控/超时自动重启） | ✅ `TaskOrchestrator` + watchdog |
| TCP 发布器（MessagePack，4 字节长度帧） | ✅ |
| 系统托盘 + 单实例锁 | ✅ |
| 滚动日志（按天） | ✅ Serilog |

| 模块 | 状态 |
|---|---|
| OPC DA 订阅器 | ⏳ 等 Windows + SDK 选定 |
| OPC AE 订阅器 | ⏳ 等 Windows |
| 打包（Inno Setup / WiX） | ⏳ 等 Windows |

详见 [`ROADMAP.md`](./ROADMAP.md)。

---

## 项目结构

```
wpf/
├── Dc.sln
├── Directory.Build.props          # CPM 启用、Nullable、ImplicitUsings
├── Directory.Packages.props       # 中央包版本锁
├── src/
│   ├── Dc.Domain/                 # 实体（无外部依赖）
│   ├── Dc.Opc.Abstractions/       # IOpcSubscriber / IOpcBrowser / TagValue 等
│   ├── Dc.Infrastructure/         # EF Core + 序列化 + TCP + 编排
│   ├── Dc.Opc.Da/                 # DA 实现（占位，等 SDK）
│   ├── Dc.Opc.Ua/                 # UA 订阅器 + 浏览器（OPC Foundation）
│   ├── Dc.Opc.Ae/                 # AE 实现（占位）
│   └── Dc.App/                    # WPF 主程序（MVVM + DI）
└── tests/
    └── Dc.Infrastructure.Tests/   # 38 个 xUnit 测试
```

### 关键设计取舍

- **EF Core DbContext 直接用，不包一层 Repository** — EF Core 本身就是 Repository+UoW，再包是 Go-leak
- **序列化器与 Publisher 分离 + 泛型** — `PublishAsync<T>(T)` 不限定消息类型，broker 端协议由部署方决定
- **TaskOrchestrator 单对象 API** — 替代 Go 端 3 channel + 3 sync.Map 钢琴键
- **IOpcSubscriber 暴露 `ChannelReader<T>` 只读** — 外部不能直接乱写订阅器内部状态
- **质量码用 `0xC0/0x40/0x00` 位运算解析** — 而不是 `quality > 0`（文章踩过的坑）
- **数据库列名锁定 `dc_` 前缀 + snake_case** — 可直接复用现有 Wails 版本的 `sqlite.db`

---

## 构建

```powershell
cd wpf
git submodule update --init --recursive  # 首次拉 Technosoftware DA/AE/HDA 客户端
.\build.ps1                              # = dotnet build Dc.sln -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
.\build.ps1 -Target test                 # 跑 44 测试
.\build.ps1 -Target run                  # 启动 Dc.App
```

或不用脚本：
```bash
dotnet build Dc.sln -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
```

**为什么两个 -p 旗必须**：
- `submodule` = `vendor/ClassicClient`，是 Technosoftware OPC DA/AE/HDA 源码（GPL-3.0 / 商业双轨）
- `Platform=x64` 必需 — vendor `DaAeHdaClient.Com` 的 `NETCORE` 宏只在 x86/x64 平台定义，否则 FILETIME 找不到
- `CustomTestTarget=net8.0-windows` 让 vendor 只编 net8 一份（默认编 5 个 TFM 含 net9/net10），无该 SDK 时会 NETSDK1045

Linux 上能构建 WPF/COM 目标项目（已开启 `EnableWindowsTargeting`），但 WPF 应用只能在 Windows 上运行。

### 跑测试

```bash
dotnet test tests/Dc.Infrastructure.Tests
```

---

## 运行（Windows）

```powershell
dotnet run --project src/Dc.App
```

首次运行会：
1. 在 `./sqlite.db` 创建空数据库
2. 在 `./logs/dc-yyyyMMdd.log` 写日志
3. 在 `./pki/` 生成 OPC UA 自签证书

### 配置（可选）

修改同目录 `appsettings.json` 可外部化以下设置（不需要重编译）：

```json
{
  "Database": { "Path": "sqlite.db" },
  "Orchestrator": {
    "WatchdogIntervalSeconds": 30,
    "HeartbeatTimeoutSeconds": 120
  }
}
```

要采集 OPC UA：
1. 任务管理 → 新建：协议选 UA，节点填 `opc.tcp://host:4840`，TCP 地址填下游 broker 的 `host:port`
2. OPC 浏览 → 输入 server URI → 连接 → 复制感兴趣的 NodeId
3. 分组管理 → 在该任务下新建一个分组
4. Tag 管理 → 新建 Tag，Item 填刚才的 NodeId
5. 任务管理 → 选中任务 → 启动
6. 实时数据 → 看到值流入

### 本地验证 OPC DA（无需额外下载）

vendor submodule 自带一份 Technosoftware Demo Server，可直接当 DA / AE 测试源：

```powershell
# 管理员 PowerShell，进入 vendor 目录
cd wpf\vendor\ClassicClient\x86\DemoServer

# 注册 COM（首次运行需要）
.\OpcDaAeServer.exe /regserver
.\OpcHdaServer.exe /regserver   # 可选，HDA 用

# 启动（保持窗口开着，关闭即停止服务）
.\OpcDaAeServer.exe
```

注册后用注册表 `HKCR\CLSID\...\ProgID` 或下文「服务器发现」接口可拿到 ProgID。
完事卸载：`.\OpcDaAeServer.exe /unregserver`。

> 备选：[KEPServerEX Demo](https://www.ptc.com/en/products/kepware/kepserverex/demo)（2 小时重启限制，自带 Simulator 通道，ProgID `Kepware.KEPServerEX.V6`）；或 Matrikon OPC Simulation Server。

采集 OPC DA（流程与 UA 同，差在协议选 DA、节点填主机/IP、Tag 的 Item 填 OPC 项名如 `Random.Real8`）：
1. 任务管理 → 新建：协议选 DA，节点填 `localhost` 或远程 `192.168.1.10`，ProgID 填如 `Technosoftware.DaSample`
2. 分组管理 → 新建分组
3. Tag 管理 → Item 填 OPC 项 ID（如 `Bucket Brigade.Int4`）
4. 启动 → 实时数据观察

### 远程发现 OPC 服务器（按 IP 枚举）

走 DCOM OPCEnum 服务，vendor 已实现 `IOpcDiscovery`：

```csharp
using Technosoftware.DaAeHdaClient;
using Technosoftware.DaAeHdaClient.Com;

IOpcDiscovery discovery = new ServerEnumerator();

// ① 列网络内能枚举到的主机（依赖 NetBIOS / 网络浏览）
string[] hosts = discovery.EnumerateHosts();

// ② 按主机 IP 列该机上的 DA 服务器（需要 DCOM 权限和 OPCEnum 安装）
var servers = discovery.GetAvailableServers(
    OpcSpecification.OPC_DA_30,
    host: "192.168.1.10",
    connectData: new OpcConnectData(new System.Net.NetworkCredential("user", "pwd", "DOMAIN"))
);
foreach (var s in servers) Console.WriteLine($"{s.Url}  {s.ServerName}");
```

跨机调用前提：
- 远端装了 OPCEnum.exe（OPC Core Components Redistributable 自带，免费）
- DCOM 配置允许调用方账号（`dcomcnfg` 调 OpcEnum + 目标服务器组件的启动/激活/访问权限）
- 防火墙放 135/TCP + DCOM 动态端口段

DCOM 配置是 OPC DA 跨机的老大难，建议优先本机 demo server 走通流程再做远程。

#### UI 上的「扫描 OPC 服务器」（Phase 3c 已实现）

OPC 浏览页面，协议选 **DA** 即出现第二行扫描栏：

1. **按 IP 扫描** 框填目标 host/IP（本机填 `localhost`）→ 点 **扫描 OPC 服务器**
2. 下拉框显示扫到的 `opcda://host/progId` 列表，选中后自动回填 Host / ProgID
3. 点 **连接** → 树状浏览节点 → 复制 NodeId 用于 Tag.Item

跨机扫描时 DCOM 报权限错（一般是 `0x80070005 拒绝访问`、`0x800706BA RPC 不可用`），先按上面三条前提排查。

---

## 打包安装包（Inno Setup）

Phase 7 — 一键出 Windows 安装包 `.exe`。

**前置**：装 [Inno Setup 6](https://jrsoftware.org/isdl.php)。默认装到 `C:\Program Files (x86)\Inno Setup 6\`；装别处的设环境变量 `INNO_SETUP_DIR` 指向其目录。

```powershell
# 出 build\installer\Dc-Setup-x64-1.0.0.exe（约 100~200MB，含 .NET 运行时）
.\build.ps1 -Target installer

# 自定义版本号
.\build.ps1 -Target installer -Version 1.2.3
```

干了什么：
1. `dotnet publish src/Dc.App` Release x64 **self-contained**（用户机不需要预装 .NET 8 Desktop Runtime）
2. 把发布产物 + `scripts/*.ps1` + `docs/wire-format.md` + LICENSE 打进 Inno Setup
3. 编译出 `build/installer/Dc-Setup-x64-<version>.exe`

安装时的行为：
- 默认装到 `C:\Program Files\Dc\`（管理员权限）
- 自动检测 `C:\Windows\SysWOW64\OpcEnum.exe` — 缺失就弹窗提示装 OPC Foundation Core Components（不强制，可选 CLSID 直连兜底）
- 创建开始菜单快捷方式；勾选可加桌面快捷方式
- 卸载只清自己装的文件；用户的 `sqlite.db`、`logs/` 等数据保留

**不带的功能**（v2 候选）：
- **代码签名** — 产线分发建议买 EV 代码签名证书避免 SmartScreen 拦截
- **MSI 格式** — 当前是 Inno Setup 的 `.exe` 自解压；IT 部门要走 SCCM / Intune 部署时再迁移到 WiX
- **自动更新** — 当前每次升级都跑安装包

---

## Windows 端到端验证清单

把 Phase 3–7 串成一条可执行的 checklist。每步给"做什么 + 怎么算通"。失败定位回到对应 Phase 章节。

### 0. 环境准备（一次性）

| 组件 | 强制 | 用途 |
|---|---|---|
| Windows 10/11 x64 | ✓ | 必需 |
| .NET 8 SDK | ✓ (dev) | 源码构建；用安装包跑则不需要 |
| PowerShell 5.1+ | ✓ | 系统自带 |
| Inno Setup 6 | 仅 Phase 7 | [下载](https://jrsoftware.org/isdl.php) |
| OPC Foundation Core Components | 仅扫描功能 | [下载](https://opcfoundation.org/developer-tools/samples-and-tools-classic/core-components/)（免费帐号） |

### 1. Phase 3a — OPC UA 订阅

```powershell
.\build.ps1 -Target run
```
- 任务管理 → 新建：协议 **UA**，服务器 `opc.tcp://opcua.demo-this.com:51210/UA/SampleServer`（或本地 Prosys / KEPServerEX UA endpoint），TCP 地址 `127.0.0.1:5000`
- 浏览页 → 协议 UA → 连接 → 应见节点树
- 任务下加 Tag（Item = 浏览到的 NodeId）→ 启动任务 → LiveData 看值流入

**通过判据**：LiveData 行数随时间增长；任务行 `IsRunning=true`。

### 2. Phase 3b — OPC DA 订阅（vendor demo server）

```powershell
# 管理员
cd vendor\ClassicClient\x86\DemoServer
.\OpcDaAeServer.exe /regserver

cd ..\..\..\..\scripts
.\register-da-x64.ps1                              # 同步 OPCEnum + DA server 到 64-bit 视图
.\diag-opcda.ps1                                   # （可选）复查注册表 6 维诊断
```
- 任务管理 → 新建：协议 **DA**，服务器 `SampleCompany.DaSample`，节点 `localhost`
- 启动任务 → LiveData 看值

**通过判据**：LiveData 有数据；事件查看器无 DCOM 10010。
**失败兜底**：扫描或连接失败 → 浏览页 "高级" Expander 填 CLSID `{5CEE2576-AA37-4D54-B02D-ECABE09A1C1E}` 跳过 OPCEnum。

### 3. Phase 3c — DA 浏览 + IP 扫描

- 浏览页 → 协议 DA → 主机 `localhost` → **扫描 OPC 服务器** → 下拉应有 `opcda://localhost/SampleCompany.DaSample/{5CEE2576-...}`
- 选中 → ProgID/CLSID 自动填 → **连接** → DataGrid 展示 DA item 树
- 双击文件夹下钻；双击叶子 → 复制 NodeId 备用

**通过判据**：能列服务器、能展开树。
**失败兜底**：扫描报 `CO_E_SERVER_EXEC_FAILURE` → 跑 `.\check-opc-corecomponents.ps1` 检 OpcEnum.exe 是否存在。

### 4. Phase 4 — OPC AE 订阅 + Area/Source 浏览

```powershell
# 注册 AE server CLSID 到 64-bit 视图（OPCEnum 已同步过则 -SkipOpcEnum）
.\register-da-x64.ps1 -ProgId SampleCompany.AeSample -SkipOpcEnum
```
- 任务管理 → 新建：协议 **AE**，服务器 `SampleCompany.AeSample`，节点 `localhost`
- 加 Tag：Item = `*`（全收）或具体 Source ID
- 浏览页 → 协议 AE → 连接 → 应见 Area/Source 树（双击 Area 下钻，Source 节点的 NodeId 即 SourceID）
- 启动任务 → LiveData 看事件（severity/message/condition 字段在 Value 字典里）

**通过判据**：LiveData 收到事件；浏览页 AE 树非空。

### 5. Phase 5 — 序列化格式切换

```powershell
# 1. 改 src\Dc.App\appsettings.json（或安装目录下的）：
#    "Messaging": { "Format": "json" }
# 2. 重启 Dc.App
```
**通过判据**：Phase 6 用 WireDump `--format json` 能解码消息。
**回退**：改回 `"msgpack"`。

### 6. Phase 6 — 端到端发布验证

两个终端：

**终端 A**（接收器）：
```powershell
cd wpf
dotnet run --project tools\Dc.WireDump -- --port 5000 --format msgpack
```

**终端 B**（应用）：
```powershell
.\build.ps1 -Target run
# 任务的 TCP 地址 127.0.0.1:5000，启动任务
```

**通过判据**：终端 A 持续打印解码后的 TagValue JSON；TCP 断开（关掉 WireDump 重开）→ Dc.App 任务的 `PublishErrorCount` 涨但 2 秒后恢复，无僵尸状态。

### 7. Phase 7 — 安装包

```powershell
.\build.ps1 -Target installer -Version 1.0.0
```
**通过判据**：`build\installer\Dc-Setup-x64-1.0.0.exe` 生成；双击安装无报错；开始菜单出现 Dc 快捷方式；启动 Dc.App 工作正常；卸载干净。

**附加检查**：在没装 Core Components 的环境上安装 → 应弹"未检测到 OPCEnum"提示，点"是"继续可装；点"否"中止。

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

Linux / WSL 跑跨平台子集（仅 Infrastructure + UA = 9 tests）：

```bash
dotnet test wpf/tests/Dc.Integration.Tests
```

手工 7 步保留，作为"装包后真机走一遍"的产线验收 — 集成测试覆盖代码层正确性，不替代真硬件 / 真服务器的最终确认。

---

## OPC UA 证书管理

默认配置（自 Phase 8b）：

```json
"OpcUa": {
  "AutoAcceptUntrustedCertificates": false,
  "MinimumCertificateKeySize": 2048
}
```

**首次连接陌生 UA server** 会被拒绝 — 在 `logs/dc-*.log` 看到拒绝记录后，按下面流程把 server 证书加入信任：

1. 第一次尝试连接 → 失败，OPC UA 栈把 server 证书写到 `pki/rejected/certs/<thumbprint>.der`
2. 人工审核该证书是否可信（看 Subject / Issuer）
3. 把 `.der` 文件从 `rejected/certs/` 移到 `pki/trusted/certs/`
4. 重启 Dc.App，重新连接即可成功

**dev 环境快速跳过**（不推荐用于产线）：

```json
"OpcUa": { "AutoAcceptUntrustedCertificates": true }
```

**目录结构**（位于安装目录的 `pki/` 下）：

| 目录 | 内容 |
|---|---|
| `pki/own/` | Dc.App 自己的客户端证书（首次启动自动生成） |
| `pki/trusted/` | 信任的 server 证书（人工放） |
| `pki/issuer/` | 信任的 CA 颁发者证书 |
| `pki/rejected/` | 拒绝过的证书记录（供审核） |

---

## 已知限制

- **远程 DA 发现/连接受 DCOM 配置约束** — 跨机 DA 强依赖 OPCEnum + DCOM 权限，常见坑见 OPC 经典文档
- **TCP Publisher 单连接、无队列** — 已加 2 秒重连冷却 + 单次发送 5s 超时（Phase 6），但**不缓存重发**；若业务要"断网期间先攒后补发"需要扩展 Publisher
- **MessagePack 序列化不带 Type 信息** — 接收端必须知道消息类型（参见 [`docs/wire-format.md`](docs/wire-format.md)）。如要带类型，加 `TypelessFormatter` 或换 typeless resolver
- **安装包未代码签名** — Phase 7 出的 `.exe` 在 SmartScreen 严格的环境下首次运行会被拦截；产线分发前需要 EV 证书 + signtool

---

## License

本项目使用 **GPL-3.0**（见 [LICENSE](LICENSE)）。

**为什么是 GPL？** 项目依赖的 `OPCFoundation.NetStandard.Opc.Ua.Client` 和（计划中的）`Technosoftware.DaAeHdaClient` 均采用双轨许可（GPL / 商业版），非会员/非购买方默认走 GPL。GPL 的传染机制使得整个产品必须同样采用 GPL 或更严许可。

**实际影响**：
- ✅ 任何开源项目可以集成、修改、分发此代码
- ✅ 内部使用、私有部署 不触发任何分发义务
- ✅ 可作为商业服务运行（GPL 允许"在云端跑"，AGPL 才管这事）
- ❌ 不能将此代码塞进**闭源商业软件**分发

要去掉 GPL 传染：需购买 OPC Foundation 会员（UA 库商业 license）+ Technosoftware 商业版（DA/AE 库商业 license）。届时可申请 dual-license 此项目。

第三方依赖完整许可证清单见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
