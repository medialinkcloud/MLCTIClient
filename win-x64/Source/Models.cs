using System.Text.RegularExpressions;

namespace MedialinkCTIClient;

internal sealed record Contact(string Name, string Number, string Type, string PhonebookDn = "", string AccountNumber = "");
internal sealed record CallEntry(string Id, string Timestamp, string Direction, string Peer, string Duration,
    string Disposition, string PeerName = "", string AnsweredBy = "", bool AnsweredElsewhere = false);

internal static class PhoneNumbers
{
    public static string Dialable(string? raw)
    {
        // Keep the leading '+' only when it exists in the original value.
        var value = (raw ?? "").Trim();
        var plus = value.StartsWith('+');
        // Preserve DTMF * and # for dialing and feature codes.
        var dial = new string(value.Where(c => char.IsAsciiDigit(c) || c == '*' || c == '#').ToArray());
        return plus ? "+" + dial : dial;
    }

    public static string SearchKey(string? raw)
    {
        var cleaned = new string(Dialable(raw).Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        if (cleaned.StartsWith("+39")) return cleaned[3..];
        if (cleaned.StartsWith("0039")) return cleaned[4..];
        // Grandstream LDAP stores some Italian numbers as digits-only 39...
        // instead of +39... . Normalize that form only when a plausible
        // Italian geographic/mobile national number follows the prefix.
        if (cleaned.StartsWith("39") && cleaned.Length >= 11 &&
            (cleaned[2] == '0' || cleaned[2] == '3')) return cleaned[2..];
        return cleaned;
    }

    public static bool ValidExtension(string value) => Regex.IsMatch(value, @"^[0-9]{1,8}$");
    public static bool ValidDialString(string value) => Regex.IsMatch(value, @"^\+?[0-9*#]{1,32}$");
}
