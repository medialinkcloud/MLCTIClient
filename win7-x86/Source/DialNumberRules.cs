using System.Text.RegularExpressions;

namespace MedialinkCTIClient;

internal sealed record DialCountry(string Iso2, string Name, string CountryCode)
{
    public string DisplayName => Name + " (+" + CountryCode + ")";
    public override string ToString() => DisplayName;
}

/// <summary>Offline list of international calling codes for selection in settings.
/// This country setting is only for removing the user's own international prefix.
/// A country code is never guessed from a bare domestic number.</summary>
internal static class DialCountryCatalog
{
    internal static readonly IReadOnlyList<DialCountry> All = new DialCountry[]
    {
        new DialCountry("AF", "Afghanistan", "93"),
        new DialCountry("AL", "Albania", "355"),
        new DialCountry("DZ", "Algeria", "213"),
        new DialCountry("AO", "Angola", "244"),
        new DialCountry("AI", "Anguilla", "1264"),
        new DialCountry("AG", "Antigua e Barbuda", "1268"),
        new DialCountry("SA", "Arabia Saudita", "966"),
        new DialCountry("AR", "Argentina", "54"),
        new DialCountry("AM", "Armenia", "374"),
        new DialCountry("AW", "Aruba", "297"),
        new DialCountry("AU", "Australia", "61"),
        new DialCountry("AT", "Austria", "43"),
        new DialCountry("AZ", "Azerbaijan", "994"),
        new DialCountry("BS", "Bahamas", "1242"),
        new DialCountry("BH", "Bahrein", "973"),
        new DialCountry("BD", "Bangladesh", "880"),
        new DialCountry("BB", "Barbados", "1246"),
        new DialCountry("BE", "Belgio", "32"),
        new DialCountry("BZ", "Belize", "501"),
        new DialCountry("BJ", "Benin", "229"),
        new DialCountry("BM", "Bermuda", "1441"),
        new DialCountry("BT", "Bhutan", "975"),
        new DialCountry("BY", "Bielorussia", "375"),
        new DialCountry("BO", "Bolivia", "591"),
        new DialCountry("BA", "Bosnia ed Erzegovina", "387"),
        new DialCountry("BW", "Botswana", "267"),
        new DialCountry("BR", "Brasile", "55"),
        new DialCountry("BN", "Brunei", "673"),
        new DialCountry("BG", "Bulgaria", "359"),
        new DialCountry("BF", "Burkina Faso", "226"),
        new DialCountry("BI", "Burundi", "257"),
        new DialCountry("KH", "Cambogia", "855"),
        new DialCountry("CM", "Camerun", "237"),
        new DialCountry("CA", "Canada", "1"),
        new DialCountry("CV", "Capo Verde", "238"),
        new DialCountry("TD", "Ciad", "235"),
        new DialCountry("CL", "Cile", "56"),
        new DialCountry("CN", "Cina", "86"),
        new DialCountry("CY", "Cipro", "357"),
        new DialCountry("VA", "Città del Vaticano", "39"),
        new DialCountry("CO", "Colombia", "57"),
        new DialCountry("KM", "Comore", "269"),
        new DialCountry("CG", "Congo", "242"),
        new DialCountry("CD", "Congo (Rep. Dem.)", "243"),
        new DialCountry("KP", "Corea del Nord", "850"),
        new DialCountry("KR", "Corea del Sud", "82"),
        new DialCountry("CI", "Costa D'Avorio", "225"),
        new DialCountry("CR", "Costa Rica", "506"),
        new DialCountry("HR", "Croazia", "385"),
        new DialCountry("CU", "Cuba", "53"),
        new DialCountry("DK", "Danimarca", "45"),
        new DialCountry("DM", "Dominica", "1767"),
        new DialCountry("EC", "Ecuador", "593"),
        new DialCountry("EG", "Egitto", "20"),
        new DialCountry("SV", "El Salvador", "503"),
        new DialCountry("AE", "Emirati Arabi Uniti", "971"),
        new DialCountry("ER", "Eritrea", "291"),
        new DialCountry("EE", "Estonia", "372"),
        new DialCountry("ET", "Etiopia", "251"),
        new DialCountry("FJ", "Figi", "679"),
        new DialCountry("PH", "Filippine", "63"),
        new DialCountry("FI", "Finlandia", "358"),
        new DialCountry("FR", "Francia", "33"),
        new DialCountry("GA", "Gabon", "241"),
        new DialCountry("GM", "Gambia", "220"),
        new DialCountry("GE", "Georgia", "995"),
        new DialCountry("GS", "Georgia del Sud e Isole Sandwich Meridionali", "500"),
        new DialCountry("DE", "Germania", "49"),
        new DialCountry("GH", "Ghana", "233"),
        new DialCountry("JM", "Giamaica", "1876"),
        new DialCountry("JP", "Giappone", "81"),
        new DialCountry("GI", "Gibilterra", "350"),
        new DialCountry("DJ", "Gibuti", "253"),
        new DialCountry("JO", "Giordania", "962"),
        new DialCountry("GR", "Grecia", "30"),
        new DialCountry("GD", "Grenada", "1473"),
        new DialCountry("GL", "Groenlandia", "299"),
        new DialCountry("GP", "Guadeloupa", "590"),
        new DialCountry("GU", "Guam", "1671"),
        new DialCountry("GT", "Guatemala", "502"),
        new DialCountry("GG", "Guernsey", "44"),
        new DialCountry("GN", "Guinea", "224"),
        new DialCountry("GQ", "Guinea Equatoriale", "240"),
        new DialCountry("GW", "Guinea-Bissau", "245"),
        new DialCountry("GY", "Guyana", "592"),
        new DialCountry("GF", "Guyana francese", "594"),
        new DialCountry("HT", "Haiti", "509"),
        new DialCountry("HN", "Honduras", "504"),
        new DialCountry("HK", "Hong Kong", "852"),
        new DialCountry("IN", "India", "91"),
        new DialCountry("ID", "Indonesia", "62"),
        new DialCountry("IR", "Iran", "98"),
        new DialCountry("IQ", "Iraq", "964"),
        new DialCountry("IE", "Irlanda", "353"),
        new DialCountry("IS", "Islanda", "354"),
        new DialCountry("JE", "Isola di Jersey", "44"),
        new DialCountry("IM", "Isola di Man", "44"),
        new DialCountry("CX", "Isola di Natale", "61"),
        new DialCountry("NF", "Isola Norfolk", "672"),
        new DialCountry("KY", "Isole Cayman", "1345"),
        new DialCountry("CC", "Isole Cocos e Keeling", "61"),
        new DialCountry("CK", "Isole Cook", "682"),
        new DialCountry("FK", "Isole Falkland o Isole Malvine", "500"),
        new DialCountry("FO", "Isole Far Oer", "298"),
        new DialCountry("MP", "Isole Marianne Settentrionali", "1670"),
        new DialCountry("MH", "Isole Marshall", "692"),
        new DialCountry("PN", "Isole Pitcairn", "64"),
        new DialCountry("SB", "Isole Salomone", "677"),
        new DialCountry("TK", "Isole Tokelau", "690"),
        new DialCountry("IL", "Israele", "972"),
        new DialCountry("IT", "Italia", "39"),
        new DialCountry("KZ", "Kazakistan", "7"),
        new DialCountry("KE", "Kenya", "254"),
        new DialCountry("KG", "Kirghizistan", "996"),
        new DialCountry("KI", "Kiribati", "686"),
        new DialCountry("XK", "Kosovo", "383"),
        new DialCountry("KW", "Kuwait", "965"),
        new DialCountry("LA", "Laos", "856"),
        new DialCountry("LS", "Lesotho", "266"),
        new DialCountry("LV", "Lettonia", "371"),
        new DialCountry("LB", "Libano", "961"),
        new DialCountry("LR", "Liberia", "231"),
        new DialCountry("LY", "Libia", "218"),
        new DialCountry("LI", "Liechtenstein", "423"),
        new DialCountry("LT", "Lituania", "370"),
        new DialCountry("LU", "Lussemburgo", "352"),
        new DialCountry("MO", "Macao", "853"),
        new DialCountry("MK", "Macedonia", "389"),
        new DialCountry("MG", "Madagascar", "261"),
        new DialCountry("MW", "Malawi", "265"),
        new DialCountry("MV", "Maldive", "960"),
        new DialCountry("MY", "Malesia", "60"),
        new DialCountry("ML", "Mali", "223"),
        new DialCountry("MT", "Malta", "356"),
        new DialCountry("MA", "Marocco", "212"),
        new DialCountry("MQ", "Martinica", "596"),
        new DialCountry("MR", "Mauritania", "222"),
        new DialCountry("MU", "Mauritius", "230"),
        new DialCountry("YT", "Mayotte", "262"),
        new DialCountry("MX", "Messico", "52"),
        new DialCountry("FM", "Micronesia", "691"),
        new DialCountry("MD", "Moldavia", "373"),
        new DialCountry("MN", "Mongolia", "976"),
        new DialCountry("MS", "Montserrat", "1664"),
        new DialCountry("MZ", "Mozambico", "258"),
        new DialCountry("NA", "Namibia", "264"),
        new DialCountry("NR", "Nauru", "674"),
        new DialCountry("NP", "Nepal", "977"),
        new DialCountry("NI", "Nicaragua", "505"),
        new DialCountry("NE", "Niger", "227"),
        new DialCountry("NG", "Nigeria", "234"),
        new DialCountry("NU", "Niue", "683"),
        new DialCountry("NO", "Norvegia", "47"),
        new DialCountry("NC", "Nuova Caledonia", "687"),
        new DialCountry("NZ", "Nuova Zelanda", "64"),
        new DialCountry("OM", "oman", "968"),
        new DialCountry("NL", "Paesi Bassi", "31"),
        new DialCountry("PK", "Pakistan", "92"),
        new DialCountry("PW", "Palau", "680"),
        new DialCountry("PA", "Panama", "507"),
        new DialCountry("PG", "Papua Nuova Guinea", "675"),
        new DialCountry("PY", "Paraguay", "595"),
        new DialCountry("PE", "Perù", "51"),
        new DialCountry("PF", "Polinesia Francese", "689"),
        new DialCountry("PL", "Polonia", "48"),
        new DialCountry("PR", "Porto Rico", "1787"),
        new DialCountry("PT", "Portogallo", "351"),
        new DialCountry("MC", "Principato di Monaco", "377"),
        new DialCountry("QA", "Qatar", "974"),
        new DialCountry("GB", "Regno Unito", "44"),
        new DialCountry("CZ", "Repubblica Ceca", "420"),
        new DialCountry("CF", "Repubblica Centrafricana", "236"),
        new DialCountry("DO", "Repubblica Dominicana", "1809"),
        new DialCountry("RE", "Riunione", "262"),
        new DialCountry("RO", "Romania", "40"),
        new DialCountry("RW", "Ruanda", "250"),
        new DialCountry("RU", "Russia", "7"),
        new DialCountry("EH", "Sahara Occidentale", "212"),
        new DialCountry("KN", "Saint Kitts e Nevis", "1869"),
        new DialCountry("VC", "Saint Vincent e Grenadine", "1784"),
        new DialCountry("PM", "Saint-Pierre e Miquelon", "508"),
        new DialCountry("WS", "Samoa", "685"),
        new DialCountry("AS", "Samoa Americane", "1684"),
        new DialCountry("SM", "San Marino", "378"),
        new DialCountry("SH", "Sant'Elena", "290"),
        new DialCountry("LC", "Santa Lucia", "1758"),
        new DialCountry("SN", "Senegal", "221"),
        new DialCountry("RS", "Serbia", "381"),
        new DialCountry("SC", "Seychelles", "248"),
        new DialCountry("SL", "Sierra Leone", "232"),
        new DialCountry("SG", "Singapore", "65"),
        new DialCountry("SY", "Siria", "963"),
        new DialCountry("SK", "Slovacchia", "421"),
        new DialCountry("SI", "Slovenia", "386"),
        new DialCountry("SO", "Somalia", "252"),
        new DialCountry("ES", "Spagna", "34"),
        new DialCountry("LK", "Sri Lanka", "94"),
        new DialCountry("US", "Stati Uniti D'America", "1"),
        new DialCountry("ZA", "Sud Africa", "27"),
        new DialCountry("SD", "Sudan", "249"),
        new DialCountry("SS", "Sudan del sud", "211"),
        new DialCountry("SR", "Suriname", "597"),
        new DialCountry("SJ", "Svalbard e Jan Mayen", "4779"),
        new DialCountry("SE", "Svezia", "46"),
        new DialCountry("CH", "Svizzera", "41"),
        new DialCountry("SZ", "Swaziland", "268"),
        new DialCountry("ST", "São Tomé e Príncipe", "239"),
        new DialCountry("TJ", "Tagikistan", "992"),
        new DialCountry("TH", "Tailandia", "66"),
        new DialCountry("TW", "Taiwan", "886"),
        new DialCountry("TZ", "Tanzania", "255"),
        new DialCountry("IO", "Territorio britannico dell'oceano indiano", "246"),
        new DialCountry("TL", "Timor Est", "670"),
        new DialCountry("TG", "Togo", "228"),
        new DialCountry("TO", "Tonga", "676"),
        new DialCountry("TT", "Trinidad e Tobago", "1868"),
        new DialCountry("TN", "Tunisia", "216"),
        new DialCountry("TR", "Turchia", "90"),
        new DialCountry("TM", "Turkmenistan", "993"),
        new DialCountry("TV", "Tuvalu", "688"),
        new DialCountry("UA", "Ucraina", "380"),
        new DialCountry("UG", "Uganda", "256"),
        new DialCountry("HU", "Ungheria", "36"),
        new DialCountry("UY", "Uruguay", "598"),
        new DialCountry("UZ", "Uzbekistan", "998"),
        new DialCountry("VU", "Vanuatu", "678"),
        new DialCountry("VE", "Venezuela", "58"),
        new DialCountry("VN", "Vietnam", "84"),
        new DialCountry("WF", "Wallis e Futuna", "681"),
        new DialCountry("YE", "Yemen", "967"),
        new DialCountry("ZM", "Zambia", "260"),
        new DialCountry("ZW", "Zimbabwe", "263"),
    };

