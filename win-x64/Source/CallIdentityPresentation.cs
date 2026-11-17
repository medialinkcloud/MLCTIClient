namespace MedialinkCTIClient;

/// <summary>Pure, two-line caller presentation rules; never initiate LDAP/API traffic.</summary>
internal static class CallIdentityPresentation
{
    internal static string ExtensionSubtitle(string? displayName, string? extension)
    {
        var name = (displayName ?? "").Trim();
        var number = (extension ?? "").Trim();
        return name.Length > 0 ? $"{name} · Interno {number} · Grandstream UCM" :
            $"Interno {number} · Grandstream UCM";
    }

    // Each physical UI label is exactly one line. Never allow LDAP data to inject
    // line breaks into the call banner or mini popup.
    private static string OneLine(string? input)
    {
        var value = (input ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    internal static string NumberLine(string? number) =>
        OneLine(number) is { Length: > 0 } text ? text : "Numero non disponibile";

    internal static string NameLine(string? name) =>
        OneLine(name) is { Length: > 0 } text ? text : "Nome non disponibile";

    internal static string MiniText(string? name, string? number) =>
        NumberLine(number) + Environment.NewLine + NameLine(name);

    internal static string LiveText(string phase, string? name, string? number) =>
        MiniText(name, number);

    internal static bool SelfTest() =>
        ExtensionSubtitle("Antonio", "62").StartsWith("Antonio · Interno 62", StringComparison.Ordinal) &&
        ExtensionSubtitle("", "62").StartsWith("Interno 62", StringComparison.Ordinal) &&
        MiniText("Mario Rossi", "3471234567") == "3471234567" + Environment.NewLine + "Mario Rossi" &&
        MiniText("", "3471234567") == "3471234567" + Environment.NewLine + "Nome non disponibile" &&
        MiniText("Mario\nRossi", "347\r123") == "347 123" + Environment.NewLine + "Mario Rossi" &&
        LiveText("In arrivo", "Mario Rossi", "3471234567") == MiniText("Mario Rossi", "3471234567");
}
