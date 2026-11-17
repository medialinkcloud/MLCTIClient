using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MedialinkCTIClient;

internal sealed class LdapClient
{
    private static readonly string[] FallbackName = ["CallerIDName", "displayName", "cn", "FirstName", "LastName", "givenName", "sn"];
    private static readonly string[] FallbackPhone = ["AccountNumber", "MobileNumber", "HomeNumber", "telephoneNumber", "mobile"];

    public Task<List<Contact>> SearchAsync(AppConfig cfg, string keyword, CancellationToken ct = default) =>
        Task.Run(() => Search(cfg, keyword, ct), ct);

    public Task<string> DiagnoseAsync(AppConfig cfg) => Task.Run(() => Diagnose(cfg));

    // After an HTTPS API write the UCM may briefly rebuild its LDAP tree.
    // Only READS are retried and each attempt creates a fresh LDAP connection;
    // no addContact/updateContact/deleteContact action is ever replayed.
    private static bool MissingObjectDuringRefresh(DirectoryOperationException error) =>
        error.Response?.ResultCode == ResultCode.NoSuchObject;

    // A UCM LDAP server can briefly fail a subtree search at dc=pbx,dc=com
    // during a phonebook rebuild. A successful search of the explicitly configured
    // phonebook is useful for positive name matches, but is NOT proof that a
    // number is absent from the other phonebooks.
    private static string? FallbackPhonebookDn(AppConfig cfg, string failedBase)
    {
        try
        {
            var dn = UcmContactWriteRules.ValidateDestinationDn(cfg.LdapWritePhonebookDn);
            return string.Equals(dn, failedBase, StringComparison.OrdinalIgnoreCase) ? null : dn;
        }
        catch (InvalidOperationException) { return null; }
    }

