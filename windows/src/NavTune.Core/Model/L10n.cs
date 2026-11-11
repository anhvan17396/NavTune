using System.Globalization;
using Microsoft.Win32;

namespace NavTune.Model;

public enum AppLanguage
{
    System,
    Vietnamese,
    English,
}

/// <summary>Current UI language. Every piece of text in NavTune is a Vietnamese / English pair.</summary>
public static class L10n
{
    private static bool isVietnamese = Resolve(AppLanguage.System);

    /// <summary>Raised on the thread that changed the language, after the change.</summary>
    public static event Action? Changed;

    public static bool IsVietnamese => isVietnamese;

    public static void Apply(AppLanguage language)
    {
        bool vi = Resolve(language);
        if (vi == isVietnamese) return;
        isVietnamese = vi;
        Changed?.Invoke();
    }

    public static bool Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Vietnamese => true,
        AppLanguage.English => false,
        _ => SystemPrefersVietnamese(),
    };

    /// <summary>Vietnamese when Windows shows Vietnamese, lists it first, or formats dates the Vietnamese way.</summary>
    private static bool SystemPrefersVietnamese()
    {
        static bool Vi(string? name) => name is not null && name.StartsWith("vi", StringComparison.OrdinalIgnoreCase);
        if (Vi(CultureInfo.CurrentUICulture.Name) || Vi(CultureInfo.CurrentCulture.Name)) return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
            if (key?.GetValue("Languages") is string[] { Length: > 0 } languages) return Vi(languages[0]);
        }
        catch
        {
            // Registry not readable: fall back to English.
        }
        return false;
    }

    public static string Code(this AppLanguage language) => language switch
    {
        AppLanguage.Vietnamese => "vi",
        AppLanguage.English => "en",
        _ => "system",
    };

    public static AppLanguage ParseLanguage(string? code) => code switch
    {
        "vi" => AppLanguage.Vietnamese,
        "en" => AppLanguage.English,
        _ => AppLanguage.System,
    };

    public static string Title(this AppLanguage language) => language switch
    {
        AppLanguage.Vietnamese => "Tiếng Việt",
        AppLanguage.English => "English",
        _ => Tr("Theo hệ thống", "System"),
    };

    /// <summary>Picks the Vietnamese or English text for the current app language.</summary>
    public static string Tr(string vi, string en) => isVietnamese ? vi : en;
}
