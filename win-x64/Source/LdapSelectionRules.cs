namespace MedialinkCTIClient;

/// <summary>Choose one stable display candidate, but retain duplicate knowledge.
/// No LDAP writes are made by this class.</summary>
internal static class LdapSelectionRules
{
    internal static LdapContactMatch ChooseFirst(IEnumerable<UcmContactDetails> matches, string? preferredDn)
    {
        // LDAP server order is not stable. Prefer editable destination, then named
        // records, then DN and account number. Never silently choose a contact
        // for an edit if the user has not acknowledged multiple matches.
        var ordered = matches.OrderByDescending(d =>
                !string.IsNullOrWhiteSpace(preferredDn) &&
                string.Equals(d.PhonebookDn, preferredDn, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => !string.IsNullOrWhiteSpace(d.DisplayName))
            .ThenBy(d => d.PhonebookDn, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.AccountNumber, StringComparer.OrdinalIgnoreCase).ToArray();
        return new LdapContactMatch(ordered.FirstOrDefault(), ordered.Length > 1);
    }

    internal static LdapContactMatch ChooseWithDestination(IEnumerable<UcmContactDetails> matches,
        string? preferredDn, bool destinationVerified)
    {
        var found = matches.ToArray();
        var display = ChooseFirst(found, preferredDn);
        var target = destinationVerified && !string.IsNullOrWhiteSpace(preferredDn)
            ? found.Where(d => string.Equals(d.PhonebookDn, preferredDn,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        return display with {
            DestinationVerified = destinationVerified,
            DestinationContact = ChooseFirst(target, preferredDn).Contact,
            DestinationAmbiguous = target.Length > 1
        };
    }

    internal static bool SelfTest()
    {
        var a = new UcmContactDetails {
            PhonebookDn = "ou=contacts,dc=pbx,dc=com", AccountNumber = "3901", CallerIdName = "Alfa"
        };
        var b = new UcmContactDetails {
            PhonebookDn = "ou=medialink,dc=pbx,dc=com", AccountNumber = "3902", CallerIdName = "Beta"
        };
        var preferred = ChooseFirst([a,b], b.PhonebookDn);
        var reversed = ChooseFirst([b,a], b.PhonebookDn);
        var none = ChooseFirst([], b.PhonebookDn);
        var single = ChooseFirst([a], b.PhonebookDn);
        var displayOnly = new UcmContactDetails {
            PhonebookDn = b.PhonebookDn, AccountNumber = "", CallerIdName = "Display only"
        };
        var readOnly = ChooseFirst([displayOnly], b.PhonebookDn);
        return ReferenceEquals(preferred.Contact,b) && preferred.Ambiguous &&
               ReferenceEquals(reversed.Contact,b) && reversed.Ambiguous &&
               none.Contact is null && !none.Ambiguous &&
               ReferenceEquals(single.Contact,a) && !single.Ambiguous &&
               readOnly.Contact?.DisplayName == "Display only" &&
               !readOnly.Contact.CanEdit && single.Contact?.CanEdit == true;
    }
}
