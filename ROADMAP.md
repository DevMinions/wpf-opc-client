# WPF OPC Collector — 重写路线图

将原 Wails（Go + Vue3）版本的 OPC 数据采集应用，用 .NET 8 + WPF 全量重写。

## 锁定的架构决策

| 项目 | 决策 |
|---|---|
| 分支 | `wpf-opc-collector` |
| 代码目录 | `wpf/`（与原 Go 代码同仓库共存，直到完全切换） |
| 运行时 | .NET 8 LTS |
| UI 框架 | WPF + CommunityToolkit.Mvvm + ModernWpf/HandyControl（视觉接近 Arco） |
| OPC DA | Technosoftware OpcClientSdk（纯托管，无需 `gbda_aut.dll`） |
| OPC UA | OPC Foundation UA .NET Standard |
| OPC AE | Technosoftware（同系列） |
| ORM | EF Core 8 + Microsoft.Data.Sqlite，配置 snake_case + `dc_` 表前缀，**直接复用现有 sqlite.db** |
| 序列化 | `IMessageSerializer` 接口为一等公民；首版内置 **MessagePack-CSharp**；可按 Task 配置切换 |
| 传输 | TCP 长连接 publisher（不绑定具体 broker，做通用产品） |
| 并发原语 | `System.Threading.Channels` + `ConcurrentDictionary<string, CancellationTokenSource>` |
| 系统集成 | `Hardcodet.NotifyIcon.Wpf`（托盘）+ 命名 `Mutex`（单实例） |
| 不做 | 内嵌 REST/SSE、UA/MQTT bridge 等文章里的衍生功能 |

## 项目结构（待 Phase 0 落地）

```
wpf/
├── Dc.sln
├── src/
│   ├── Dc.Domain/                 # 实体 + 仓储接口（无依赖）
│   ├── Dc.Infrastructure/         # EF Core 实现 + 序列化器 + TCP 发布器
│   ├── Dc.Opc.Abstractions/       # ISubscriber, Config, TagValue, HeartBeat
│   ├── Dc.Opc.Da/                 # OpcClientSdk 实现
│   ├── Dc.Opc.Ua/                 # OPC Foundation 实现
│   ├── Dc.Opc.Ae/                 # AE 实现
│   └── Dc.App/                    # WPF 主程序（MVVM）
└── tests/
    ├── Dc.Infrastructure.Tests/
    ├── Dc.Opc.Da.IntegrationTests/   # 需要 Matrikon Simulation Server
    └── Dc.Opc.Ua.IntegrationTests/
```

## 阶段与验证标准

每个阶段必须满足 **"Verify"** 才能进入下一阶段。

