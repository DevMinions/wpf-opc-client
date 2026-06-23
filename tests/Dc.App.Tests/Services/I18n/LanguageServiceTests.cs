using System.Globalization;
using Dc.App.Services.I18n;
using Microsoft.Extensions.Configuration;

namespace Dc.App.Tests.Services.I18n;

public class LanguageServiceTests
{
    private static IConfiguration Config(string? value)
    {
        var dict = value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Language"] = value };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static Mock<ILanguageApplier> Applier(string systemName = "en")
    {
        var a = new Mock<ILanguageApplier>();
        a.Setup(x => x.DetectSystemCulture()).Returns(new CultureInfo(systemName));
        return a;
    }

    [Fact]
    public void Initial_DefaultsToSystem_WhenConfigMissing()
    {
        var applier = Applier("en");
        var svc = new LanguageService(Config(null), applier.Object);
        svc.Initialize();
        Assert.Equal(AppLanguage.System, svc.Current);
        applier.Verify(a => a.Apply(It.Is<CultureInfo>(c => c.Name == "en")), Times.Once);
    }

    [Theory]
    [InlineData("ChineseSimplified", AppLanguage.ChineseSimplified)]
    [InlineData("English", AppLanguage.English)]
    [InlineData("System", AppLanguage.System)]
    public void Initial_ReadsConfiguredValue(string configured, AppLanguage expected)
    {
        var svc = new LanguageService(Config(configured), Applier().Object);
        svc.Initialize();
        Assert.Equal(expected, svc.Current);
    }

    [Fact]
    public void Apply_English_AppliesEnCulture()
    {
        var applier = Applier();
        var svc = new LanguageService(Config(null), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();
        svc.Apply(AppLanguage.English);
        applier.Verify(a => a.Apply(It.Is<CultureInfo>(c => c.Name == "en")), Times.Once);
        Assert.Equal(AppLanguage.English, svc.Current);
    }

    [Fact]
    public void Apply_ChineseSimplified_AppliesZhCulture()
    {
        var applier = Applier();
        var svc = new LanguageService(Config(null), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();
        svc.Apply(AppLanguage.ChineseSimplified);
        applier.Verify(a => a.Apply(It.Is<CultureInfo>(c => c.Name == "zh-CN")), Times.Once);
    }

    [Fact]
    public void Apply_System_ResolvesViaApplier()
    {
        var applier = Applier("zh-CN");
        var svc = new LanguageService(Config("English"), applier.Object);
        svc.Initialize();
        applier.Invocations.Clear();
        svc.Apply(AppLanguage.System);
        applier.Verify(a => a.DetectSystemCulture(), Times.Once);
        applier.Verify(a => a.Apply(It.Is<CultureInfo>(c => c.Name == "zh-CN")), Times.Once);
    }

    [Fact]
    public void Apply_RaisesLanguageChanged()
    {
        var svc = new LanguageService(Config(null), Applier().Object);
        svc.Initialize();
        AppLanguage? got = null;
        svc.LanguageChanged += l => got = l;
        svc.Apply(AppLanguage.English);
        Assert.Equal(AppLanguage.English, got);
    }

    [Fact]
    public void Initialize_DoesNotRaiseOrPersist()
    {
        var writer = new Mock<ILanguagePreferenceWriter>();
        var svc = new LanguageService(Config("English"), Applier().Object, writer.Object);
        bool fired = false;
        svc.LanguageChanged += _ => fired = true;
        svc.Initialize();
        Assert.False(fired);
        writer.Verify(w => w.Write(It.IsAny<AppLanguage>()), Times.Never);
    }

    [Fact]
    public void Apply_PersistsViaWriter()
    {
        var writer = new Mock<ILanguagePreferenceWriter>();
        var svc = new LanguageService(Config(null), Applier().Object, writer.Object);
        svc.Initialize();
        svc.Apply(AppLanguage.English);
        writer.Verify(w => w.Write(AppLanguage.English), Times.Once);
    }
}
