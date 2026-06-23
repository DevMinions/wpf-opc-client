namespace Dc.App.Services.I18n;

public interface ILanguageService
{
    AppLanguage Current { get; }
    event Action<AppLanguage>? LanguageChanged;

    /// <summary>启动时调用一次:读 IConfiguration["Language"] → Apply 一次(不发事件、不写盘)。</summary>
    void Initialize();

    /// <summary>用户切换语言。System 会被解析为 effective culture 再下发。</summary>
    void Apply(AppLanguage language);
}
