using Dc.Infrastructure.Orchestration;
using Microsoft.Extensions.Configuration;

namespace Dc.Cli;

// `Dc.Cli --healthcheck`：探活已运行的采集进程的诊断端点。
// Docker HEALTHCHECK 用它，免去镜像里装 curl/wget。
internal static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            // 端点关闭时无可探测，视为健康（不让探针误杀只跑采集、不开 HTTP 的部署）。
            if (!config.GetValue("Diagnostics:Http:Enabled", true))
                return 0;

            var prefix = config.GetValue<string>("Diagnostics:Http:Prefix") ?? "http://+:9090/";
            var port = MetricsHttpServer.ParsePort(prefix);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync($"http://localhost:{port}/healthz").ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return 0;

            await Console.Error.WriteLineAsync($"healthcheck: /healthz 返回 {(int)resp.StatusCode}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"healthcheck 失败: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
