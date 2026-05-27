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
