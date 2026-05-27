# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

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
