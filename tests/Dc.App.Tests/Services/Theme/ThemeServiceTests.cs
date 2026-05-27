using Dc.App.Services.Theme;
using Microsoft.Extensions.Configuration;

namespace Dc.App.Tests.Services.Theme;

public class ThemeServiceTests
{
    private static IConfiguration ConfigWithTheme(string? value)
    {
        var dict = value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Theme"] = value };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Initial_DefaultsToSystem_WhenConfigMissing()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);

        var svc = new ThemeService(ConfigWithTheme(null), applier.Object);
        svc.Initialize();

        Assert.Equal(AppTheme.System, svc.Current);
        applier.Verify(a => a.Apply(AppTheme.Light), Times.Once);
    }

    [Theory]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("Dark",  AppTheme.Dark)]
    [InlineData("System", AppTheme.System)]
    public void Initial_ReadsConfiguredValue(string configured, AppTheme expected)
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Dark);

        var svc = new ThemeService(ConfigWithTheme(configured), applier.Object);
        svc.Initialize();

        Assert.Equal(expected, svc.Current);
    }

    [Fact]
    public void Apply_System_ResolvesViaApplier()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Dark);

        var svc = new ThemeService(ConfigWithTheme("Light"), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();

        svc.Apply(AppTheme.System);

        applier.Verify(a => a.Apply(AppTheme.Dark), Times.Once);
        applier.Verify(a => a.DetectSystemTheme(), Times.Once);
        Assert.Equal(AppTheme.System, svc.Current);
    }

    [Fact]
    public void Apply_Light_CallsApplierWithLight()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);

        var svc = new ThemeService(ConfigWithTheme(null), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();

        svc.Apply(AppTheme.Light);

        applier.Verify(a => a.Apply(AppTheme.Light), Times.Once);
        Assert.Equal(AppTheme.Light, svc.Current);
    }

    [Fact]
    public void Apply_RaisesThemeChangedEvent()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var svc = new ThemeService(ConfigWithTheme(null), applier.Object);
        svc.Initialize();

        AppTheme? received = null;
        svc.ThemeChanged += t => received = t;

        svc.Apply(AppTheme.Dark);

        Assert.Equal(AppTheme.Dark, received);
    }

    [Fact]
    public void Initialize_DoesNotRaiseThemeChangedEvent()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var svc = new ThemeService(ConfigWithTheme("Dark"), applier.Object);

        bool fired = false;
        svc.ThemeChanged += _ => fired = true;
        svc.Initialize();

        Assert.False(fired);
    }

    [Fact]
    public void Apply_PersistsViaWriter()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var writer = new Mock<IThemePreferenceWriter>();
        var svc = new ThemeService(ConfigWithTheme(null), applier.Object, writer.Object);
        svc.Initialize();

        svc.Apply(AppTheme.Dark);

        writer.Verify(w => w.Write(AppTheme.Dark), Times.Once);
    }

    [Fact]
    public void Initialize_DoesNotPersist()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var writer = new Mock<IThemePreferenceWriter>();
        var svc = new ThemeService(ConfigWithTheme("Dark"), applier.Object, writer.Object);

        svc.Initialize();

        writer.Verify(w => w.Write(It.IsAny<AppTheme>()), Times.Never);
    }

    private sealed class FakeWatcher : ISystemThemeWatcher
    {
        public event Action? SystemThemeChanged;
        public bool StartCalled;
        public void Start() => StartCalled = true;
        public void Raise() => SystemThemeChanged?.Invoke();
    }

    [Fact]
    public void Initialize_StartsSystemWatcher()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Light);
        var watcher = new FakeWatcher();
        var svc = new ThemeService(ConfigWithTheme("System"), applier.Object, null, watcher);
        svc.Initialize();
        Assert.True(watcher.StartCalled);
    }

    [Fact]
    public void SystemThemeChange_WhenFollowingSystem_Reapplies()
    {
        var applier = new Mock<IThemeApplier>();
        applier.SetupSequence(a => a.DetectSystemTheme())
               .Returns(AppTheme.Light)
               .Returns(AppTheme.Dark);
        var watcher = new FakeWatcher();
        var svc = new ThemeService(ConfigWithTheme("System"), applier.Object, null, watcher);
        svc.Initialize();
        applier.Invocations.Clear();

        watcher.Raise();

        applier.Verify(a => a.Apply(AppTheme.Dark), Times.Once);
    }

    [Fact]
    public void SystemThemeChange_WhenFixedTheme_DoesNotReapply()
    {
        var applier = new Mock<IThemeApplier>();
        applier.Setup(a => a.DetectSystemTheme()).Returns(AppTheme.Dark);
        var watcher = new FakeWatcher();
        var svc = new ThemeService(ConfigWithTheme("Light"), applier.Object, null, watcher);
        svc.Initialize();
        applier.Invocations.Clear();

        watcher.Raise();

        applier.Verify(a => a.Apply(It.IsAny<AppTheme>()), Times.Never);
    }
}
