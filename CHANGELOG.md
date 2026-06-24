# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## v1.2.0 — 双语界面 + 浏览/采集体验打磨

### 功能
- **双语 UI（简体中文 / English，运行时切换）**：设置页可选「简体中文 / English / 跟随系统」，免重启即时生效并持久化（响应 issue #2）。
- 浏览节点「加为 Tag」**自动按节点真实数据类型回填**（单选与批量一致），「默认」退为兜底。
- 运行日志与诊断指标（`/metrics`、dotnet-counters）统一英文，便于检索 / 排错。

### 修复 / 改进
- 浏览节点：同步任务的「安全连接」设置（无安全 UA server 浏览不再报安全错）；下钻后「返回」可用、可逐级回退；选中行名称 / 图标不再隐形；窄面板下列宽自适应、不再重叠。
- 任务 / Tag 编辑：UA 任务不再误用 classic OPC 字段校验（修保存按钮永久禁用）；任务「名称」编辑后正确持久化；弹窗保存 / 确定按钮禁用态清晰可见。
- 运行日志列表启用真正的虚拟化，日志量大时不再卡顿。

## v1.1.0 — 无头采集器 + 跨平台/容器化 + 诊断可观测

### 功能
- **无头 / 服务模式 `Dc.Cli`**：采集引擎脱离 WPF，可 Linux / Docker 部署（纯 net8.0）。
- 发布产物新增 **Linux tarball（x64 / arm64）+ 多架构 Docker 镜像（GHCR）**。
- **诊断可观测**：Metrics 仪表 + 周期结构化日志（IHostedService）。
- **UA 断线自动重连**（KeepAlive → SessionReconnectHandler）。
- 浏览节点读取**真实值**（UA ReadValue），移除占位 mock。

### 修复
- 看门狗 / 心跳测试独占执行，修 CI 在 CPU 争用下的 flaky。

## v1.0.0 — 首个开源版本

通用 OPC 数据采集客户端（.NET 8 + WPF）首个开源发布。

### 功能
- OPC **UA / DA / AE** 订阅 + 浏览（UA 跨平台；DA/AE 走 Technosoftware COM，需 Windows）
- 采集任务编排：启停 / 热增删 Tag / 心跳监控 / 超时自动重启
- TCP 发布（MessagePack/JSON，wire v1.1：magic + format-id）+ 冷却重连 + 可选离线队列
- 实时数据（三态质量码）/ 诊断面板（sparkline 趋势）/ 运行日志 / 节点浏览 + IP 扫描
- Fluent UI（亮/暗主题跟随系统）、系统托盘、单实例锁、Inno Setup 安装包

### 许可
GPL-3.0（依赖 OPC UA / Technosoftware DA·AE 库的 GPL 版本，整体以 GPL-3.0 分发）。

> 本版的 UI/行为改动建议在 Windows + 真实 OPC server 上完整验证，见
> [`docs/windows-verification-checklist.md`](docs/windows-verification-checklist.md)。
