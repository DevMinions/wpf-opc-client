using System.IO;
using System.Text.Json;
using Dc.App.Services.I18n;

namespace Dc.App.Tests.Services.I18n;

public class JsonLanguagePreferenceWriterTests
{
    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-lang-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Write_UpdatesLanguageKey_PreservesOtherKeys()
    {
        var path = TempFile("""
        { "Database": { "Path": "sqlite.db" }, "Language": "System", "Theme": "Dark" }
        """);
        try
        {
            new JsonLanguagePreferenceWriter(path).Write(AppLanguage.English);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal("English", root.GetProperty("Language").GetString());
            Assert.Equal("sqlite.db", root.GetProperty("Database").GetProperty("Path").GetString());
            Assert.Equal("Dark", root.GetProperty("Theme").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_AddsLanguageKey_WhenMissing()
    {
        var path = TempFile("""{ "Database": { "Path": "sqlite.db" } }""");
        try
        {
            new JsonLanguagePreferenceWriter(path).Write(AppLanguage.ChineseSimplified);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("ChineseSimplified", doc.RootElement.GetProperty("Language").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dc-missing-{Guid.NewGuid():N}.json");
        var ex = Record.Exception(() => new JsonLanguagePreferenceWriter(path).Write(AppLanguage.English));
        Assert.Null(ex);
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void Write_MalformedJson_DoesNotThrow()
    {
        var path = TempFile("{ not json ");
        try
        {
            var ex = Record.Exception(() => new JsonLanguagePreferenceWriter(path).Write(AppLanguage.English));
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }
}
