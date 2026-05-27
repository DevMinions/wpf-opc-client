using System.Runtime.Versioning;
using Microsoft.Win32;
using Xunit;

namespace Dc.Integration.Tests.Com.Fixtures;

// 自定义 [Fact] 派生类：根据 OS / OPCEnum 二进制 / demo server ProgID 注册自动 skip。
// 失败原因写入 Skip 属性，xunit runner 显示为 skipped 而非 failed。
[SupportedOSPlatform("windows")]
public sealed class WindowsComFactAttribute : FactAttribute
{
    // 默认探测 SampleCompany.DaSample；AE 测试可显式传 "SampleCompany.AeSample"
    public WindowsComFactAttribute(string requiredProgId = "SampleCompany.DaSample")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "OPC DA/AE 仅 Windows";
            return;
        }
        if (!File.Exists(@"C:\Windows\SysWOW64\OpcEnum.exe"))
        {
            Skip = "OPCEnum 未安装（装 OPC Core Components Redistributable）";
            return;
        }
        if (Registry.ClassesRoot.OpenSubKey(requiredProgId) is null)
        {
            Skip = $"{requiredProgId} 未 /regserver — cd vendor\\ClassicClient\\x86\\DemoServer 跑 OpcDaAeServer.exe /regserver";
            return;
        }
    }
}
