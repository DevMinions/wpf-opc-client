namespace Dc.App.Services.I18n;

public interface ILanguagePreferenceWriter
{
    /// <summary>把语言选择写入持久化(appsettings.json 的 Language 键)。失败不抛。</summary>
    void Write(AppLanguage language);
}