### Phase 0 — Scaffolding（半天）
- 建 `Dc.sln`、上述七个项目骨架
- 配 `.gitignore`（bin/obj/*.user）
- 锁定 NuGet：Technosoftware、OPCFoundation.NetStandard.Opc.Ua、MessagePack、EFCore.Sqlite、CommunityToolkit.Mvvm、Serilog、Hardcodet.NotifyIcon.Wpf
- **Verify**：`dotnet build wpf/Dc.sln` 零警告通过；`dotnet run --project Dc.App` 弹出空白窗口

### Phase 1 — Domain + Persistence（2~3 天）
- 移植 `pkg/model/{tag,group,task,config}` → C# 实体（与 Go 字段名一一对应）
- EF Core `DcDbContext`，`NamingPolicy` snake_case，`TablePrefix` = `dc_`
- 仓储接口 + EF 实现（Get/List/Create/Update/Delete + Tag 复合唯一索引）
- **Verify**：拿一份现有项目的 `sqlite.db` 副本，用 EF Core 读出来，行数、字段、外键关系与 GORM 读出来的完全一致（写一个自检脚本对比）

### Phase 2 — Serializer + TCP Publisher（1~2 天）
- `IMessageSerializer { byte[] Serialize<T>(T msg); }` 在 `Dc.Infrastructure`
- `MessagePackMessageSerializer` 实现（attribute-less 模式或 GeneratedResolver）
- `TcpPublisher`：长连接 + 简单帧（4 字节长度 + payload）+ 自动重连 + 关闭语义
- **Verify**：单元测试序列化往返；集成测试启动 netcat 监听 → publisher 发送 → bytes 与预期一致

### Phase 3 — OPC 抽象 + DA（3~4 天）
- `ISubscriber` 接口 + `OpcConfig` record + `TagValue` 记录（含 IsGood 位运算）
- `OpcDaSubscriber`：所有 COM 调用进 `_comLock`、`Channel<TagValue>` 背压、批量 Read、`DataChange` 事件 → Channel
- 浏览器：`OpcDaBrowser` 列出服务器、列出 NodeID 树
- **Verify**：连 Matrikon Simulation Server，订阅 `Random.Int1`、`Random.Real8` 各 100 个，Channel 持续收到值；强行关闭 OPC Server，订阅在 StateCh 信号下退出

### Phase 4 — UA + AE（4~5 天）
- `OpcUaSubscriber`（OPC Foundation Session+MonitoredItem）
- `OpcAeSubscriber`（Technosoftware）
- **Verify**：UA 连 Prosys / Unified Automation 模拟器；AE 连 KEPServerEX 模拟器；两者都能正常推送

### Phase 5 — TaskManager + 生命周期（3 天）
- 三个 `Channel<T>` 对应原 Go 的 `ch` / `changeCh` / `stateCh+heartBeat`
- `ConcurrentDictionary<string, CancellationTokenSource>` = `serCancelMap`
- 心跳监控：`>150s` 未刷新 → 取消并重启
- **Verify**：脚本依次 create / update / delete task，watchdog 在断开 OPC 后 3 分钟内重连；并发创建 10 个 task 不出现重复订阅

### Phase 6 — WPF UI（2~3 周）
- Shell：侧边菜单 + 内容 ContentControl
- 视图：任务列表、任务编辑、组管理、Tag 树（OPC Browse）、实时数据网格、配置、日志
- ViewModel 全部 `[ObservableProperty]` + `[RelayCommand]`，零代码后置
- 托盘 + 单实例 + 启动隐藏窗口
- Excel 导入导出（ClosedXML）
- **Verify**：与现有 Wails 应用做并排功能 walk-through，每个用户故事都能在新应用上完成

### Phase 7 — 打包与回归（1 周）
- `dotnet publish -c Release -r win-x64 --self-contained`
- Inno Setup 安装器（替代现有 NSIS）
- 在干净 Win10 / Win11 上端到端跑一次
- **Verify**：完成切换准则：所有 Phase 1~6 Verify 全部通过，且现有用户能在不丢配置情况下从 Wails 版本升级到 WPF 版本（同一份 `sqlite.db` 直接被新版打开）

## 切换准则（什么时候删 Go 代码）

**全部满足才动 Go 代码**：

1. Phase 7 通过
2. 至少一个真实生产现场跑过 7×24 ≥ 7 天，未出现订阅卡死、内存泄漏、心跳异常
3. WPF 版本能读现有 sqlite.db 的所有历史配置且行为一致
4. 用户确认接受 broker 协议变更（MessagePack 替代 protobuf）

满足后，单独再开一次 PR 删除根目录下的 Go 文件和 `frontend/`、`opc/`、`pkg/`。

## 当前进度

- [x] 创建分支 `wpf-opc-collector`
- [x] 创建 `wpf/` 目录
- [x] 写本路线图
- [x] **Phase 0 完成**：Dc.sln + 7 项目骨架、CPM 包版本锁、Linux 上 `dotnet build` 通过（0 警告 0 错误）
- [ ] Windows 端验证：`dotnet run --project src\Dc.App` 弹出 Phase 0 占位窗口（待用户在 Windows 机器上跑一遍）
- [x] **Phase 1 完成**：Domain 实体（EntityBase / Tag / Group / CollectorTask / ConfigEntry）+ 仓储接口与 EF Core 实现（snake_case + `dc_` 前缀，FK 强制关闭以匹配 GORM 行为）+ ULID 主键 + 6/6 xUnit 测试通过（表名、列名、唯一索引、嵌套 Include、ConfigEntry GetByKey）
- [ ] 待 Windows 端拿真实 `sqlite.db` 做一次对照（行数 / 字段一致性）
- [x] **Phase 2 完成**：泛型 `IMessageSerializer<T>` + `MessagePackMessageSerializer`（contractless，无需 attribute）+ `IPublisher` + `TcpPublisher`（4 字节大端长度帧 + `SemaphoreSlim` 串行化 + 自动重连）+ 7/7 messaging 测试（roundtrip、FormatId、单帧、多帧、重连、地址解析有效/无效）。**设计取舍**：删除了 Mainflux 风格的 `Message` envelope，调用方传任意类型；TcpPublisher 不绑定任何特定数据契约。累计 13/13 测试通过。
- [x] **Phase 3a 完成（抽象层）**：`Dc.Opc.Abstractions` 提供 `TagValue`（含 IsGood/IsUncertain/IsBad 位运算）/ `TagDescriptor` / `HeartBeat` / `OpcConnectionOptions` / `OpcNode` / `IOpcSubscriber` / `IOpcSubscriberFactory` / `IOpcBrowser` / `OpcProtocol`。**设计取舍**：废弃 Go 端 `sync.Map`+裸 channel 暴露，改用 `ChannelReader<T>` 只读外露 + Tag 增删通过 `Subscribe/Unsubscribe` 方法；Heartbeat 是订阅器主动产出而不是外部塞入。7 个质量码组合用例通过（含 0xC0 Good、0x40 Uncertain、0x00 Bad、保留位 0x80）。累计 20/20 测试通过。
- [ ] **Phase 3b（DA 实现）**：等 Windows 端，需先选定 SDK（TitaniumAS 免费 vs Technosoftware 商用）
- [x] **Phase 5 完成（TaskOrchestrator）**：单对象 API 替代 Go 端 3 channel + 3 sync.Map 钢琴键。提供 `StartAsync` / `StopAsync` / `AddTagsAsync` / `RemoveTagsAsync` + 内置 watchdog（heartbeat 超时自动重启）+ `SemaphoreSlim` 互斥 + 心跳超时驱动重启。`IPublisherFactory` / `TcpPublisherFactory` 提供 per-task publisher 创建。测试用 `FakeOpcSubscriber` + `FakeOpcSubscriberFactory` + `FakePublisher` + `FakePublisherFactory` 驱动 12 个用例：start/stop/replace、值流到 publisher、AddTags/RemoveTags 幂等、心跳新鲜不重启、心跳过期重启、并发 10 task 无重复、Dispose 清空、未知协议抛错。累计 32/32 测试通过。
- [x] **Phase 6a 完成（App 引导）**：Dc.App 接入 Generic Host（`Microsoft.Extensions.Hosting`）+ DI（`Microsoft.Extensions.DependencyInjection`）+ Serilog（控制台 + 按天滚动文件 `logs/dc-YYYY-MM-DD.log`）+ CommunityToolkit.Mvvm。`ServiceRegistration.AddDcApp(sqlite.db)` 注册 DbContext / 序列化器 / Publisher 工厂 / TaskOrchestrator / MainWindow / VM 全家桶。MainWindow 改 DI 注入 ViewModel；MainWindow.xaml 给出 220px 侧栏 + 内容区 + 状态栏的 shell 雏形。`Database.EnsureCreated()` 启动时自动建表。
- [x] **重构（清理）**：删除 Go 遗产的 `IXxxRepository` + `XxxRepository` 4×2 文件，全切到 EF Core `IDbContextFactory<DcDbContext>` 直连（EF Core 本身就是 Repository+UoW，二次包装是 over-engineering）。原仓储测试改为 DbContext 行为测试，新增 `Config_KeyUniqueIndex_RejectsDuplicates` 用例。
- [x] **Phase 6b 完成（侧边导航）**：`NavigationViewModel` 持有 5 个 `NavigationItem`（任务/分组/Tag/配置/日志）+ `SelectedItem`。MainWindow 改 DockPanel + ListBox 实现选择高亮（#1E3A5F 选中态）。`PlaceholderView` 单一占位 UC，所有 5 个 VM 通过 `DataTemplate` 在 `App.xaml` 映射到它。
- [x] **Phase 6c 完成（TasksView 读+删）**：DataGrid 绑定 `ObservableCollection<CollectorTask>`，列：ID/服务器/节点/协议/TCP地址/间隔/死区/创建时间。`LoadAsync` 用 `AsNoTracking()` 加载；`DeleteAsync` 用 `ExecuteDeleteAsync` 级联清除 Task+Groups+Tags。
- [x] **Phase 6d 完成（Task 新建/编辑对话框）**：`TaskEditorViewModel` 含输入验证（Server/Node 非空、Interval>0、Deviation 0-100、TcpAddress 含冒号）。`TaskEditorWindow` 模态对话框，OwnerWindow 关联主窗。`ITaskEditorDialog` 抽象 + `TaskEditorDialog` 实现，VM 通过 DI 调用，**不直接引用 Window 类型**（保持 MVVM 纯净）。
- [x] **Phase 6e 完成（GroupsView）**：扁平列表 + 任务下拉筛选（不做 drill-down 导航，避免维护导航栈；与侧栏一致性更好）。完整 CRUD：`GroupEditorViewModel` / `GroupEditorWindow` / `IGroupEditorDialog`。删除分组时级联清除其 Tag。
- [x] **Phase 6f 完成（TagsView）**：分组筛选 + LIKE 模糊搜索（回车触发），DataGrid 显示 Item / 数据类型 / 分组 / 任务。`OpcDataTypeOption` 静态列表（Boolean/Int16/Int32/Float32/Float64/String 等 14 项）替代裸 int。`TagEditorViewModel` 校验 Item 非空 + 必须选分组。新建时捕获 `DbUpdateException` 提示 Item 冲突（受 `udx_name` 唯一索引保护）。查询带 `Take(500)` 防大表雪崩。
- [x] **Phase 6g 完成（SettingsView）**：`ConfigEntry` 表 CRUD，Key 在编辑模式只读（避免改 unique key）。`IConfigEditorDialog` 走 DI。
- [x] **Phase 6h 完成（LogsView）**：定位 `logs/dc-yyyyMMdd.log`（或最近一份）+ 等宽字体 + 控制台风格（绿字黑底）+ 2 秒自动刷新（可关）+ "打开目录"按钮。读文件用 `FileShare.ReadWrite | FileShare.Delete` 避免与 Serilog 写冲突；取 `Take(500)` 末尾行控制内存。
- [x] **Phase 6i 完成（托盘 + 单实例）**：`Global\Dc.App.SingleInstance.<GUID>` 命名 Mutex（fresh GUID `b7c9e2a4-...`，与 Wails 原版隔离避免冲突；OSS 部署方可改自己的命名空间）。`H.NotifyIcon.Wpf` 提供 TaskbarIcon，左键单击与右键菜单都能恢复主窗口；最小化窗口自动隐藏到托盘。`Application.Current.Shutdown()` 关闭 Host + 释放 Mutex。
- [x] **Phase 6j 完成（Excel 导入/导出）**：`Dc.Infrastructure.Excel` 提供 `ITagExcelService` + `ClosedXmlTagExcelService`。Excel schema：sheet="Tags"，列 Item/DataType/GroupName/TaskId。**列顺序无关**——通过表头匹配定位列。导入按 GroupName 解析到 GroupId+TaskId；空 Item 行跳过；错误聚合提示。导出复用当前筛选 + 搜索条件（按需导出）。3 个测试通过（roundtrip 含 Unicode、空行跳过、列顺序无关）。`IFilePicker` 抽象（`WpfFilePicker` 实现）保持 VM 不引用 Win32 类型。累计 36/36 测试通过。
- [x] **TasksView 接入 TaskOrchestrator**：引入 `TaskRowViewModel` 包装 `CollectorTask + IsRunning + Status`（自动通知 Status 派生变化）。`StartCommand` 从 DB 读 Tag → 构 `TaskStartRequest` → `orchestrator.StartAsync`；`StopCommand` 调 `StopAsync`。删除前自动停止运行中的任务。Status 列绿字高亮"运行中"。
- [x] **Phase 4a 完成（UA 订阅器）**：`OpcUaSubscriber` 使用 `OPCFoundation.NetStandard.Opc.Ua.Client`（跨平台 nuget）。`ApplicationConfiguration` 程序内构造，自签证书自动生成到 `<basedir>/pki`，未受信证书自动接受（产线请收紧 `AutoAcceptUntrustedCertificates`）。`Subscription.PublishingInterval` 直接映射 `OpcConnectionOptions.SamplingInterval`。`MonitoredItem.Notification` → `Channel<TagValue>`，StatusCode 用 `StatusCode.IsBad/IsUncertain` 静态方法解析（实例属性不存在，与文章里的 DA 位运算不同）。Heartbeat 后台循环按 `HeartbeatInterval` 推送，依赖 `Session.Connected` 探活。`OpcUaSubscriberFactory` 注册到 DI，Tasks 视图 Start UA 类型任务现可达。**Linux 端编译通过；端到端跑通需 UA server（Prosys / Unified Automation / KEPServerEX 等模拟器）**。包升级：`Microsoft.Extensions.Logging.Abstractions` 与 `DependencyInjection.Abstractions` 升至 9.0.0（OPC Foundation 强制依赖）。
- [x] **LiveDataView 完成**：`TaskOrchestrator.TagValueReceived` 事件（`Action<string,TagValue>`），不分散到 channel 避免多消费者问题。`LiveDataRowViewModel` 持有最新值 + 累计更新次数；`LiveDataViewModel` 用 `Dictionary<key,row>` 索引（key = `taskId::item`），事件→Dispatcher.BeginInvoke 切回 UI 线程更新 ObservableCollection。质量码列绿/红着色（IsGood）。提供 暂停 ToggleButton + 清空按钮。已加入侧边导航第 4 项。
- [x] **Phase 4b 完成（UA Browser）**：`IOpcBrowserFactory` 抽象 + `OpcUaBrowser` 实现，`Session.Browse` 走 `HierarchicalReferences` 拉子节点（带 Object / Variable / View / Types）。共享 `OpcUaApplicationConfig.Build` 让 Subscriber 和 Browser 复用同一套自签证书目录。BrowseView：服务器 URI 输入 + 协议下拉 + 当前路径面包屑 + 子节点 DataGrid + "进入/返回上级/复制 NodeId" + 选中节点详情面板。双击进入下级。导航第 5 项。
- [x] **端到端集成测试**：`OrchestratorEndToEndTests` 用 FakeSubscriber + 真实 TcpPublisher + 真实 MessagePackSerializer + TcpListener 模拟 broker，验证 TagValue → 网络字节流 → 反序列化全链路。第二个测试验证 `TagValueReceived` 事件正确触发。累计 38/38 测试通过。
- [x] **README.md**：项目目标 / 已实现清单 / 架构关键决策 / 构建运行步骤 / 已知限制 / 详细 ROADMAP 链接。
- [x] **诊断视图完成**：`TaskOrchestrator.GetDiagnostics()` 返回 `TaskDiagnostics[]`（任务 ID / 启动时间 / 最后值时间 / 最后心跳时间 / 累计值数 / 发送错误数 / 重启次数 / 订阅 Tag 数）。Pipeline 用 `Interlocked.Increment` 安全计数。Watchdog 重启时 +1。`DiagnosticsViewModel` 用 `DispatcherTimer` 1 秒轮询，按 TaskId 索引行 VM，移除不再运行的任务。`DiagnosticsRowViewModel` 计算 1 秒滑动窗口的"速率/秒"。错误数 0 绿色 / >0 粉红；重启 0 绿 / >0 橙黄。导航第 6 项。`GetDiagnostics_ReportsValueCountAndLastValueTime` 测试。**39/39 测试通过**。
- [x] **TagEditor 浏览选取闭环完成**：`IBrowseDialog` 抽象 + `WpfBrowseDialog` 实现，模态嵌入 `BrowseView` + 选择/取消 footer，OK 时校验必须选 Variable 叶子节点（拒绝文件夹）。`TagEditorViewModel` 接 `IBrowseDialog` + `Func<string, CollectorTask?> taskLookup`，浏览时自动用当前 Group 所属 Task 的 Node 作为默认 server URI。`ITagEditorDialog` 多带一个 `taskLookup` 参数，`TagsViewModel` 维护 `_taskById` 字典并传入。Item 字段右侧加"浏览…"按钮。Linux 端构建通过；端到端需 UA 服务器。
- [x] **小补丁**：TasksView 加"全部启动 / 全部停止"按钮，批量启停时聚合错误提示。`TaskOrchestrator` 协议未注册错误信息友好化（DA/AE 提示需 Windows + COM SDK）。
- [x] **状态栏版本信息**：Dc.App.csproj 加 `<Version>` / `<Product>` / `<Company>` 元数据。`MainWindowViewModel.VersionInfo` 反射读取 Assembly 元数据，状态栏右下角显示 "Dc — OPC 数据采集 · v1.0.0"。
- [x] **配置备份/恢复（JSON）**：`Dc.Infrastructure.Backup` 提供 `BackupBundle` + `IConfigBackupService` + `JsonConfigBackupService`。导出全部 Tasks/Groups/Tags/Configs（带 schemaVersion + appVersion + exportedAt）为缩进 JSON。导入两种模式：**Merge**（按 ID 去重只插入新条目）与 **Replace**（先清空所有表再插入，破坏性）。Entity 加 `[JsonIgnore]` 屏蔽 nav 属性。SettingsView 加"导出全部 / 导入备份"按钮 + 模式选择对话框。**44/44 测试通过**（5 个 backup 测试：roundtrip / merge 去重 / replace 清空 / merge 部分新增 / 兼容性 schemaVersion）。
- [x] **Tag 热加/热卸**：`TagsViewModel` 新建/编辑/删除/Excel 导入时，若目标 Task 正在运行，自动调 `TaskOrchestrator.AddTagsAsync` / `RemoveTagsAsync`，订阅器无需停启就能感知新 Tag。`GroupsViewModel` 删除分组时级联卸载该分组的 Tag。Edit 时 Item/TaskId 变化先 Remove 再 Add。
- [x] **删除确认弹窗**：Tasks/Groups/Tags/Configs 删除都加 Yes/No MessageBox 二次确认，避免误操作。
- [x] **About 对话框**：tray menu 加"关于…"项，`AboutWindow` + `AboutViewModel` 反射读取 AssemblyProduct / Version / Company + 运行时版本 + 构建时间。
- [x] **appsettings.json 配置**：`Host.CreateDefaultBuilder` 自动加载。`Database:Path` 覆盖 sqlite.db 路径；`Orchestrator:WatchdogIntervalSeconds` / `HeartbeatTimeoutSeconds` 覆盖编排器选项。csproj 配置 `CopyToOutputDirectory=PreserveNewest`。让产品部署时可外部化配置而不重编译。

## Linux 端工作收官

到此为止，**Linux 端可做的部分全部完成**。剩余工作均需 Windows：

| 待 Windows 端 | 内容 |
|---|---|
| Phase 3b | ✅ **完成（2026-05-16）** — `wpf/src/Dc.Opc.Da/OpcDaSubscriber.cs` + Factory，submodule `wpf/vendor/ClassicClient`，Linux 上 `dotnet build -p:Platform=x64 -p:CustomTestTarget=net8.0-windows` 通过 |
| Phase 4 AE | AE 订阅器（同 submodule，AE namespace） |
| 端到端验证 | 真实 sqlite.db 数据 + 真实 OPC UA server + 真实 broker 接收 MessagePack |
| Phase 7 | Inno Setup / WiX 安装器；`dotnet publish` self-contained |
| UI 视觉验证 | WPF 窗口实际渲染（Linux 上无法验证） |

**全 ROADMAP 验证就绪条件已达成**：44/44 测试通过，0 警告，0 错误，全部 Linux-doable 阶段完成。Windows 端可以基于此分支直接开工 DA/AE，所有抽象、UI、编排、消息、诊断已就位。

## Phase 3b / 4 — Windows 端开工指南（DA / AE）

### 选型决策（2026-05-15）

**SDK：Technosoftware DaAeHdaClient（社区/GPL 版）**

| 维度 | 选择理由 |
|---|---|
| 许可证 | 本项目以 GPL-3.0 开源，与 Technosoftware GPL 版本兼容 |
| 跨主机 DCOM | Technosoftware 走 URL 风格连接（底层 TCP），DCOM 防火墙配置简单，**最关键的决定因素** |
| 部署依赖 | 纯托管 NuGet 包，不需要 `gbda_aut.dll` + `regsvr32`，不需要本机注册表 |
| .NET 8 | 原生支持 |
| 文档 | `opcda/article.md` 全程基于该 SDK，踩坑清单可直接复用 |

被淘汰的候选：
- **TitaniumAS.Opc.Client**：仍底层 COM Interop，DCOM 坑要全踩，2020 后停更
- **自撸 OPCAutomation Interop**：跨主机 DCOM 类型映射 / 多线程崩溃 / 防火墙逐个踩

### Windows 端开工步骤

1. `dotnet add wpf/src/Dc.Opc.Da package Technosoftware.DaAeHdaClient`，确认版本后改到 `Directory.Packages.props` 集中固化
2. 实现 `OpcDaSubscriber : IOpcSubscriber`（参照 `OpcUaSubscriber` 的结构 + `opcda/article.md` 第 84-110 行的核心实现描述）
   - 所有 COM 调用包在 `_comLock` 内（文章第 88 行强调过）
   - `_subscribedTags` 字典做幂等（文章踩坑 1）
   - Quality 用 `(quality & 0xC0) == 0xC0` 解析，不用 `> 0`（文章踩坑反复强调）
   - `DataChange` 事件 → `Channel<TagValue>`，写端用 `TryWrite` 非阻塞（文章 100-110 行）
3. 实现 `OpcDaSubscriberFactory : IOpcSubscriberFactory`，`Protocol => OpcProtocol.Da`
4. 在 `ServiceRegistration.AddDcApp` 注册 `IOpcSubscriberFactory` 多实例（与 UA 工厂并列）
5. 实现 `OpcDaBrowser : IOpcBrowser` + Factory，BrowseView 已经支持 Protocol 切换，只需在 `BrowseViewModel.AvailableProtocols` 里把 `Da` 加进去
6. AE 同理（Technosoftware 同一包内）

### 验证清单（Windows 端）

- [ ] Matrikon OPC Simulation Server 本地连接 + 订阅 `Random.Int1` / `Random.Real8` 持续收数
- [ ] 跨主机 DCOM 连接（远程 Win10 + 防火墙开 135 + Technosoftware URL）
- [ ] Quality 位运算解析正确（Good/Uncertain/Bad 三态）
- [ ] 拔网线 / 杀 Server 进程 → watchdog 在 HeartbeatTimeout 内重连
- [ ] 添加 / 删除 Tag 时不重启订阅（幂等 AddItem）
- [ ] 大量 Tag (≥1000) 批量读取性能（参考 article.md 提到的 8200ms → 580ms 的 14x 优化）

## Phase 8 — 集成测试 ✓ (2026-05-18)

- 18 测试用例覆盖 OPC 协议层 + TcpPublisher + 关键弹性
- 跨平台 9 通过（INF-1..5 + UA-1..3）/ Windows-only 9 待验证（DA-1..4 + AE-1..3 + RES-1..2，前置: demo server + OPCEnum）
- 两测试项目按 TFM 切分（Dc.Integration.Tests / Dc.Integration.Tests.Com）
- WindowsComFactAttribute 自动 skip 缺失环境
