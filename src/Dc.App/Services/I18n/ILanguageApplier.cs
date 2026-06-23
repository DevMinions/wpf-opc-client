using System.Globalization;

namespace Dc.App.Services.I18n;

public interface ILanguageApplier
{
    void Apply(CultureInfo effective);   // 设 CurrentUICulture + 通知 manager
    CultureInfo DetectSystemCulture();   // OS UI culture → 受支持 culture
}
