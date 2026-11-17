namespace MedialinkCTIClient;

// A UCM contact is identified by (phonebook DN, ORIGINAL account number).
// Keep the identity separate from the edited values so updateContact cannot
// inadvertently address the newly edited account number as an existing record.
internal sealed class UcmContactDetails
{
    public string PhonebookDn { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string CallerIdName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string HomeNumber { get; set; } = "";
    public string MobileNumber { get; set; } = "";
    public string FamilyNumber { get; set; } = "";
    public string Fax { get; set; } = "";
    public string Department { get; set; } = "";
    public string Position { get; set; } = "";

    public string DisplayName => !string.IsNullOrWhiteSpace(CallerIdName) ? CallerIdName :
        (FirstName + " " + LastName).Trim();

    // A valid LDAP display result is not necessarily writable via UCM API.
    // Never offer an edit button without the original account key and a
    // modifiable phonebook DN; also never offer '+' for this existing record.
    public bool CanEdit => AccountNumber.Length > 0 && AccountNumber.All(LegacyCompat.IsAsciiDigit) &&
        PhonebookDn.Length > 0 &&
        !PhonebookDn.Equals("ou=pbx,dc=pbx,dc=com", StringComparison.OrdinalIgnoreCase);

    public UcmContactDetails Copy() => (UcmContactDetails)MemberwiseClone();

    public static UcmContactDetails New(string dn, string number, string name)
    {
        return new UcmContactDetails {
            PhonebookDn = dn,
            AccountNumber = UcmContactWriteRules.AccountNumber(PhoneNumbers.Dialable(number)),
            CallerIdName = name.Trim(), FirstName = name.Trim(), HomeNumber = PhoneNumbers.Dialable(number)
        };
    }
}

// Contact = best LDAP name across searchable phonebooks; used for caller display.
// DestinationContact = contact verified in the configured *writable* phonebook.
// Keeping them separate is essential: a historic name or a match in another
// phonebook must NEVER enable the edit pencil for the configured phonebook.
internal sealed record LdapContactMatch(UcmContactDetails? Contact, bool Ambiguous = false,
    bool DestinationVerified = false, UcmContactDetails? DestinationContact = null,
    bool DestinationAmbiguous = false)
{
    public bool Found => Contact is not null || Ambiguous;
}
