using System.Globalization;
using Dc.App.Services.I18n;

namespace Dc.App.Tests.Services.I18n;

public class LocalizationManagerTests
{
    [Fact]
    public void Indexer_ReturnsValueForCurrentCulture()
    {
        var m = new LocalizationManager();
        m.SetCulture(new CultureInfo("zh-CN"));
        Assert.Equal("保存", m["Common_Save"]);
        m.SetCulture(new CultureInfo("en"));
        Assert.Equal("Save", m["Common_Save"]);
    }

    [Fact]
    public void Indexer_UnknownKey_ReturnsKeyItself()
    {
        var m = new LocalizationManager();
        m.SetCulture(new CultureInfo("en"));
        Assert.Equal("__nope__", m["__nope__"]);
    }

    [Fact]
    public void SetCulture_RaisesIndexerPropertyChanged()
    {
        var m = new LocalizationManager();
        string? changed = null;
        m.PropertyChanged += (_, e) => changed = e.PropertyName;
        m.SetCulture(new CultureInfo("en"));
        Assert.Equal("Item[]", changed); // Binding.IndexerName
    }
}
