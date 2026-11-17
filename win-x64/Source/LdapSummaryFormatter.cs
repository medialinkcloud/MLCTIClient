namespace MedialinkCTIClient;

// "Number" means a visible phone-number row (one LDAP contact can have more
// than one). The grouped counts refer to the returned LDAP phonebooks, NOT the
// unrelated 150-row CDR history limit.
internal static class LdapSummaryFormatter
{
    internal static string PhonebookName(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return "sconosciuta";
        var part = dn.Split(',', 2)[0].Trim();
        return part.StartsWith("ou=", StringComparison.OrdinalIgnoreCase)
            ? part[3..] : part;
    }

    internal static string Build(IEnumerable<Contact> contacts)
    {
        var list = contacts.ToArray();
        if (list.Length == 0) return "0 numeri LDAP · nessuna rubrica con risultati";
        var grouped = list.GroupBy(c => PhonebookName(c.PhonebookDn),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.Key + ": " + g.Count().ToString("N0"));
        return list.Length.ToString("N0") + " numeri LDAP · Rubriche: " + string.Join(" | ", grouped);
    }

    internal static bool SelfTest()
    {
        var list = new [] {
            new Contact("Uno", "123", "Telefono", "ou=medialink,dc=pbx,dc=com"),
            new Contact("Due", "456", "Telefono", "ou=medialink,dc=pbx,dc=com"),
            new Contact("Tre", "789", "Telefono", "ou=contacts,dc=pbx,dc=com")
        };
        var s = Build(list);
        return s.Contains("3 numeri LDAP") && s.Contains("medialink: 2") &&
            s.Contains("contacts: 1") && PhonebookName("ou=Link,dc=pbx,dc=com") == "Link";
    }
}
