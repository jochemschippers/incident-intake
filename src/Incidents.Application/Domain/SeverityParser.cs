using System.Text.RegularExpressions;

namespace Incidents.Application.Domain;

// "<@U0BOT> sev1 Payments API returns 500" -> (Sev1, "Payments API returns 500")
public static partial class SeverityParser
{
    public const Severity Default = Severity.Sev3;

    [GeneratedRegex(@"<@[A-Z0-9]+>")] private static partial Regex Mention();
    [GeneratedRegex(@"\bsev\s*([1-4])\b", RegexOptions.IgnoreCase)] private static partial Regex Sev();

    public static (Severity Severity, string Title) Parse(string text)
    {
        var clean = Mention().Replace(text ?? "", "");
        var m = Sev().Match(clean);
        var sev = m.Success ? (Severity)int.Parse(m.Groups[1].Value) : Default;
        if (m.Success) clean = clean.Remove(m.Index, m.Length);
        return (sev, Regex.Replace(clean, @"\s+", " ").Trim());
    }
}
