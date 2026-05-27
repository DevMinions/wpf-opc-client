# 无头 OPC 采集器 Dc.Cli 镜像（纯 net8.0，跨平台；仅 UA，DA/AE 走 COM 需 Windows）。
# 多阶段：SDK 阶段框架依赖发布 → runtime 阶段运行（镜像更小、IL 与架构无关，便于多架构）。
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG VERSION=0.0.0
WORKDIR /src

# 仅拷 Dc.Cli 及其依赖工程 + 中央包管理配置（不含 WPF / vendor COM）
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Dc.Domain/ src/Dc.Domain/
COPY src/Dc.Opc.Abstractions/ src/Dc.Opc.Abstractions/
COPY src/Dc.Opc.Ua/ src/Dc.Opc.Ua/
COPY src/Dc.Infrastructure/ src/Dc.Infrastructure/
COPY src/Dc.Cli/ src/Dc.Cli/

RUN dotnet publish src/Dc.Cli/Dc.Cli.csproj -c Release -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# 数据库与任务配置走挂载卷持久化；用环境变量覆盖 appsettings 的 Database:Path。
# 运行示例：docker run -v /宿主/data:/data ghcr.io/<owner>/dc-cli:<ver>
ENV Database__Path=/data/sqlite.db
VOLUME ["/data"]

# 日志走 stdout（Serilog Console），docker logs 可见
ENTRYPOINT ["dotnet", "Dc.Cli.dll"]
