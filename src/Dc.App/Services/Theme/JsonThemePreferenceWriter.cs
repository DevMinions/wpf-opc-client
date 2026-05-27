using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dc.App.Services.Theme;

public sealed class JsonThemePreferenceWriter : IThemePreferenceWriter
{
    private readonly string _path;

    public JsonThemePreferenceWriter(string path) => _path = path;

    public void Write(AppTheme theme)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var text = File.ReadAllText(_path);
            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (JsonException) { return; }
            if (root is not JsonObject obj) return;

            obj["Theme"] = theme.ToString();
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_path, obj.ToJsonString(options));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
