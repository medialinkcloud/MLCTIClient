namespace MedialinkCTIClient;

/// <summary>Pure rules: a cached negative lookup never blocks a fresh live call.
/// A live call attempts at most one recheck per number unless another call
/// starts. No network I/O or UI activity here.</summary>
internal static class LiveNameLookupRules
{
    internal static bool ShouldRecheck(LdapContactMatch? cached, bool attemptedForThisCall) =>
        !attemptedForThisCall && string.IsNullOrWhiteSpace(cached?.Contact?.DisplayName);

    internal static bool SelfTest()
    {
        var named = new LdapContactMatch(new UcmContactDetails { CallerIdName = "Contatto" });
        var empty = new LdapContactMatch(null);
        var noName = new LdapContactMatch(new UcmContactDetails { AccountNumber = "1" });
        return ShouldRecheck(null, false) && ShouldRecheck(empty, false) &&
            ShouldRecheck(noName, false) && !ShouldRecheck(named, false) &&
            !ShouldRecheck(null, true) && !ShouldRecheck(empty, true);
    }
}