    // ISO-based stable selection, independent from list sorting or WinForms data binding.
    internal static int IndexByIso(string? iso)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Iso2.Equals(iso, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < All.Count; i++)
            if (All[i].Iso2 == "IT") return i;
        throw new InvalidOperationException("Paese predefinito Italia assente dal catalogo.");
    }

    internal static DialCountry ByIso(string? iso) =>
        All.FirstOrDefault(c => c.Iso2.Equals(iso, StringComparison.OrdinalIgnoreCase)) ??
        All.First(c => c.Iso2 == "IT");
}

/// <summary>One common, pure normalization step before dispatch to AMI/Snom/Yealink.
/// The LDAP original number is never changed and the PBX extension number is not prefixed.</summary>
internal static class DialNumberRules
{
    internal static bool TryPrepare(string? supplied, AppConfig config, out string target, out bool external)
    {
        target = ""; external = false;
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 120) return false;
        string normalized = PhoneNumbers.Dialable(supplied);
        if (!PhoneNumbers.ValidDialString(normalized) || normalized == "+") return false;
        // DTMF service codes are dialed verbatim: never apply country, E.164 or trunk-prefix rules.
        // Keep *, # exactly as entered (e.g. **64, *8, #21#).
        if (normalized.Contains('*') || normalized.Contains('#'))
        {
            if (normalized.StartsWith('+')) return false;
            target = normalized;
            external = false;
            return true;
        }

