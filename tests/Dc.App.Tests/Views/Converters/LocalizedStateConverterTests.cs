using System.Globalization;
using Dc.App.Services.I18n;
using Dc.App.Views.Converters;

namespace Dc.App.Tests.Views.Converters;

[Collection("I18nCulture")]
public class LocalizedStateConverterTests
{
    private readonly LocalizedStateConverter _c = new();

    private object Conv(object? state, string prefix)
        => _c.Convert(new[] { state, (object?)"culture" }, typeof(string), prefix, CultureInfo.InvariantCulture);

    [Fact]
    public void EnumState_LooksUpPrefixedKey_FollowsCulture()
    {
        // Diagnostics_StateFaulted: zh=故障 / en=Faulted。第二个输入(culture 占位)内容被忽略。
        LocalizationManager.Instance.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("故障", Conv("Faulted", "Diagnostics_State"));
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        Assert.Equal("Faulted", Conv("Faulted", "Diagnostics_State"));
    }

    [Fact]
    public void WorkspaceStatus_PrefixMatchesExistingKeys()
    {
        LocalizationManager.Instance.SetCulture(new CultureInfo("en"));
        Assert.Equal("Running", Conv("Running", "Workspace_Status"));
        Assert.Equal("Alert", Conv("Alert", "Workspace_Status"));
        Assert.Equal("Stopped", Conv("Stopped", "Workspace_Status"));
    }

    [Fact]
    public void NullOrEmptyState_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Conv(null, "Diagnostics_State"));
        Assert.Equal(string.Empty, Conv("", "Diagnostics_State"));
    }
}
