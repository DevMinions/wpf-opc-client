using System.IO;
using System.Text.Json;
using Dc.App.Services.Theme;

namespace Dc.App.Tests.Services.Theme;

public class JsonThemePreferenceWriterTests
{
    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-theme-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Write_UpdatesThemeKey_PreservesOtherKeys()
    {
        var path = TempFile("""
        {
          "Database": { "Path": "sqlite.db" },
          "Theme": "System",
          "Serilog": { "MinimumLevel": "Information" }
        }
        """);
        try
        {
            new JsonThemePreferenceWriter(path).Write(AppTheme.Dark);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal("Dark", root.GetProperty("Theme").GetString());
            Assert.Equal("sqlite.db", root.GetProperty("Database").GetProperty("Path").GetString());
            Assert.Equal("Information", root.GetProperty("Serilog").GetProperty("MinimumLevel").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_AddsThemeKey_WhenMissing()
    {
        var path = TempFile("""{ "Database": { "Path": "sqlite.db" } }""");
        try
        {
            new JsonThemePreferenceWriter(path).Write(AppTheme.Light);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Light", doc.RootElement.GetProperty("Theme").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-missing-{Guid.NewGuid():N}.json");
        var ex = Record.Exception(() => new JsonThemePreferenceWriter(path).Write(AppTheme.Dark));
        Assert.Null(ex);
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Write_MalformedJson_DoesNotThrow()
    {
        var path = TempFile("{ this is not json ");
        try
        {
            var ex = Record.Exception(() => new JsonThemePreferenceWriter(path).Write(AppTheme.Dark));
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }
}
