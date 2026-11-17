using System.Text.RegularExpressions;

namespace MedialinkCTIClient;

internal static class UcmContactWriteRules
{
    // UCM HTTPS API FP15: accountnumber must be numeric. A leading + is preserved
    // in the mobile/home attribute, while the numeric accountnumber remains dialable.
    public static string ValidateNumber(string number, int internalExtensionMaxLength)
    {
        var original = (number ?? "").Trim();
        if (!Regex.IsMatch(original, @"^\+?[0-9\s()\-./]{4,40}$"))
            throw new InvalidOperationException("Numero non valido. Sono ammessi cifre, + iniziale e separatori telefonici.");
        string normalized = PhoneNumbers.Dialable(original);
        var digits = normalized.TrimStart('+');
        if (digits.Length <= internalExtensionMaxLength || digits.Length > 20)
            throw new InvalidOperationException("Non è un numero esterno valido (controlla la lunghezza o l'interno).");
        if (!digits.All(LegacyCompat.IsAsciiDigit))
            throw new InvalidOperationException("Il numero deve contenere solo cifre e l'eventuale + iniziale.");
        return normalized;
    }

    public static string AccountNumber(string normalized) => normalized.TrimStart('+');

    public static string ValidateName(string name)
    {
        string clean = (name ?? "").Trim();
        if (clean.Length is < 2 or > 100 || clean.Any(char.IsControl))
            throw new InvalidOperationException("Inserire un nome contatto tra 2 e 100 caratteri.");
        return clean;
    }

    public static UcmContactDetails ValidateDetails(UcmContactDetails d, int internalMax, bool editing)
    {
        ValidateDestinationDn(d.PhonebookDn);
        if (d.AccountNumber.Length is < 1 or > 20 || !d.AccountNumber.All(LegacyCompat.IsAsciiDigit))
            throw new InvalidOperationException("Account Number: inserire da 1 a 20 cifre numeriche.");
        ValidateName(d.CallerIdName);
        foreach (var (label, value, limit) in new (string, string, int)[] {
            ("Nome", d.FirstName, 100), ("Cognome", d.LastName, 100),
            ("Email", d.Email, 254), ("Cellulare", d.MobileNumber, 40),
            ("Telefono", d.HomeNumber, 40), ("Numero famiglia", d.FamilyNumber, 40),
            ("Fax", d.Fax, 40), ("Reparto", d.Department, 100), ("Posizione", d.Position, 100) })
        {
            if (value.Length > limit || value.Any(char.IsControl))
                throw new InvalidOperationException(label + ": caratteri di controllo o lunghezza non valida.");
        }
        if (!string.IsNullOrWhiteSpace(d.Email) &&
            (!d.Email.Contains('@') || d.Email.StartsWith('@') || d.Email.EndsWith('@')))
            throw new InvalidOperationException("Indirizzo email non valido.");
        if (!editing && d.AccountNumber.Length <= internalMax)
            throw new InvalidOperationException("Il numero appartiene agli interni: inserimento non consentito.");
        // UCM allows different numbers in the same contact; validate each populated one.
        foreach (var number in new[] { d.HomeNumber, d.MobileNumber, d.FamilyNumber, d.Fax })
        {
            if (number.Length == 0) continue;
            if (number.Length > 40 || !System.Text.RegularExpressions.Regex.IsMatch(number,
                @"^\+?[0-9\s()\-./]{1,40}$"))
                throw new InvalidOperationException("Numero telefonico non valido: sono consentite cifre e separatori.");
        }
        return d;
    }

    public static string ValidateDestinationDn(string value)
    {
        var dn = (value ?? "").Trim();
        if (!Regex.IsMatch(dn, @"^ou=[a-zA-Z0-9][a-zA-Z0-9_-]{0,63},dc=pbx,dc=com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Rubrica LDAP: indicare una OU UCM esistente, es. ou=contatti,dc=pbx,dc=com.");
        if (dn.Equals("ou=pbx,dc=pbx,dc=com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La rubrica interna ou=pbx,dc=pbx,dc=com è di sola lettura.");
        return dn;
    }

    public static bool SameNumber(string left, string right)
    {
        // The UCM accountnumber is digits-only, whereas MobileNumber may contain +.
        // Check both the established Italy-aware lookup and equivalent numeric forms.
        return PhoneNumbers.SearchKey(left) == PhoneNumbers.SearchKey(right) ||
               PhoneNumbers.Dialable(left).TrimStart('+') == PhoneNumbers.Dialable(right).TrimStart('+');
    }

    public static bool SelfTest()
    {
        return ValidateNumber("+39 0444-123456", 4) == "+390444123456"
            && AccountNumber("+390444123456") == "390444123456"
            && SameNumber("+39 333 1234567", "3331234567")
            && SameNumber("393331234567", "+393331234567")
            && ValidateDestinationDn("ou=contatti,dc=pbx,dc=com").StartsWith("ou=contatti")
            && Throws(() => ValidateDestinationDn("ou=pbx,dc=pbx,dc=com"))
            && Throws(() => ValidateNumber("62", 4))
            && Throws(() => ValidateNumber("anonymous", 4));
    }
    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
