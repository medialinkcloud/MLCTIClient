using System.Text.RegularExpressions;

namespace MedialinkCTIClient;

/// <summary>
/// Only telephone URI data, never arbitrary commands/URLs. This parser is
/// deliberately stricter than PhoneNumbers.Dialable, which discards letters.
/// No call can be placed by parsing: a separate explicit user confirmation is required.
/// </summary>
internal static class ExternalDialUri
{
    private static readonly Regex SafeDialCharacters = new(@"^\+?[0-9 .()\-]{1,80}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool HasSupportedScheme(string? value) =>
        value is not null && (value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("callto:", StringComparison.OrdinalIgnoreCase));

    public static bool TryParse(string? uri, out string number)
    {
        number = "";
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 320 ||
            uri.Any(char.IsControl)) return false;
        string raw;
        if (uri.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            raw = uri.Substring(4);
        else if (uri.StartsWith("callto:", StringComparison.OrdinalIgnoreCase))
        {
            raw = uri.Substring(7);
            if (raw.StartsWith("//", StringComparison.Ordinal)) raw = raw.Substring(2);
        }
        else return false;

        // URI encoding from HTML: %2B for a leading plus, %20 for spaces.
        // Reject malformed percent sequences rather than allowing ambiguous data.
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '%') continue;
            if (i + 2 >= raw.Length || !Uri.IsHexDigit(raw[i + 1]) || !Uri.IsHexDigit(raw[i + 2])) return false;
            i += 2;
        }
        try { raw = Uri.UnescapeDataString(raw); }
        catch (UriFormatException) { return false; }
        return TryValidateNumber(raw, out number);
    }

    public static bool TryValidateNumber(string? supplied, out string number)
    {
        number = "";
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 80 ||
            supplied.Any(char.IsControl) || !SafeDialCharacters.IsMatch(supplied)) return false;
        number = PhoneNumbers.Dialable(supplied);
        return PhoneNumbers.ValidDialString(number) &&
            number.Count(LegacyCompat.IsAsciiDigit) >= 1 &&
            number.Count(LegacyCompat.IsAsciiDigit) <= 32;
    }

    internal static bool SelfTest()
    {
        static bool Good(string source, string expected) =>
            TryParse(source, out var result) && result == expected;
        static bool Bad(string source) => !TryParse(source, out _);
        return Good("tel:+393479784437", "+393479784437") &&
            Good("TEL:0444-123456", "0444123456") &&
            Good("tel:%2B39%20347%209784437", "+393479784437") &&
            Good("callto:62", "62") &&
            Good("callto://3479784437", "3479784437") &&
            Bad("https://example.com") && Bad("tel:123;ext=45") &&
            Bad("callto:alice@example.com") &&
            Bad("mlcti:call?number=3479784437") &&
            Bad("MLCTI://call?number=%2B393479784437") &&
            Bad("tel:%Q2") && Bad("tel:123#foo") && Bad("tel:123abc") &&
            Bad("tel:++393479784437") && Bad("tel:");
    }
}
