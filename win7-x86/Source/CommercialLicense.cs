using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MedialinkCTIClient;

// Commercial licensing: signed server lease, per-user DPAPI storage, 7-day offline grace.
// Online revocation takes effect at the next successful validation; revocation cannot be
// learned while the server is unreachable. Local-clock rollback detection is best effort.
// R7.1: nome computer e MAC proposti localmente, visibili e confermati prima dell'invio.
internal sealed class LicenseRegistration
{
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public string? MacAddress { get; set; }
    public bool Confirmed { get; set; }

    internal void Validate()
    {
        if (!Confirmed) throw new InvalidDataException("Confermare l'invio dei dati di registrazione");
        if (string.IsNullOrWhiteSpace(Username) || Username.Trim().Length > 80)
            throw new InvalidDataException("Username obbligatorio (massimo 80 caratteri)");
        if (string.IsNullOrWhiteSpace(Email) || Email.Trim().Length > 254 ||
            !System.Text.RegularExpressions.Regex.IsMatch(Email.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
            throw new InvalidDataException("Indirizzo email non valido");
        if (string.IsNullOrWhiteSpace(ComputerName) || ComputerName.Trim().Length > 80 ||
            !System.Text.RegularExpressions.Regex.IsMatch(ComputerName.Trim(), @"^[^\r\n\t]+$"))
            throw new InvalidDataException("Nome computer non valido (massimo 80 caratteri)");
        if (!string.IsNullOrWhiteSpace(MacAddress) &&
            !System.Text.RegularExpressions.Regex.IsMatch(MacAddress.Trim(), @"^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$"))
            throw new InvalidDataException("MAC address non valido: usare AA:BB:CC:DD:EE:FF");
    }
}

internal static class CommercialLicense
{
    internal const string BaseUrl = "https://license.medialink.cloudns.eu:8443/api/v1/mlcti/license/";
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Medialink", "CTIClient", "Licensing");
    private static readonly string StatePath = Path.Combine(DirectoryPath, "commercial-v1.dpapi");
    private static readonly object Sync = new object();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Medialink|MLCTICLIENT|COMMERCIAL-V1");
    private sealed class State
    {
        public string LicenseId { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public string Token { get; set; } = "";
        public string LastSeenUtc { get; set; } = "";
        public bool DeniedOnline { get; set; }
    }
    private sealed class Token
    {
        public string LicenseId {get; private set;}
        public string Edition {get; private set;}
        public DateTimeOffset ValidatedUtc {get; private set;}
        public DateTimeOffset OfflineUntilUtc {get; private set;}
        public DateTimeOffset? ExpiresUtc {get; private set;}
        public bool Perpetual {get; private set;}
        public Token(string id,string edition,DateTimeOffset validated,DateTimeOffset offline,DateTimeOffset? expires,bool perpetual)
        {LicenseId=id;Edition=edition;ValidatedUtc=validated;OfflineUntilUtc=offline;ExpiresUtc=expires;Perpetual=perpetual;}
    }

    private static State Read()
    {
        lock (Sync)
        {
            if (!File.Exists(StatePath)) return new State();
            var input = File.ReadAllBytes(StatePath);
            if (input.Length > 65536) throw new InvalidDataException("Archivio licenza troppo grande");
            var clear = ProtectedData.Unprotect(input, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<State>(clear) ?? throw new InvalidDataException("Archivio licenza non valido");
        }
    }
    private static void Write(State value)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            byte[] raw = JsonSerializer.SerializeToUtf8Bytes(value);
            byte[] secured = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
            string temp = StatePath + ".tmp." + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temp, secured); if (File.Exists(StatePath)) File.Replace(temp, StatePath, null);
                  else File.Move(temp, StatePath); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    private static DateTimeOffset Utc(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var x) || x.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Data licenza mancante: " + name);
        string v = x.GetString() ?? "";
        if (!DateTimeOffset.TryParseExact(v, "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            throw new InvalidDataException("Data UTC licenza non valida: " + name);
        return dt;
    }
    private static string Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var x) || x.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Campo licenza mancante: " + name);
        return x.GetString() ?? "";
    }
    private static Token VerifyToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement;
        if (Str(item, "product") != "MLCTICLIENT" ||
            Str(item, "device_hash") != DemoLicenseClient.ComputeDeviceHash() ||
            !new[] { "STANDARD", "PRO", "ENTERPRISE" }.Contains(Str(item,"edition")) ||
            !item.TryGetProperty("license_version", out var vers) || !vers.TryGetInt32(out var v) || v != 1)
            throw new InvalidDataException("Token non autorizzato per questa postazione");
        if (!item.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array ||
            !new[] { "click_to_dial", "ldap", "cdr", "call_control" }
               .All(f => features.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == f)))
            throw new InvalidDataException("Funzioni commerciali non autorizzate");
        DemoLicenseVerifier.VerifySignedPayload(item); // RSA3072, canonical JSON, pinned SPKI
        var issued = Utc(item,"issued_at");
        var validated = Utc(item,"validated_at");
        var offline = Utc(item,"offline_until");
        bool perpetual = false;
        if (item.TryGetProperty("perpetual", out var flag))
        {
            if (flag.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Indicatore perpetuo non valido");
            perpetual = true;
        }
        DateTimeOffset? expires = null;
        if (perpetual)
        {
            if (!item.TryGetProperty("expires_at", out var expiryField) ||
                !(expiryField.ValueKind == JsonValueKind.Null ||
                  (expiryField.ValueKind == JsonValueKind.String &&
                   expiryField.GetString() == "2040-12-31T23:59:59Z")))
                throw new InvalidDataException("Una licenza perpetua non deve avere scadenza");
        }
        else
        {
            expires = Utc(item,"expires_at");
        }
        if (issued > validated || offline > validated.AddDays(7) ||
            (expires.HasValue && offline > expires.Value) || offline <= validated)
            throw new InvalidDataException("Intervallo offline firmato non coerente");
        return new Token(Str(item,"license_id"),Str(item,"edition"),validated,offline,expires,perpetual);
    }
    private static DateTimeOffset? Seen(State state) =>
        DateTimeOffset.TryParse(state.LastSeenUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;

    private static (bool Allowed, string Message) Assess(State s, bool saveClock)
    {
        if (string.IsNullOrWhiteSpace(s.LicenseId) || string.IsNullOrWhiteSpace(s.LicenseKey))
            return (false,"Licenza commerciale non configurata");
        if (s.DeniedOnline) return (false,"Licenza rifiutata dal License Server");
        if (string.IsNullOrWhiteSpace(s.Token)) return (false,"Manca una validazione online positiva");
        var verified = VerifyToken(s.Token);
        if (verified.LicenseId != s.LicenseId) return (false,"Token riferito a una licenza differente");
        var now = DateTimeOffset.UtcNow;
        var seen = Seen(s);
        if (seen.HasValue && now < seen.Value.AddMinutes(-2))
            return (false,"Orologio Windows arretrato: richiedere una verifica online");
        if (now < verified.ValidatedUtc.AddMinutes(-2))
            return (false,"Data del PC anteriore alla validazione del server");
        if (verified.ExpiresUtc.HasValue && now >= verified.ExpiresUtc.Value) return (false,"Licenza commerciale scaduta");
        if (now >= verified.OfflineUntilUtc) return (false,"Grace offline di 7 giorni terminato");
        if (saveClock && (!seen.HasValue || now > seen.Value.AddMinutes(1)))
        {
            s.LastSeenUtc=now.ToString("O",CultureInfo.InvariantCulture);
            Write(s);
        }
        string rights = verified.Perpetual
            ? $"{verified.Edition} perpetua - Attiva; scadenza tecnica 31/12/2040 23:59"
            : $"{verified.Edition} valida fino al {verified.ExpiresUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}";
        // ValidatedUtc e' la data della piu' recente validazione online firmata.
        // Lo stato grace compare solo dopo 24 ore senza validazioni positive.
        if (now >= verified.ValidatedUtc.AddHours(24))
            rights += $"; Modalita offline - autonomia fino al {verified.OfflineUntilUtc.ToLocalTime():dd/MM/yyyy HH:mm}";
        return (true, rights);
    }
    internal static bool IsConfigured() => File.Exists(StatePath);
    internal static bool IsAuthorized(out string reason)
    {
        try { var res=Assess(Read(),true);reason=res.Message;return res.Allowed; }
        catch (Exception) { reason="Licenza commerciale non verificabile o archivio danneggiato";return false; }
    }
    internal static string Status() { IsAuthorized(out var reason); return reason; }

    internal static async Task<string> ActivateAsync(string id, string key, LicenseRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException("Inserire License ID e License Key");
        registration.Validate();
        var response = await RequestAsync("activate",id.Trim(),key.Trim(),registration);
        var state=new State { LicenseId=id.Trim(),LicenseKey=key.Trim(),Token=response,
            LastSeenUtc=DateTimeOffset.UtcNow.ToString("O"),DeniedOnline=false };
        var check=Assess(state,false);
        if (!check.Allowed) throw new InvalidDataException(check.Message);
        Write(state);
        return check.Message;
    }
    private static async Task<string> RequestAsync(string action,string id,string key,LicenseRegistration? registration=null)
    {
        bool tlsInvalid = false;
        using var handler = new HttpClientHandler { AllowAutoRedirect=false };
        handler.ServerCertificateCustomValidationCallback = (_,_,_,errors) =>
        {
            if (errors != SslPolicyErrors.None) tlsInvalid = true;
            return errors == SslPolicyErrors.None; // never disable TLS validation
        };
        using var http = new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(10) };
        // Metadati R7 solo alla prima attivazione, dopo conferma esplicita.
        // Nessun nome utente o MAC letto automaticamente e nessun reinvio nel refresh.
        var content = registration == null
            ? JsonSerializer.Serialize(new { product="MLCTICLIENT", license_id=id,
                license_key=key, device_hash=DemoLicenseClient.ComputeDeviceHash() })
            : JsonSerializer.Serialize(new { product="MLCTICLIENT", license_id=id,
                license_key=key, device_hash=DemoLicenseClient.ComputeDeviceHash(),
                registration = new {
                    username = registration.Username.Trim(),
                    email = registration.Email.Trim(),
                    computer_name = registration.ComputerName.Trim(),
                    mac_address = string.IsNullOrWhiteSpace(registration.MacAddress) ? null : registration.MacAddress.Trim().ToUpperInvariant(),
                    confirmed = true
                } });
        using var req=new HttpRequestMessage(HttpMethod.Post,BaseUrl+action)
        { Content=new StringContent(content,Encoding.UTF8,"application/json") };
        HttpResponseMessage response;
        try { response=await http.SendAsync(req); }
        catch (HttpRequestException) when (tlsInvalid)
        { throw new LicenseTlsException("Certificato TLS non attendibile"); }
        using var res=response;
        if (res.StatusCode==HttpStatusCode.Forbidden || res.StatusCode==HttpStatusCode.BadRequest ||
            res.StatusCode==HttpStatusCode.NotFound || res.StatusCode==HttpStatusCode.Unauthorized)
            throw new LicenseRejectedException("Licenza rifiutata dal server: HTTP " + (int)res.StatusCode);
        if (res.StatusCode!=HttpStatusCode.OK)
            throw new HttpRequestException("License Server non disponibile: HTTP "+(int)res.StatusCode);
        var data=await res.Content.ReadAsByteArrayAsync();
        if (data.Length>32768) throw new InvalidDataException("Risposta del server eccessiva");
        using var doc=JsonDocument.Parse(data);
        if (!doc.RootElement.TryGetProperty("token",out var obj) || obj.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Token commerciale assente");
        var raw=obj.GetRawText();
        var verified=VerifyToken(raw);
        if (verified.LicenseId!=id) throw new InvalidDataException("ID licenza non corrispondente");
        return raw;
    }
    private sealed class LicenseRejectedException : Exception { public LicenseRejectedException(string message) : base(message) {} }
    private sealed class LicenseTlsException : Exception { public LicenseTlsException(string message) : base(message) {} }

    internal static async Task<string> RefreshAsync()
    {
        State s;
        try { s=Read(); } catch { return "Archivio licenza non leggibile"; }
        if (string.IsNullOrWhiteSpace(s.LicenseId)) return "Licenza commerciale assente";
        try
        {
            var token=await RequestAsync("validate",s.LicenseId,s.LicenseKey);
            s.Token=token;s.DeniedOnline=false;
            s.LastSeenUtc=DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
            Write(s);
            return Assess(s,false).Message;
        }
        catch (LicenseRejectedException)
        {
            s.DeniedOnline=true;s.Token="";Write(s);
            return "Licenza rifiutata dal License Server: CTI bloccato";
        }
        catch (LicenseTlsException)
        {
            // TLS cert failures are NOT an offline grace condition.
            s.DeniedOnline=true;s.Token="";Write(s);
            return "Certificato TLS del License Server non valido: CTI bloccato";
        }
        catch (HttpRequestException) { return Status(); } // Network error/5xx: only signed cached lease.
        catch (TaskCanceledException) { return Status(); }
        catch (Exception)
        {
            // A reached server returning malformed/unsigned data is not an offline event.
            s.DeniedOnline=true;s.Token="";Write(s);
            return "Risposta non firmata o non valida: CTI bloccato";
        }
    }
}
