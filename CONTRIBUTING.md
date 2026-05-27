# 贡献指南

感谢关注本项目。

## 开发环境

- .NET 8 SDK
- Windows 10/11（运行 WPF / OPC DA·AE 需要；非 GUI 项目与跨平台测试可在 Linux/macOS 构建）
- OPC DA/AE 的 Technosoftware 客户端源码已内置于 `vendor/ClassicClient/`（GPL-3.0），无需额外拉取

## 提交前

```bash
dotnet build Dc.sln -p:Platform=x64 -p:CustomTestTarget=net8.0-windows
dotnet test tests/Dc.Infrastructure.Tests        # 须全绿
dotnet format                                    # 统一格式
```

OPC DA/AE 相关改动请在 Windows + 真实/示例 COM server 上验证（见 README「运行」章）。

## 约定

- 架构分层严格单向：UI → Infrastructure → Abstractions/Domain；OPC 协议实现互相独立。
- ULID 在 service/VM 层生成，不交给 DB。
- 不写 Repository 包 EF Core；DbContext 经 `IDbContextFactory` 按操作创建。
- OPC 质量码用位运算 `(q & 0xC0) == 0xC0` 判 GOOD。
- wire 协议变更需同步常量 / 收发端 / 测试 / `docs/wire-format.md`。
- Commit message 用 gitmoji + 简述。

## 许可

提交即表示同意你的贡献以 **GPL-3.0** 授权。
