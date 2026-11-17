namespace MedialinkCTIClient;

/// <summary>Fail-closed scope selection for LDAP CDR contact insertion.
/// Never search the entire PBX LDAP tree to decide if a contact can be
/// added to a specific writable phonebook.</summary>
internal static class LdapDuplicateScopeRules
{
    internal static string DestinationBaseDn(AppConfig cfg) =>
        UcmContactWriteRules.ValidateDestinationDn(cfg.LdapWritePhonebookDn);

    internal static bool SelfTest()
    {
        const string medialink = "ou=medialink,dc=pbx,dc=com";
        var cfg = new AppConfig { LdapBaseDn = "dc=pbx,dc=com", LdapWritePhonebookDn = medialink };
        if (DestinationBaseDn(cfg) != medialink) return false;
        // Changing the browse/search Base DN must NOT change duplicate-check scope.
        cfg.LdapBaseDn = "ou=other,dc=pbx,dc=com";
        if (DestinationBaseDn(cfg) != medialink) return false;
        cfg.LdapWritePhonebookDn = "ou=OTHER,dc=pbx,dc=com";
        if (!DestinationBaseDn(cfg).Equals("ou=OTHER,dc=pbx,dc=com", StringComparison.Ordinal))
            return false;
        cfg.LdapWritePhonebookDn = "dc=pbx,dc=com";
        if (!Rejects(cfg)) return false;
        cfg.LdapWritePhonebookDn = "ou=pbx,dc=pbx,dc=com";
        if (!Rejects(cfg)) return false;
        cfg.LdapWritePhonebookDn = "";
        return Rejects(cfg);
    }

    private static bool Rejects(AppConfig cfg)
    {
        try { _ = DestinationBaseDn(cfg); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
