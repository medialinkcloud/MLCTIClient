using System.Text.RegularExpressions;

namespace MedialinkCTIClient;

/// <summary>
/// Asterisk/UCM AMI outgoing-number extraction. Keep the dialled number
/// distinct from the local CallerIDNum (which may be the external trunk DID).
/// No network access; no personal numbers in diagnostic logs.
/// </summary>
internal static class OutgoingPeerRules
{
    private static string Value(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : "";

    // DialString may be a simple number, or a resource such as
    // PJSIP/347...@trunk, SIP/provider/347..., or Local/347...@context.
    // Avoid digit extraction from arbitrary SIP endpoint names or contexts.
    private static string ExtractNumber(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            text = text[4..];
        if (text.Contains('/')) text = text[(text.LastIndexOf('/') + 1)..];
        int at = text.IndexOf('@');
        if (at >= 0) text = text[..at];
        return Regex.IsMatch(text, @"^\+?[0-9*#]{1,32}$", RegexOptions.CultureInvariant)
            ? text : "";
    }

    internal static (string Number, string Source) Resolve(
        IReadOnlyDictionary<string, string> fields, string localExtension)
    {
        var localCaller = PhoneNumbers.SearchKey(Value(fields, "CallerIDNum"));
        // DialString is the destination of the Dial operation. The various
        // CallerID fields identify parties, not the digits dialled.
        foreach (var key in new[] { "DialString", "DestExten", "Exten", "ConnectedLineNum",
            "DestConnectedLineNum", "DestCallerIDNum" })
        {
            var candidate = ExtractNumber(Value(fields, key));
            if (candidate.Length == 0 || candidate == localExtension) continue;
            if (key != "DialString" &&
                PhoneNumbers.SearchKey(candidate) == localCaller) continue;
            return (candidate, key);
        }
        return ("", "unknown");
    }

    internal static bool SelfTest()
    {
        var outgoing = new Dictionary<string, string> {
            ["CallerIDNum"] = "0444417433", ["DestCallerIDNum"] = "0444417433",
            ["DialString"] = "3479784437"
        };
        if (Resolve(outgoing, "62") != ("3479784437", "DialString")) return false;
        outgoing["DialString"] = "PJSIP/3479784437@provider";
        if (Resolve(outgoing, "62").Number != "3479784437") return false;
        outgoing["DialString"] = "PJSIP/trunk/3479784437";
        if (Resolve(outgoing, "62").Number != "3479784437") return false;
        outgoing["DialString"] = "PJSIP/trunk";
        if (Resolve(outgoing, "62").Number.Length != 0) return false;
        outgoing["DestExten"] = "3479784437";
        if (Resolve(outgoing, "62").Number != "3479784437") return false;
        outgoing["DestExten"] = "s";
        outgoing["ConnectedLineNum"] = "3479784437";
        if (Resolve(outgoing, "62").Source != "ConnectedLineNum") return false;
        outgoing.Clear();
        outgoing["CallerIDNum"] = "62";
        outgoing["DialString"] = "202";
        if (Resolve(outgoing, "62").Number != "202") return false;
        return ExtractNumber("PJSIP/WRONG-3479784437") == "";
    }
}