    public async Task<List<Contact>> SearchAfterMutationAsync(AppConfig cfg, string keyword,
        CancellationToken ct = default)
    {
        DirectoryOperationException? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await SearchAsync(cfg, keyword, ct); }
            catch (DirectoryOperationException e) when (MissingObjectDuringRefresh(e))
            {
                last = e;
                DiagnosticLog.Warn("LDAP_READ_DURING_UCM_APPLY", "attempt=" + (attempt + 1));
                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(600 * (attempt + 1)), ct);
            }
        }
        throw new InvalidOperationException("La rubrica LDAP UCM non risponde ancora dopo le modifiche. " +
            "I contatti non sono stati cancellati dal client: aggiornare la vista quando la UCM è pronta.", last);
    }

    public async Task<Dictionary<string, LdapContactMatch>> LookupAfterMutationAsync(
        AppConfig cfg, IEnumerable<string> numbers, CancellationToken ct = default)
    {
        var selected = numbers.ToArray();
        DirectoryOperationException? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await LookupForNumbersAsync(cfg, selected, ct); }
            catch (DirectoryOperationException e) when (MissingObjectDuringRefresh(e))
            {
                last = e;
                DiagnosticLog.Warn("LDAP_READ_DURING_UCM_APPLY", "attempt=" + (attempt + 1));
                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(600 * (attempt + 1)), ct);
            }
        }
        throw new InvalidOperationException("Rubrica LDAP temporaneamente non disponibile durante l'applicazione UCM.", last);
    }


    private static LdapConnection Connect(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.LdapHost)) throw new InvalidOperationException("Server LDAP non impostato.");
        if (string.IsNullOrWhiteSpace(cfg.LdapBaseDn)) throw new InvalidOperationException("Base DN LDAP non impostato.");
        if (cfg.LdapPort is < 1 or > 65535) throw new InvalidOperationException("Porta LDAP non valida.");
        var id = new LdapDirectoryIdentifier(cfg.LdapHost, cfg.LdapPort, fullyQualifiedDnsHostName: false, connectionless: false);
        var conn = new LdapConnection(id, new NetworkCredential(cfg.LdapUser, cfg.LdapSecret), AuthType.Basic);
        try
        {
            conn.SessionOptions.ProtocolVersion = 3;
            conn.SessionOptions.SecureSocketLayer = cfg.LdapSsl;
            conn.Timeout = TimeSpan.FromSeconds(15);
            conn.Bind();
            DiagnosticLog.Info("LDAP_BIND_OK");
            return conn;
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("LDAP_BIND_FAILED", e);
            conn.Dispose();
            throw;
        }
    }

    private static string[] Names(string list, string[] fallback)
    {
        var parsed = (list ?? "").Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var n in parsed)
            if (!Regex.IsMatch(n, @"^[a-zA-Z][a-zA-Z0-9-]*$"))
                throw new InvalidOperationException("Nome attributo LDAP non valido: " + n);
        return parsed.Length == 0 ? fallback : parsed;
    }

    private static string BuildFilter(AppConfig cfg, string keyword)
    {
        var token = EscapeFilter(keyword.Trim());
        var query = token.Length == 0 ? "*" : "*" + token + "*";
        var name = string.IsNullOrWhiteSpace(cfg.LdapNameFilter) ? "(CallerIDName=%)" : cfg.LdapNameFilter.Trim();
        var number = string.IsNullOrWhiteSpace(cfg.LdapNumberFilter) ?
            "(|(AccountNumber=%)(MobileNumber=%)(HomeNumber=%))" : cfg.LdapNumberFilter.Trim();
        if (!name.Contains('%') || !number.Contains('%'))
            throw new InvalidOperationException("I filtri LDAP devono contenere il segnaposto %.");
        return "(|" + name.Replace("%", query) + number.Replace("%", query) + ")";
    }

    private static SearchResponse SearchRequest(LdapConnection conn, string dn, string filter, int sizeLimit, string[] attributes)
    {
        var request = new SearchRequest(dn, filter, SearchScope.Subtree, attributes)
        { SizeLimit = sizeLimit, TimeLimit = TimeSpan.FromSeconds(15) };
        SearchResponse response;
        try { response = (SearchResponse)conn.SendRequest(request); }
        catch (DirectoryOperationException error) when (error.Response is SearchResponse partial &&
            partial.ResultCode == ResultCode.SizeLimitExceeded)
        {
            response = partial;
        }
        // Preserve NoSuchObject so bounded post-write reads can retry when
        // the UCM is rebuilding its LDAP directory (no mutation is retried).
        if (response.ResultCode == ResultCode.NoSuchObject)
            throw new DirectoryOperationException(response);
        if (response.ResultCode != ResultCode.Success && response.ResultCode != ResultCode.SizeLimitExceeded)
            throw new InvalidOperationException($"LDAP Search: {response.ResultCode} - {response.ErrorMessage}");
        return response;
    }

    // Do not cap LDAP contact count in the client (CDR retains its own limit).
    // RFC2696 paged results if supported; an unrestricted LDAP search otherwise.
    // A server-side SizeLimitExceeded is NEVER treated as full results.
    private static List<SearchResultEntry> ReadAllEntries(LdapConnection conn, string dn,
        string filter, string[] attributes, CancellationToken ct)
    {
        const int pageSize = 100;
        // The guard catches buggy servers that repeat cookies; not a contact limit.
        const int maximumPagesSafety = 100000;
        var entries = new List<SearchResultEntry>();
        byte[] cookie = [];
        var visitedCookies = new HashSet<string>(StringComparer.Ordinal);
        conn.Timeout = TimeSpan.FromSeconds(45);
        for (int page = 0; page < maximumPagesSafety; page++)
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchRequest(dn, filter, SearchScope.Subtree, attributes)
            { SizeLimit = 0, TimeLimit = TimeSpan.FromSeconds(40) };
            request.Controls.Add(new PageResultRequestControl(pageSize)
            { Cookie = cookie, IsCritical = false });
            SearchResponse response;
            try { response = (SearchResponse)conn.SendRequest(request); }
            catch (DirectoryOperationException e) when (e.Response is SearchResponse partial &&
                partial.ResultCode == ResultCode.SizeLimitExceeded)
            {
                throw new InvalidOperationException("LDAP: limite imposto dalla UCM, risultati incompleti.",e);
            }
            catch (DirectoryOperationException e) when (e.Response?.ResultCode == ResultCode.NoSuchObject)
            { throw; }
            catch (DirectoryOperationException e) when (page == 0 &&
                e.Response?.ResultCode == ResultCode.UnavailableCriticalExtension)
            {
                DiagnosticLog.Warn("LDAP_PAGING_UNAVAILABLE", "fallback=unrestricted");
                return UnrestrictedRead(conn,dn,filter,attributes,ct);
            }
            if (response.ResultCode == ResultCode.NoSuchObject)
                throw new DirectoryOperationException(response);
            if (response.ResultCode == ResultCode.SizeLimitExceeded)
                throw new InvalidOperationException("LDAP: limite server, ricerca non completa.");
            if (response.ResultCode != ResultCode.Success)
                throw new InvalidOperationException("LDAP Search: " + response.ResultCode +
                    " - " + response.ErrorMessage);
            var pager = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (pager is null)
            {
                // An ignored paging request could return fewer results than exist.
                DiagnosticLog.Warn("LDAP_PAGING_UNAVAILABLE", "fallback=unrestricted");
                return UnrestrictedRead(conn,dn,filter,attributes,ct);
            }
            entries.AddRange(response.Entries.Cast<SearchResultEntry>());
            if (pager.Cookie.Length == 0)
            {
                DiagnosticLog.Info("LDAP_FULL_SEARCH_COMPLETE",
                    $"records={entries.Count}; pages={page + 1}; paged=true");
                return entries;
            }
            var nextCookie = Convert.ToBase64String(pager.Cookie);
            if (!visitedCookies.Add(nextCookie))
                throw new InvalidOperationException("LDAP: cookie di pagina ripetuto; lettura non completa.");
            cookie = pager.Cookie;
        }
        throw new InvalidOperationException("LDAP: ricerca paginata non completata.");
    }

    private static List<SearchResultEntry> UnrestrictedRead(LdapConnection conn, string dn,
        string filter, string[] attributes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = new SearchRequest(dn,filter,SearchScope.Subtree,attributes)
        { SizeLimit = 0, TimeLimit = TimeSpan.FromSeconds(40) };
        SearchResponse response;
        try { response = (SearchResponse)conn.SendRequest(request); }
        catch (DirectoryOperationException e) when (e.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            throw new InvalidOperationException("La UCM applica un limite LDAP e non supporta " +
                "la paginazione: impossibile caricare l'intera rubrica senza una strategia " +
                "di ricerca compatibile col firmware. Nessun totale parziale viene presentato.",e);
        }
        if (response.ResultCode == ResultCode.NoSuchObject)
            throw new DirectoryOperationException(response);
        if (response.ResultCode != ResultCode.Success)
            throw new InvalidOperationException("LDAP Search: " + response.ResultCode +
                " - " + response.ErrorMessage);
        var entries = response.Entries.Cast<SearchResultEntry>().ToList();
        DiagnosticLog.Info("LDAP_FULL_SEARCH_COMPLETE",
            $"records={entries.Count}; pages=1; paged=false");
        return entries;
    }

    private static (List<Contact> contacts, int records) SearchConnected(
        LdapConnection conn, AppConfig cfg, string keyword, CancellationToken ct, string? baseDn = null)
    {
        var nameAttributes = Names(cfg.LdapNameAttributes, FallbackName);
        var phoneAttributes = Names(cfg.LdapNumberAttributes, FallbackPhone);
        var fields = nameAttributes.Concat(FallbackName).Concat(phoneAttributes).Concat(FallbackPhone)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = ReadAllEntries(conn, baseDn ?? cfg.LdapBaseDn,
            BuildFilter(cfg, keyword), fields, ct);
        DiagnosticLog.Info("LDAP_QUERY_RESULT",
            $"records={entries.Count}; code=Success; keyword_present={!string.IsNullOrWhiteSpace(keyword)}");
        var results = new List<Contact>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SearchResultEntry entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = First(entry, [.. nameAttributes, "displayName", "cn"]);
            if (string.IsNullOrWhiteSpace(name))
                name = (First(entry, "FirstName", "givenName") + " " + First(entry, "LastName", "sn")).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "Senza nome";
            var entryPhones = new List<(string Number, string Attribute)>();
            foreach (var attribute in phoneAttributes.Concat(FallbackPhone).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!entry.Attributes.Contains(attribute)) continue;
                foreach (var item in entry.Attributes[attribute]!.GetValues(typeof(string)).OfType<string>())
                {
                    var number = item.Trim();
                    if (number.Length > 0) entryPhones.Add((number, attribute));
                }
            }
            // UCM API requires numeric AccountNumber, while the associated mobile/home
            // may preserve +countrycode: display it once, preferring the + form.
            foreach (var item in entryPhones
                .GroupBy(x => PhoneNumbers.Dialable(x.Number).TrimStart('+'), StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(x => x.Number.StartsWith('+')).First()))
            {
                var dedup = entry.DistinguishedName + "|" + PhoneNumbers.Dialable(item.Number).TrimStart('+');
                if (unique.Add(dedup)) results.Add(new Contact(name, item.Number,
                    item.Attribute.Contains("mobile", StringComparison.OrdinalIgnoreCase) ? "Cellulare" : "Telefono",
                    PhonebookDnOf(entry.DistinguishedName), First(entry, "AccountNumber")));
            }
        }
        DiagnosticLog.Info("LDAP_CONTACTS_RESULT", $"records={entries.Count}; numbers={results.Count}");
        return (results.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), entries.Count);
    }

    private static List<Contact> Search(AppConfig cfg, string keyword, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = Connect(cfg);
        ct.ThrowIfCancellationRequested();
        try { return SearchConnected(conn, cfg, keyword, ct).contacts; }
        catch (DirectoryOperationException error) when (MissingObjectDuringRefresh(error) &&
            FallbackPhonebookDn(cfg, cfg.LdapBaseDn) is not null)
        {
            string dn = FallbackPhonebookDn(cfg, cfg.LdapBaseDn)!;
            DiagnosticLog.Warn("LDAP_GLOBAL_BASE_UNAVAILABLE", "read_fallback=configured_phonebook; scope=partial");
            using var fallback = Connect(cfg); // Fresh session after NoSuchObject.
            return SearchConnected(fallback, cfg, keyword, ct, dn).contacts;
        }
    }

    private static string Diagnose(AppConfig cfg)
    {
        var report = new StringBuilder();
        try
        {
            using var conn = Connect(cfg);
            report.AppendLine("Bind LDAP: riuscito.");
            report.AppendLine("Base DN: " + cfg.LdapBaseDn);
            try
            {
                var probe = SearchRequest(conn, cfg.LdapBaseDn, "(objectClass=*)", 50, ["objectClass"]);
                report.AppendLine($"Ricerca base (objectClass=*): {probe.Entries.Count} record (limite 50).");
                DiagnosticLog.Info("LDAP_DIAGNOSTIC_BASE", $"records={probe.Entries.Count}; code={probe.ResultCode}");
            }
            catch (Exception e) { DiagnosticLog.Error("LDAP_DIAGNOSTIC_BASE_FAILED", e);
                report.AppendLine("Ricerca base: ERRORE - " + e.Message); }
            try
            {
                var r = SearchConnected(conn, cfg, "", CancellationToken.None);
                report.AppendLine($"Ricerca rubrica: {r.records} record LDAP, {LdapSummaryFormatter.Build(r.contacts)}.");
                DiagnosticLog.Info("LDAP_DIAGNOSTIC_CONTACTS", $"records={r.records}; numbers={r.contacts.Count}");
                if (r.records > 0 && r.contacts.Count == 0)
                    report.AppendLine("I record esistono, ma i campi telefonici non sono stati trovati: controllare Attributi numeri.");
                if (r.records == 0)
                    report.AppendLine("Nessun record per il filtro: verificare Base DN e filtri nome/numero.");
            }
            catch (Exception e) { DiagnosticLog.Error("LDAP_DIAGNOSTIC_CONTACTS_FAILED", e);
                report.AppendLine("Ricerca rubrica: ERRORE - " + e.Message); }
        }
        catch (Exception e) { DiagnosticLog.Error("LDAP_DIAGNOSTIC_BIND_FAILED", e);
            report.AppendLine("Bind LDAP: ERRORE - " + e.Message); }
        return report.ToString();
    }

    /// <summary>Ensure the destination is an EXISTING UCM branch, never create one implicitly.</summary>
    public Task VerifyWritableDestinationAsync(AppConfig cfg, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string dn = UcmContactWriteRules.ValidateDestinationDn(cfg.LdapWritePhonebookDn);
            using var conn = Connect(cfg);
            var resp = SearchRequest(conn,dn,"(objectClass=*)",1,["objectClass"]);
            if (resp.Entries.Count == 0)
                throw new InvalidOperationException("Rubrica destinazione inesistente sulla UCM. " +
                    "Creala prima in Impostazioni sistema > Server LDAP > Rubrica LDAP.");
            DiagnosticLog.Info("UCM_LDAP_DESTINATION_VERIFIED");
        },ct);

    /// <summary>Check duplicates ONLY in the configured writable phonebook.
    /// UCM contacts are identified by (phonebook_dn, accountnumber); the same number
    /// in another phonebook is not an existing entry in this destination.</summary>
    public Task<bool> ContainsNumberInDestinationAsync(AppConfig cfg,string number,CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var target = PhoneNumbers.Dialable(number);
            if (target.Length == 0) throw new InvalidOperationException("Numero vuoto.");
            var candidates = new HashSet<string>(StringComparer.Ordinal)
            {
                target, target.TrimStart('+'), PhoneNumbers.SearchKey(target)
            };
            // Italy-specific equivalent spellings, comparing full canonical numbers in C#.
            string key = PhoneNumbers.SearchKey(target);
            if (key.Length >= 6 && !target.StartsWith('+') && !target.StartsWith("00"))
            {
                candidates.Add("+39" + key);
                candidates.Add("0039" + key);
            }
            var fields = new[] { "AccountNumber", "MobileNumber", "HomeNumber", "FamilyNumber", "Fax", "telephoneNumber", "mobile" };
            var filters = candidates.Where(v=>v.Length > 0).SelectMany(v=>fields.Select(f =>
                $"({f}={EscapeFilter(v)})"));
            var filter = "(|" + string.Concat(filters) + ")";
            // A search rooted at dc=pbx,dc=com would also see other phonebooks,
            // producing false duplicates when the selected destination is empty.
            string destinationDn = LdapDuplicateScopeRules.DestinationBaseDn(cfg);
            using var conn = Connect(cfg);
            var result = SearchRequest(conn,destinationDn,filter,500,fields);
            // If there are >500 exact matching LDAP records, stop rather than guess.
            if (result.ResultCode == ResultCode.SizeLimitExceeded)
                throw new InvalidOperationException("Ricerca duplicati LDAP troppo ampia: salvataggio annullato per sicurezza.");
            foreach (SearchResultEntry item in result.Entries)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var attr in fields)
                {
                    if (!item.Attributes.Contains(attr)) continue;
                    foreach (var value in item.Attributes[attr]!.GetValues(typeof(string)).OfType<string>())
                    {
                        if (UcmContactWriteRules.SameNumber(value,target))
                        {
                            DiagnosticLog.Info("UCM_LDAP_DUPLICATE_FOUND", "scope=destination");
                            return true;
                        }
                    }
                }
            }
            DiagnosticLog.Info("UCM_LDAP_DUPLICATE_NOT_FOUND", "scope=destination");
            return false;
        },ct);

    // Resolve names independently of the Rubrica tab's 150-entry browsing limit.
    // One connection, batched exact-number searches; no per-CDR-row network calls.
    public Task<Dictionary<string, LdapContactMatch>> LookupForNumbersAsync(
        AppConfig cfg, IEnumerable<string> numbers, CancellationToken ct = default) =>
        Task.Run(() => LookupForNumbers(cfg, numbers, ct), ct);

    private static readonly string[] DetailNumberFields =
        ["AccountNumber", "MobileNumber", "HomeNumber", "FamilyNumber", "telephoneNumber", "mobile"];
    private static readonly string[] DetailFields =
        ["AccountNumber", "CallerIDName", "FirstName", "LastName", "Email", "HomeNumber",
         "MobileNumber", "FamilyNumber", "Fax", "Department", "Position", "displayName", "cn"];

    private static IEnumerable<string> Variants(string number)
    {
        var dialable = PhoneNumbers.Dialable(number);
        if (dialable.Length == 0) return [];
        var key = PhoneNumbers.SearchKey(dialable);
        var result = new HashSet<string>(StringComparer.Ordinal) { dialable, dialable.TrimStart('+'), key };
        if (key.Length >= 6 && !key.StartsWith('+') && !key.StartsWith("00"))
        {
            result.Add("+39" + key);
            result.Add("0039" + key);
            result.Add("39" + key);
        }
        return result.Where(x => x.Length != 0);
    }

    private static string NumbersFilter(IEnumerable<string> numbers)
    {
        var terms = numbers.SelectMany(Variants).Distinct(StringComparer.Ordinal)
            .SelectMany(number => DetailNumberFields.Select(field =>
                $"({field}={EscapeFilter(number)})"));
        return "(|" + string.Concat(terms) + ")";
    }

    private static string PhonebookDnOf(string entryDn)
    {
        int start = entryDn.StartsWith("ou=", StringComparison.OrdinalIgnoreCase) ? 0 :
            entryDn.IndexOf(",ou=", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        if (start > 0) start++;
        return entryDn[start..];
    }

    private static UcmContactDetails DetailsFrom(SearchResultEntry entry)
    {
        var firstName = First(entry, "FirstName", "givenName");
        var lastName = First(entry, "LastName", "sn");
        var callerName = First(entry, "CallerIDName", "displayName");
        // cn often contains a numeric LDAP identifier. Prefer an actual
        // FirstName/LastName to cn when CallerIDName is not set.
        if (string.IsNullOrWhiteSpace(callerName) &&
            string.IsNullOrWhiteSpace((firstName + " " + lastName).Trim()))
            callerName = First(entry, "cn");
        return new UcmContactDetails
        {
            PhonebookDn = PhonebookDnOf(entry.DistinguishedName),
            AccountNumber = First(entry, "AccountNumber"),
            CallerIdName = callerName,
            FirstName = firstName,
            LastName = lastName,
            Email = First(entry, "Email", "mail"),
            HomeNumber = First(entry, "HomeNumber", "telephoneNumber"),
            MobileNumber = First(entry, "MobileNumber", "mobile"),
            FamilyNumber = First(entry, "FamilyNumber", "family_number"),
            Fax = First(entry, "Fax", "facsimileTelephoneNumber"),
            Department = First(entry, "Department"),
            Position = First(entry, "Position")
        };
    }

    private static bool Matches(SearchResultEntry entry, string number)
    {
        foreach (var field in DetailNumberFields)
        {
            if (!entry.Attributes.Contains(field)) continue;
            foreach (var value in entry.Attributes[field]!.GetValues(typeof(string)).OfType<string>())
                if (UcmContactWriteRules.SameNumber(value, number)) return true;
        }
        return false;
    }

    private static Dictionary<string, LdapContactMatch> LookupForNumbers(
        AppConfig cfg, IEnumerable<string> numbers, CancellationToken ct)
    {
        var targets = numbers.Select(PhoneNumbers.Dialable).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new Dictionary<string, LdapContactMatch>(StringComparer.OrdinalIgnoreCase);
        if (targets.Length == 0) return results;
        using var conn = Connect(cfg);
        foreach (var batch in targets.Chunk(10))
        {
            ct.ThrowIfCancellationRequested();
            var fields = DetailFields.Concat(DetailNumberFields)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var filter = NumbersFilter(batch);
            SearchResponse response;
            bool complete = true;
            try { response = SearchRequest(conn, "dc=pbx,dc=com", filter, 500, fields); }
            catch (DirectoryOperationException error) when (MissingObjectDuringRefresh(error) &&
                FallbackPhonebookDn(cfg, "dc=pbx,dc=com") is not null)
            {
                // Display positive matches from the configured phonebook only.
                // NEVER turn its negative results into '+' (global absence).
                using var fallback = Connect(cfg); // Fresh LDAP session after failed root search.
                response = SearchRequest(fallback, FallbackPhonebookDn(cfg, "dc=pbx,dc=com")!,
                    filter, 500, fields);
                complete = false;
                DiagnosticLog.Warn("LDAP_GLOBAL_BASE_UNAVAILABLE", "lookup_fallback=configured_phonebook; scope=partial");
            }
            if (response.ResultCode == ResultCode.SizeLimitExceeded)
                throw new InvalidOperationException("La ricerca esatta LDAP ha superato il limite: verifica manuale necessaria.");
            // A successful global-base query covers the selected phonebook too.
            // If the global base is unavailable, the fallback explicitly searched
            // the configured phonebook: a missing result proves absence THERE,
            // not necessarily across every other UCM phonebook.
            string? writableDn = FallbackPhonebookDn(cfg, "");
            bool writableVerified = !string.IsNullOrWhiteSpace(writableDn);
            foreach (var number in batch)
            {
                // Do not discard LDAP records without AccountNumber: they remain
                // valid as names, but never as verified editable records.
                var found = response.Entries.Cast<SearchResultEntry>()
                    .Where(e => Matches(e,number))
                    .GroupBy(e => e.DistinguishedName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => DetailsFrom(g.First())).ToArray();
                if (complete || found.Length > 0 || writableVerified)
                    results[PhoneNumbers.SearchKey(number)] =
                        LdapSelectionRules.ChooseWithDestination(found, writableDn, writableVerified);
            }
        }
        return results;
    }

    /// <summary>Dedicated read-only lookup for UCM-managed PBX extensions.
    /// Never uses this branch for contact writes or deletion.</summary>
    // BLF candidate extensions come only from the PBX's internal LDAP directory.
    // The branch is read-only: no write or API mutation is performed.
    public Task<List<Contact>> ListInternalExtensionsAsync(AppConfig cfg, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            const string internalDn = "ou=pbx,dc=pbx,dc=com";
            ct.ThrowIfCancellationRequested();
            using var conn = Connect(cfg);
            string[] fields = ["AccountNumber", "CallerIDName", "displayName", "cn", "FirstName", "LastName", "telephoneNumber"];
            var entries = ReadAllEntries(conn, internalDn, "(objectClass=*)", fields, ct);
            var result = new Dictionary<string, Contact>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                string ext = First(entry, "AccountNumber");
                // Only a real extension identifier, never fallback to mobile or home numbers.
                if (!System.Text.RegularExpressions.Regex.IsMatch(ext, @"^[0-9]{1,8}$")) continue;
                string name = First(entry, "CallerIDName", "displayName");
                if (string.IsNullOrWhiteSpace(name))
                    name = (First(entry, "FirstName") + " " + First(entry, "LastName")).Trim();
                if (string.IsNullOrWhiteSpace(name)) name = "Interno " + ext;
                if (!result.ContainsKey(ext)) result.Add(ext, new Contact(name, ext, "Interno", internalDn, ext));
            }
            DiagnosticLog.Info("BLF_LDAP_DIRECTORY_READ", "count=" + result.Count + "; scope=pbx; read_only=true");
            return result.Values.OrderBy(c => c.Number.PadLeft(8, '0'), StringComparer.Ordinal).ToList();
        }, ct);

    public Task<string> LookupInternalExtensionNameAsync(AppConfig cfg, string extension,
        CancellationToken ct = default) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!PhoneNumbers.ValidExtension(extension)) return "";
            const string internalDn = "ou=pbx,dc=pbx,dc=com";
            var filter = NumbersFilter([extension]);
            using var conn = Connect(cfg);
            var fields = DetailFields.Concat(DetailNumberFields)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            SearchResponse response;
            try { response = SearchRequest(conn, internalDn, filter, 100, fields); }
            catch (DirectoryOperationException e) when (MissingObjectDuringRefresh(e))
            {
                DiagnosticLog.Warn("LDAP_INTERNAL_BRANCH_UNAVAILABLE", "scope=pbx; read_only=true");
                return "";
            }
            if (response.ResultCode == ResultCode.SizeLimitExceeded)
                throw new InvalidOperationException("LDAP pbx: risposta incompleta alla ricerca interno.");
            var matches = response.Entries.Cast<SearchResultEntry>()
                .Where(e => Matches(e, extension))
                .Select(DetailsFrom)
                .OrderBy(d => d.AccountNumber == extension ? 0 : 1)
                .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var name = matches.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.DisplayName))?.DisplayName ?? "";
            DiagnosticLog.Info("LDAP_INTERNAL_NAME", "found=" + (!string.IsNullOrWhiteSpace(name)).ToString().ToLowerInvariant());
            return name;
        }, ct);

    public Task<UcmContactDetails> ReadContactAsync(AppConfig cfg, string dn, string accountNumber,
        CancellationToken ct = default) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            dn = UcmContactWriteRules.ValidateDestinationDn(dn);
            if (!accountNumber.All(char.IsAsciiDigit) || accountNumber.Length == 0)
                throw new InvalidOperationException("Identificativo del contatto LDAP non valido.");
            using var conn = Connect(cfg);
            SearchResponse response;
            try
            {
                response = SearchRequest(conn, dn, "(AccountNumber=" + EscapeFilter(accountNumber) + ")", 2, DetailFields);
            }
            catch (DirectoryOperationException e) when (MissingObjectDuringRefresh(e))
            {
                throw new InvalidOperationException("Il contatto o la rubrica LDAP non è più raggiungibile. " +
                    "Non modificare/eliminare prima di una nuova verifica sulla UCM.", e);
            }
            if (response.ResultCode == ResultCode.SizeLimitExceeded || response.Entries.Count != 1)
                throw new InvalidOperationException("Contatto LDAP non univoco o non più presente: aggiornare il registro.");
            var result = DetailsFrom(response.Entries[0]);
            if (!string.Equals(result.PhonebookDn,dn,StringComparison.OrdinalIgnoreCase) ||
                result.AccountNumber != accountNumber)
                throw new InvalidOperationException("Identità LDAP cambiata: modifica bloccata.");
            return result;
        }, ct);

    // Duplicate guard for edits: the record being edited must not conflict with itself.
    public Task<bool> HasOtherContactWithNumberAsync(AppConfig cfg, string number, string originalDn,
        string originalAccount, CancellationToken ct = default) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var conn = Connect(cfg);
            var response = SearchRequest(conn, "dc=pbx,dc=com", NumbersFilter([number]), 500,
                DetailFields.Concat(DetailNumberFields).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            if (response.ResultCode == ResultCode.SizeLimitExceeded)
                throw new InvalidOperationException("Troppi risultati LDAP: modifica annullata per sicurezza.");
            foreach (SearchResultEntry e in response.Entries)
            {
                if (!Matches(e, number)) continue;
                var d = DetailsFrom(e);
                if (string.Equals(d.PhonebookDn, originalDn, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.AccountNumber, originalAccount, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }, ct);

    private static string First(SearchResultEntry entry, params string[] names)
    {
        foreach (var name in names)
            if (entry.Attributes.Contains(name) && entry.Attributes[name]!.Count > 0)
                return entry.Attributes[name]![0]?.ToString()?.Trim() ?? "";
        return "";
    }

    public static bool SelfTestSafeReadFallback()
    {
        var cfg = new AppConfig { LdapWritePhonebookDn = "ou=medialink,dc=pbx,dc=com" };
        return FallbackPhonebookDn(cfg, "dc=pbx,dc=com") == "ou=medialink,dc=pbx,dc=com" &&
            FallbackPhonebookDn(cfg, "ou=medialink,dc=pbx,dc=com") is null &&
            FallbackPhonebookDn(new AppConfig { LdapWritePhonebookDn = "" }, "dc=pbx,dc=com") is null;
    }

    private static string EscapeFilter(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value)
            result.Append(c switch
            {
                '*' => "\\2a", '(' => "\\28", ')' => "\\29", '\\' => "\\5c", '\0' => "\\00",
                _ => c.ToString()
            });
        return result.ToString();
    }
}
