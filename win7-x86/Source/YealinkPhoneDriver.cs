using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;

namespace MedialinkCTIClient;

/// <summary>
/// Generic Yealink direct CTI over the documented Action URI HTTPS/HTTP GET interface.
/// This is NOT Action URL and does not require a PC-side HTTP listener or SIP NOTIFY.
/// Supported commands can vary by model/firmware; AMI still confirms call state.
/// No credentials, phone numbers, query strings or HTTP bodies are logged.
/// </summary>
internal sealed class YealinkPhoneDriver
{
    private static bool IsPrivatePhone(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
    }

    private static string ValidDestination(string? value)
    {
        if (value is null || !PhoneNumbers.ValidDialString(value))
            throw new InvalidOperationException("Yealink: numero non valido.");
        return value;
    }

    private static string ValidDeviceCallId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 12 || !value.All(LegacyCompat.IsAsciiDigit))
            throw new InvalidOperationException("Yealink: identificativo chiamata del telefono non valido.");
        return value;
    }

    internal static Uri BuildUri(IPAddress phone, AppConfig config, string operation, string? value = null)
    {
        if (!IsPrivatePhone(phone))
            throw new InvalidOperationException("Yealink CTI: consentiti solo IPv4 privati del telefono.");
        // 'Automatico' is HTTPS-only. No silent downgrade to HTTP with web credentials.
        string scheme = config.PhoneCtiProtocol == "HTTP" ? "http" : "https";
        int port = config.PhoneCtiPort > 0 ? config.PhoneCtiPort : scheme == "https" ? 443 : 80;
        if (port is < 1 or > 65535) throw new InvalidOperationException("Porta Yealink non valida.");
        string key = operation switch
        {
            "dial" => "number=" + Uri.EscapeDataString(ValidDestination(value)),
            "answer" => "ANSWER",
            "end" => "CALLEND",
            "hold" => "F_HOLD",
            // Unhold is call-specific, and requires a Yealink DEVICE call-id (not AMI uniqueid).
            "unhold" => "UNHOLD:" + ValidDeviceCallId(value),
            "blind-transfer" => "BTrans=" + Uri.EscapeDataString(ValidDestination(value)),
            "attended-transfer" => "ATrans=" + Uri.EscapeDataString(ValidDestination(value)),
            "status" => "",
            _ => throw new InvalidOperationException("Comando Yealink non supportato.")
        };
        string query = operation == "status" ? "phonecfg=get&fw=1" : "key=" + key;
        return new UriBuilder(scheme, phone.ToString(), port, "/servlet") { Query = query }.Uri;
    }

    private static async Task<int> SendAsync(IPAddress phone, AppConfig cfg, string operation,
        string? value, CancellationToken cancellation)
    {
        Uri uri = BuildUri(phone, cfg, operation, value);
        bool hasUser = !string.IsNullOrWhiteSpace(cfg.YealinkWebUsername);
        bool hasPassword = !string.IsNullOrWhiteSpace(cfg.YealinkWebPassword);
        if (hasUser != hasPassword)
            throw new InvalidOperationException("Yealink: inserire sia utente sia password web, oppure lasciare entrambi vuoti se il telefono autorizza per IP.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, PreAuthenticate = false };
        if (hasUser)
        {
            var credentials = new NetworkCredential(cfg.YealinkWebUsername, cfg.YealinkWebPassword);
            var cache = new CredentialCache();
            var origin = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
            cache.Add(origin, "Digest", credentials);
            // Basic is only allowed inside TLS. Explicit HTTP uses Digest exclusively.
            if (uri.Scheme == Uri.UriSchemeHttps) cache.Add(origin, "Basic", credentials);
            handler.Credentials = cache;
        }
        if (uri.Scheme == Uri.UriSchemeHttp)
            DiagnosticLog.Warn("YEALINK_HTTP_EXPLICIT", "credentials=digest_only; private_phone=true");
        handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
        {
            if (cert is null || request.RequestUri is not { } actual ||
                actual.Scheme != Uri.UriSchemeHttps ||
                !actual.Host.Equals(phone.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !IsPrivatePhone(phone)) return false;
            if (errors == SslPolicyErrors.None) return true;
            if (!cfg.AllowUntrustedPhoneCertificate) return false;
            DiagnosticLog.Warn("YEALINK_TLS_LAB_EXCEPTION_USED", "scope=private_phone; errors=" + errors);
            return true;
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.ConnectionClose = true;
        var stopwatch = Stopwatch.StartNew();
        DiagnosticLog.Info("YEALINK_HTTP_START", "operation=" + operation + "; protocol=" + uri.Scheme +
            "; port=" + uri.Port + "; auth=" + (hasUser ? "challenge" : "none"));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            int code = (int)response.StatusCode;
            DiagnosticLog.Info("YEALINK_HTTP_RESULT", "operation=" + operation +
                "; http=" + code + "; elapsed_ms=" + stopwatch.ElapsedMilliseconds);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Yealink: autorizzazione rifiutata (HTTP " + code +
                    "). Controllare credenziali web, Action URI e IP autorizzato sul telefono.");
            if (code >= 300 && code < 400)
                throw new InvalidOperationException("Yealink CTI: redirect HTTP " + code +
                    " non seguito. Controllare HTTPS, certificato e pagina di accesso web.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Yealink CTI: HTTP " + code +
                    ". Verificare l'abilitazione Action URI, l'indirizzo IP del PC e la compatibilità firmware.");
            // 2xx means only transport acceptance. AMI must confirm state, no extra retries
            // for state-changing commands because a response may arrive after execution.
            return code;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            DiagnosticLog.Warn("YEALINK_HTTP_TIMEOUT", "operation=" + operation + "; elapsed_ms=" + stopwatch.ElapsedMilliseconds);
            throw new InvalidOperationException("Yealink: timeout HTTP; il comando potrebbe essere stato eseguito. Nessun reinvio automatico.");
        }
        catch (HttpRequestException)
        {
            // Do not log exception.Message: some .NET handlers include the request URL / dialed number.
            DiagnosticLog.Warn("YEALINK_HTTP_TRANSPORT_FAILED", "operation=" + operation + "; scheme=" + uri.Scheme);
            throw new InvalidOperationException("Yealink: connessione web non riuscita. Verificare IP/porta, certificato e log.");
        }
    }

    public Task<int> DialAsync(IPAddress phone, AppConfig cfg, string number, CancellationToken ct = default) =>
        SendAsync(phone, cfg, "dial", number, ct);
    public Task<int> AnswerAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default) =>
        SendAsync(phone, cfg, "answer", null, ct);
    public Task<int> EndAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default) =>
        SendAsync(phone, cfg, "end", null, ct);
    // Prepared for explicit hold/transfer controls when AMI state and Yealink device call-id are available.
    public Task<int> HoldAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default) =>
        SendAsync(phone, cfg, "hold", null, ct);
    public Task<int> ResumeAsync(IPAddress phone, AppConfig cfg, string deviceCallId, CancellationToken ct = default) =>
        SendAsync(phone, cfg, "unhold", deviceCallId, ct);
    public Task<int> BlindTransferAsync(IPAddress phone, AppConfig cfg, string number, CancellationToken ct = default) =>
        SendAsync(phone, cfg, "blind-transfer", number, ct);
    public Task<int> AttendedTransferAsync(IPAddress phone, AppConfig cfg, string number, CancellationToken ct = default) =>
        SendAsync(phone, cfg, "attended-transfer", number, ct);

    public async Task<string> DiagnoseAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default)
    {
        int code = await SendAsync(phone, cfg, "status", null, ct);
        DiagnosticLog.Info("YEALINK_DIAGNOSTIC_OK", "read_only=true; http=" + code);
        return "Yealink: endpoint web raggiungibile (HTTP " + code +
            "). Richiesta di sola lettura. Verificare Action URI e IP PC autorizzato " +
            "prima delle chiamate. Il test non effettua comandi telefonici.";
    }

    internal static bool SelfTest()
    {
        var ip = IPAddress.Parse("192.168.74.25");
        var cfg = new AppConfig { PhoneCtiProtocol = "Automatico", PhoneCtiPort = 0 };
        var dial = BuildUri(ip, cfg, "dial", "+393479784437");
        var answer = BuildUri(ip, cfg, "answer");
        var end = BuildUri(ip, cfg, "end");
        var hold = BuildUri(ip, cfg, "hold");
        var resume = BuildUri(ip, cfg, "unhold", "33093");
        var blind = BuildUri(ip, cfg, "blind-transfer", "62");
        var attended = BuildUri(ip, cfg, "attended-transfer", "62");
        var status = BuildUri(ip, cfg, "status");
        var http = BuildUri(ip, new AppConfig { PhoneCtiProtocol = "HTTP" }, "answer");
        bool publicRejected = false, invalidDialRejected = false, invalidCallIdRejected = false;
        try { _ = BuildUri(IPAddress.Parse("8.8.8.8"), cfg, "answer"); }
        catch (InvalidOperationException) { publicRejected = true; }
        try { _ = BuildUri(ip, cfg, "dial", "62\r\nX-Test"); }
        catch (InvalidOperationException) { invalidDialRejected = true; }
        try { _ = BuildUri(ip, cfg, "unhold", "1@2"); }
        catch (InvalidOperationException) { invalidCallIdRejected = true; }
        return dial.Scheme == "https" && dial.Port == 443 && dial.AbsolutePath == "/servlet" &&
            Uri.UnescapeDataString(dial.Query) == "?key=number=+393479784437" &&
            answer.Query == "?key=ANSWER" && end.Query == "?key=CALLEND" &&
            hold.Query == "?key=F_HOLD" && Uri.UnescapeDataString(resume.Query) == "?key=UNHOLD:33093" &&
            blind.Query == "?key=BTrans=62" && attended.Query == "?key=ATrans=62" &&
            status.Query == "?phonecfg=get&fw=1" && http.Scheme == "http" && http.Port == 80 &&
            publicRejected && invalidDialRejected && invalidCallIdRejected;
    }
}
