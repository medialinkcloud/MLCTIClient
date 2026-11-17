using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MedialinkCTIClient;

/// <summary>
/// Resolves one physical phone from the UCM SIP registration table, using AMI PJSIPShowContacts.
/// No static phone IP or DHCP reservation is necessary. Never chooses ambiguously.
/// The remote HTTP CTI command remains device-specific and LAN-only.
/// </summary>
internal sealed class AmiPhoneDiscovery
{
    // PJSIPShowEndpoints is documented by Grandstream for UCM6xxx AMI and
    // exposes Contacts in EndpointList events. PJSIPShowContacts is not exposed
    // on every UCM firmware/AMI permission profile.
    private const string PreferredAction = "PJSIPShowEndpoints";
    private const string LegacyAction = "PJSIPShowContacts";
    internal sealed record Contact(string Endpoint, string Uri, string UserAgent, string Status);
    private sealed class AmiActionRejectedException : InvalidOperationException
    {
        public AmiActionRejectedException(string action)
            : base("La UCM non permette l'azione AMI " + action + ".") { }
    }

    public async Task<IPAddress> ResolveAsync(AppConfig cfg, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.AmiUser) || string.IsNullOrWhiteSpace(cfg.AmiSecret))
            throw new InvalidOperationException("Credenziali AMI non configurate.");
        if (!PhoneNumbers.ValidExtension(cfg.Extension))
            throw new InvalidOperationException("Interno non valido.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(9));
        try
        {
            List<Contact> contacts;
            string source = PreferredAction;
            try
            {
                contacts = await QueryAmiAsync(cfg, PreferredAction, timeout.Token);
                if (!contacts.Any(c => c.Endpoint.Equals(cfg.Extension, StringComparison.OrdinalIgnoreCase)))
                {
                    DiagnosticLog.Warn("PHONE_AMI_ENDPOINT_NO_CONTACT", "action=PJSIPShowEndpoints; extension_registered=false");
                    contacts = await QueryAmiAsync(cfg, LegacyAction, timeout.Token);
                    source = LegacyAction;
                }
            }
            catch (AmiActionRejectedException)
            {
                DiagnosticLog.Warn("PHONE_AMI_ACTION_FALLBACK", "from=PJSIPShowEndpoints; to=PJSIPShowContacts");
                contacts = await QueryAmiAsync(cfg, LegacyAction, timeout.Token);
                source = LegacyAction;
            }
            var address = ChooseAddress(contacts, cfg.Extension, cfg.PhoneType);
            DiagnosticLog.Info("PHONE_AMI_DISCOVERY_OK", "action=" + source + "; unique_phone=true; type=" + cfg.PhoneType);
            return address;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            DiagnosticLog.Warn("PHONE_AMI_DISCOVERY_TIMEOUT");
            throw new InvalidOperationException("Ricerca IP telefono tramite AMI scaduta. Verificare PJSIPShowEndpoints, autorizzazioni e connessione UCM.");
        }
        catch (AmiActionRejectedException ex)
        {
            DiagnosticLog.Warn("PHONE_AMI_ACTION_UNAVAILABLE", "action=both; response=error");
            throw new InvalidOperationException(ex.Message + " Verificare i permessi AMI della UCM o usare temporaneamente l'IP manuale.");
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("PHONE_AMI_DISCOVERY_FAILED", e);
            throw;
        }
    }

    private static List<Contact> ParseEndpointList(Dictionary<string,string> packet)
    {
        var endpoint = packet.GetValueOrDefault("ObjectName", "").Trim();
        var raw = packet.GetValueOrDefault("Contacts", "");
        return ParseEndpointContacts(endpoint, raw);
    }

    internal static List<Contact> ParseEndpointContacts(string endpoint, string contactsField)
    {
        var result = new List<Contact>();
        if (!PhoneNumbers.ValidExtension(endpoint)) return result;
        // Grandstream UCM AMI example:
        // Contacts: 62/sip:62@192.168.74.166:5060, 
        // Comma-separated contacts are legitimate with multiple registrations.
        foreach (Match match in Regex.Matches(contactsField,
            @"sips?:[^\s,;@]+@\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?(?:[;?][^\s,]*)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            result.Add(new Contact(endpoint, match.Value, "", "Registered"));
        }
        return result;
    }

    private static async Task<List<Contact>> QueryAmiAsync(AppConfig cfg, string action, CancellationToken ct)
    {
        DiagnosticLog.Info("PHONE_AMI_CONNECT_START", "action=" + action);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(cfg.AmiHost, cfg.AmiPort).WithCancellation(ct);
        DiagnosticLog.Info("PHONE_AMI_CONNECTED", "protocol=" + (cfg.AmiTls ? "TLS" : "TCP") + "; action=" + action);
        Stream stream;
        if (cfg.AmiTls)
        {
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen:false);
            await ssl.AuthenticateAsClientAsync(cfg.AmiHost, null,
            System.Security.Authentication.SslProtocols.Tls12, false).WithCancellation(ct);
            stream = ssl;
        }
        else stream = tcp.GetStream();
        using (stream)
        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen:true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen:true)
                { NewLine="\r\n", AutoFlush=true })
        {
            var banner = await reader.ReadLineAsync().WithCancellation(ct);
            if (banner is null || !banner.Contains("Asterisk", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Banner AMI non valido.");
            await WritePacketAsync(writer, new Dictionary<string,string> {
                ["Action"]="Login", ["Username"]=cfg.AmiUser,
                ["Secret"]=cfg.AmiSecret, ["Events"]="off" });
            var auth = await ReadPacketAsync(reader, ct);
            if (!auth.TryGetValue("Response", out var ok) || !ok.Equals("Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Autenticazione AMI non riuscita.");
            DiagnosticLog.Info("PHONE_AMI_LOGIN_OK", "action=" + action);
            string aid = "MLCTI-DISCOVERY-" + Guid.NewGuid().ToString("N");
            await WritePacketAsync(writer, new Dictionary<string,string> {
                ["Action"]=action, ["ActionID"]=aid });
            var contacts = new List<Contact>();
            bool accepted = false;
            string expectedComplete = action == PreferredAction ? "EndpointListComplete" : "ContactListComplete";
            for (int i=0; i<1500; i++)
            {
                var packet = await ReadPacketAsync(reader, ct);
                if (packet.TryGetValue("ActionID", out var fromAction) && fromAction != aid)
                    continue;
                if (packet.TryGetValue("Response", out var resp))
                {
                    if (!resp.Equals("Success", StringComparison.OrdinalIgnoreCase))
                    {
                        DiagnosticLog.Warn("PHONE_AMI_ACTION_REJECTED", "action=" + action + "; response=error");
                        throw new AmiActionRejectedException(action);
                    }
                    accepted = true;
                    DiagnosticLog.Info("PHONE_AMI_ACTION_ACCEPTED", "action=" + action);
                }
                var ev = packet.GetValueOrDefault("Event", "");
                if (action == PreferredAction && ev.Equals("EndpointList", StringComparison.OrdinalIgnoreCase))
                    contacts.AddRange(ParseEndpointList(packet));
                if (action == LegacyAction && ev.Equals("ContactList", StringComparison.OrdinalIgnoreCase))
                {
                    string endpoint = packet.GetValueOrDefault("Endpoint", "");
                    if (endpoint.Length == 0)
                        endpoint = packet.GetValueOrDefault("ObjectName", "").Split('/')[0];
                    contacts.Add(new Contact(endpoint, packet.GetValueOrDefault("Uri", ""),
                        packet.GetValueOrDefault("UserAgent", ""), packet.GetValueOrDefault("Status", "")));
                }
                if (ev.Equals(expectedComplete, StringComparison.OrdinalIgnoreCase))
                {
                    if (!accepted) throw new InvalidOperationException("Risposta AMI incompleta alla lettura dei contatti.");
                    DiagnosticLog.Info("PHONE_AMI_CONTACTS_COMPLETE", "action=" + action + "; total=" + contacts.Count +
                        "; matching_endpoint=" + contacts.Count(c => string.Equals(c.Endpoint, cfg.Extension, StringComparison.OrdinalIgnoreCase)));
                    return contacts;
                }
            }
            throw new InvalidOperationException("Risposta AMI " + action + " troppo lunga o incompleta.");
        }
    }

    internal static IPAddress ChooseAddress(IEnumerable<Contact> items, string ext, string phoneType)
    {
        var matched = new List<IPAddress>();
        var unknownModel = new List<IPAddress>();
        foreach (var c in items)
        {
            if (!string.Equals(c.Endpoint, ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Status.Equals("Unreachable", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryReadContactIPv4(c.Uri, out var addr) || !IsPrivateLan(addr)) continue;
            if (MatchesPhoneType(c.UserAgent, phoneType))
                matched.Add(addr);
            else if (string.IsNullOrWhiteSpace(c.UserAgent))
                unknownModel.Add(addr);
        }
        // With an explicit model, use only that phone. PJSIPShowEndpoints
        // does not include UserAgent; then only a *single* SIP registration is safe.
        var candidates = matched.Count > 0 ? matched.Distinct().ToList() : unknownModel.Distinct().ToList();
        DiagnosticLog.Info("PHONE_AMI_CANDIDATES", "model_matched=" + matched.Distinct().Count() +
            "; model_unknown=" + unknownModel.Distinct().Count() + "; unique=" + candidates.Count);
        if (candidates.Count == 0)
            throw new InvalidOperationException("Nessun contatto SIP valido dell'interno. Verificare registrazione UCM e indirizzo LAN.");
        if (candidates.Count > 1)
            throw new InvalidOperationException("Piu dispositivi sono registrati sullo stesso interno. Il client non sceglie a caso: utilizzare un'identificazione del telefono o IP manuale.");
        return candidates[0];
    }

    private static bool MatchesPhoneType(string ua, string type)
    {
        if (type == "Grandstream")
            return Regex.IsMatch(ua, @"\b(Grandstream|GRP\d+|GXP\d+|GXV\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (type == "Yealink")
            return Regex.IsMatch(ua, @"\b(Yealink|SIP-(?:T|W|CP|MP|AX)\d+[A-Z]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (type == "Snom")
            return Regex.IsMatch(ua, @"\b(snom|D717|D7[0-9]{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return false;
    }

    private static bool TryReadContactIPv4(string contactUri, out IPAddress addr)
    {
        addr = IPAddress.None;
        // Example: sip:62@192.168.74.25:5060;ob. Do not trust an unrelated hostname.
        var match = Regex.Match(contactUri, @"^sips?:[^@\s]+@(?<ip>\d{1,3}(?:\.\d{1,3}){3})(?::\d{1,5})?(?:[;?].*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !IPAddress.TryParse(match.Groups["ip"].Value, out var parsed) || parsed is null)
            return false;
        if (parsed.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(parsed))
            return false;
        addr = parsed;
        return true;
    }

    private static bool IsPrivateLan(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 ||
            b[0] == 192 && b[1] == 168;
    }

    private static async Task WritePacketAsync(StreamWriter writer, IReadOnlyDictionary<string,string> fields)
    {
        foreach (var (name, value) in fields)
        {
            if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("Valore AMI non valido.");
            await writer.WriteLineAsync(name + ": " + value);
        }
        await writer.WriteLineAsync();
        await writer.FlushAsync();
    }

    private static async Task<Dictionary<string,string>> ReadPacketAsync(StreamReader reader, CancellationToken ct)
    {
        var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for (int i=0; i<300; i++)
        {
            var line = await reader.ReadLineAsync().WithCancellation(ct) ?? throw new IOException("Connessione AMI interrotta.");
            if (line.Length == 0) return map;
            int index = line.IndexOf(':');
            if (index>0) map[line.Substring(0,index).Trim()] = line.Substring(index+1).Trim();
        }
        throw new IOException("Pacchetto AMI di dimensioni inattese.");
    }

    internal static bool SelfTest()
    {
        var c = new[] {
            new Contact("62", "sip:62@192.168.74.95:5060;ob", "Grandstream GRP2613", "Reachable"),
            new Contact("62", "sip:62@192.168.74.96:5060", "Wave Mobile", "Reachable"),
            new Contact("63", "sip:63@192.168.74.97:5060", "Grandstream GRP2613", "Reachable")
        };
        if (ChooseAddress(c, "62", "Grandstream").ToString() != "192.168.74.95") return false;
        var parsed = ParseEndpointContacts("62", "62/sip:62@192.168.74.166:5060, ");
        if (parsed.Count != 1 || ChooseAddress(parsed, "62", "Grandstream").ToString() != "192.168.74.166")
            return false;
        var multiple = ParseEndpointContacts("62", "62/sip:62@192.168.74.166:5060, 62/sip:62@192.168.74.167:5070,");
        if (multiple.Count != 2) return false;
        try { ChooseAddress(multiple, "62", "Grandstream"); return false; }
        catch (InvalidOperationException) { }
        var snom = new List<Contact> { new("62", "sip:62@192.168.74.40:5060", "snom D717", "Reachable") };
        if (ChooseAddress(snom, "62", "Snom").ToString() != "192.168.74.40") return false;
        try { ChooseAddress(c, "62", "Snom"); return false; }
        catch (InvalidOperationException) { }
        try { ChooseAddress(c, "62", "Yealink"); return false; }
        catch (InvalidOperationException) { }
        return true;
    }
}
