using System.Text.RegularExpressions;
namespace MedialinkCTIClient;
internal enum BlfState { Unknown, Free, Busy, Ringing, Dnd, Offline }
internal sealed record BlfEntry(string Extension, string Name);
internal static class BlfLogic
{
    internal static IReadOnlyList<BlfEntry> Parse(string? source, string own)
    {
        var rows = new List<BlfEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in (source ?? "").Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Trim().Split(new[] { ':' }, 2);
            var extension = parts[0].Trim();
            if (!Regex.IsMatch(extension, @"^[0-9]{1,8}$") || extension == own || !seen.Add(extension)) continue;
            rows.Add(new BlfEntry(extension, parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : "Interno " + extension));
            if (rows.Count == 100) break;
        }
        return rows;
    }
    internal static BlfState FromAmi(string? raw)
    {
        if (!int.TryParse(raw, out int code)) return BlfState.Unknown;
        if (code == -1) return BlfState.Offline;
        if (code < 0) return BlfState.Unknown;
        if ((code & 8) != 0) return BlfState.Ringing;
        if ((code & (1 | 2 | 16)) != 0) return BlfState.Busy;
        if ((code & 4) != 0) return BlfState.Offline;
        return code == 0 ? BlfState.Free : BlfState.Unknown;
    }
    internal static bool ValidPrefix(string? prefix) =>
        !string.IsNullOrWhiteSpace(prefix) && Regex.IsMatch(prefix, @"^[*#0-9]{1,12}$");
    internal static string? Target(BlfState state, string extension, string prefix)
    {
        if (!Regex.IsMatch(extension, @"^[0-9]{1,8}$")) return null;
        if (state == BlfState.Ringing) return ValidPrefix(prefix) ? prefix + extension : null;
        return state == BlfState.Free || state == BlfState.Busy || state == BlfState.Dnd ? extension : null;
    }
    internal static bool IsPickup(string number, AppConfig cfg) =>
        ValidPrefix(cfg.BlfPickupPrefix) && number.StartsWith(cfg.BlfPickupPrefix, StringComparison.Ordinal) &&
        Regex.IsMatch(number.Substring(cfg.BlfPickupPrefix.Length), @"^[0-9]{1,8}$") &&
        Parse(cfg.BlfExtensions, cfg.Extension).Any(x => x.Extension == number.Substring(cfg.BlfPickupPrefix.Length));
    internal static bool SelfTest() =>
        FromAmi("0") == BlfState.Free && FromAmi("8") == BlfState.Ringing &&
        FromAmi("1") == BlfState.Busy && FromAmi("4") == BlfState.Offline &&
        Target(BlfState.Ringing, "203", "**") == "**203" &&
        Target(BlfState.Free, "203", "**") == "203" &&
        Target(BlfState.Unknown, "203", "**") == null &&
        IsPickup("**203",new AppConfig { BlfPickupPrefix="**",BlfExtensions="203:Supporto" });
}
