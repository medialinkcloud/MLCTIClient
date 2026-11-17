namespace MedialinkCTIClient;

internal enum CdrLdapAction { Verify, Add, Edit, ExistingUneditable }

// UI action is driven by an authoritative lookup IN the selected destination
// phonebook, never by the display name (which can be historic or from elsewhere).
internal static class LdapDestinationRules
{
    internal static CdrLdapAction Action(LdapContactMatch? match, string? destinationDn)
    {
        if (match is null || !match.DestinationVerified) return CdrLdapAction.Verify;
        var selected = match.DestinationContact;
        if (selected is null) return CdrLdapAction.Add;
        return selected.CanEdit && string.Equals(selected.PhonebookDn, destinationDn,
            StringComparison.OrdinalIgnoreCase) ? CdrLdapAction.Edit : CdrLdapAction.ExistingUneditable;
    }

    internal static bool SelfTest()
    {
        const string target = "ou=medialink,dc=pbx,dc=com";
        var old = new UcmContactDetails { PhonebookDn = "ou=contacts,dc=pbx,dc=com",
            AccountNumber = "3479784437", CallerIdName = "Storico" };
        var onTarget = new UcmContactDetails { PhonebookDn = target,
            AccountNumber = "3479784437", CallerIdName = "Attuale" };
        var historicElsewhere = new LdapContactMatch(old, DestinationVerified:true);
        var current = new LdapContactMatch(onTarget, DestinationVerified:true, DestinationContact:onTarget);
        var unverified = new LdapContactMatch(old);
        var notEditable = new LdapContactMatch(onTarget, DestinationVerified:true,
            DestinationContact:new UcmContactDetails { PhonebookDn = target, CallerIdName="Nessun ID" });
        return Action(historicElsewhere,target)==CdrLdapAction.Add &&
            Action(current,target)==CdrLdapAction.Edit &&
            Action(unverified,target)==CdrLdapAction.Verify &&
            Action(null,target)==CdrLdapAction.Verify &&
            Action(new LdapContactMatch(null,DestinationVerified:true),target)==CdrLdapAction.Add &&
            Action(notEditable,target)==CdrLdapAction.ExistingUneditable;
    }
}