        DialCountry own = DialCountryCatalog.ByIso(config.DialCountryIso);
        bool isInternational = normalized.StartsWith('+') || normalized.StartsWith("00", StringComparison.Ordinal);
        string ownPrefix = own.CountryCode;
        if (normalized.StartsWith("+" + ownPrefix, StringComparison.Ordinal) &&
            normalized.Length > ownPrefix.Length + 1)
            normalized = normalized.Substring(ownPrefix.Length + 1);
        else if (normalized.StartsWith("00" + ownPrefix, StringComparison.Ordinal) &&
            normalized.Length > ownPrefix.Length + 2)
            normalized = normalized.Substring(ownPrefix.Length + 2);
        else if (normalized.StartsWith('+'))
            normalized = "00" + normalized.Substring(1);

        // Treat short domestic digits as PBX extensions; international numbers always external
        // even when the remaining country-specific subscriber number is short.
        external = isInternational || normalized.Length > LegacyCompat.Clamp(config.InternalExtensionMaxLength, 1, 8);
        if (external && !string.IsNullOrEmpty(config.ExternalLineAccessPrefix))
        {
            if (!Regex.IsMatch(config.ExternalLineAccessPrefix, @"^[0-9]{1,6}$")) return false;
            normalized = config.ExternalLineAccessPrefix + normalized;
        }
        if (!Regex.IsMatch(normalized, @"^[0-9]{1,32}$")) return false;
        target = normalized;
        return true;
    }

    internal static bool SelfTest()
    {
        var config = new AppConfig { DialCountryIso = "IT", InternalExtensionMaxLength = 4 };
        static bool Expect(AppConfig c, string raw, string expected, bool external) =>
            TryPrepare(raw, c, out var actual, out var isExternal) &&
            actual == expected && isExternal == external;
        if (!Expect(config, "+39 0444 417433", "0444417433", true) ||
            !Expect(config, "0039 0444 417433", "0444417433", true) ||
            !Expect(config, "+39 347 9784437", "3479784437", true) ||
            !Expect(config, "+44 20 1234 5678", "00442012345678", true) ||
            !Expect(config, "62", "62", false) ||
            !Expect(config, "0444 417433", "0444417433", true)) return false;
        config.ExternalLineAccessPrefix = "0";
        if (!Expect(config, "**64", "**64", false) ||
            !Expect(config, "*8", "*8", false) ||
            !Expect(config, "#21#", "#21#", false) ||
            !Expect(config, "+39 0444 417433", "00444417433", true) ||
            !Expect(config, "+44 20 1234 5678", "000442012345678", true) ||
            !Expect(config, "62", "62", false)) return false;
        config.DialCountryIso = "DE";
        if (!Expect(config, "+49 30 1234567", "0301234567", true) ||
            !Expect(config, "+39 3479784437", "000393479784437", true)) return false;
        return !TryPrepare("", config, out _, out _) &&
            !TryPrepare("+", config, out _, out _) &&
            !TryPrepare("12345678901234567890123456789012345", config, out _, out _) &&
            DialCountryCatalog.All.Count > 220;
    }
}
