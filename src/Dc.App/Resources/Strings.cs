using System.Resources;

namespace Dc.App.Resources;

// 手写资源访问器：不用 VS 设计器代码生成(headless/CI 的 dotnet build 不跑设计器)。
// 统一按字符串 key 访问;key 存在性由 StringsParityTests 保证。
// base name 必须等于 resx 的清单名: {RootNamespace}.{folder}.{file} = Dc.App.Resources.Strings
internal static class Strings
{
    public static ResourceManager ResourceManager { get; } =
        new("Dc.App.Resources.Strings", typeof(Strings).Assembly);
}
