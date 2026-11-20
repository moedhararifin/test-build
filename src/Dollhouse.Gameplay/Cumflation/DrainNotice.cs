using System.Globalization;

namespace Dollhouse.Gameplay;

// Keep the amount independent of cleared attributes and formatted UI text.
internal sealed record DrainNotice(string Role, int Womb, int Gi)
{
    internal long Total => (long)Math.Max(0, Womb) + Math.Max(0, Gi);
    internal string Volume => Total >= 1000
        ? (Total / 1000m).ToString("0.###", CultureInfo.InvariantCulture) + "L"
        : Total.ToString(CultureInfo.InvariantCulture) + "mL";

    internal string? Format(string template, string character)
    {
        // This recovered localized template uses two positional FText arguments.
        // Refuse unsupported patterns instead of silently displaying an incomplete alert.
        if (!template.Contains("{0}") || !template.Contains("{1}")) return null;
        string remainder = template.Replace("{0}", "").Replace("{1}", "");
        if (remainder.Contains('{') || remainder.Contains('}') || template.Contains('`')) return null;
        return template.Replace("{0}", character).Replace("{1}", Volume);
    }
}
