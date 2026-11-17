using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;

namespace MedialinkCTIClient;

/// <summary>
/// Snom D-Series direct CTI using HTTP Digest; NO Snom Action URI, callback listener or PBX write.
/// URI patterns were verified in the separate D717 test. A successful HTTP response
/// means the command was transmitted, NOT that AMI confirmed the call transition.
/// Never logs HTTP query strings, credentials, raw response body or telephone numbers.
/// </summary>
internal sealed class SnomPhoneDriver
{
    private static bool IsPrivatePhone(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    internal static Uri BuildUri(IPAddress phone, AppConfig config, string operation, string? destination = null)
    {
        if (!IsPrivatePhone(phone)) throw new InvalidOperationException("Snom CTI: richiesto indirizzo IPv4 privato.");
        string scheme = config.PhoneCtiProtocol == "HTTPS" ? "https" : "http";
        int port = config.PhoneCtiPort != 0 ? config.PhoneCtiPort : scheme == "https" ? 443 : 80;
        if (port is < 1 or > 65535) throw new InvalidOperationException("Porta Web Snom non valida.");
        string path;
        string query;
        switch (operation)
        {
            case "dial":
                if (destination is null || !PhoneNumbers.ValidDialString(destination))
                    throw new InvalidOperationException("Numero non valido per il Click-to-Dial Snom.");
                path = "/index.htm";
                query = "number=" + Uri.EscapeDataString(destination);
                break;
            case "answer":
                path = "/command.htm"; query = "key=SPEAKER"; break;
            case "end":
                // CANCEL ends the ringing/connected call; ONHOOK also returns the
                // physical Snom speakerphone to the idle state (not just AMI leg).
                // Snom D7xx documents semicolon-separated key events.
                path = "/command.htm"; query = "key=CANCEL;ONHOOK"; break;
            case "hold":
            case "unhold":
                path = "/command.htm"; query = "key=F_HOLD"; break;
            case "status":
                path = "/index.htm"; query = ""; break;
            default:
                throw new InvalidOperationException("Operazione CTI Snom non supportata.");
        }
        return new UriBuilder(scheme, phone.ToString(), port, path) { Query = query }.Uri;
    }

    private static bool IsSafeRedirect(HttpResponseMessage response, Uri source)
    {
        var loc = response.Headers.Location;
        if (loc is null) return false;
        Uri dest;
        try { dest = loc.IsAbsoluteUri ? loc : new Uri(source, loc); }
        catch (UriFormatException) { return false; }
        // A login redirect or an unexpected host is NOT a successful CTI operation.
        return dest.Scheme == source.Scheme && dest.Host.Equals(source.Host, StringComparison.OrdinalIgnoreCase) &&
               dest.Port == source.Port &&
               !dest.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) &&
               !dest.AbsolutePath.Contains("auth", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> SendAsync(IPAddress phone, AppConfig cfg, string operation,
        string? number, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.SnomWebUsername) || string.IsNullOrWhiteSpace(cfg.SnomWebPassword))
            throw new InvalidOperationException("Snom D-Series: impostare utente e password web Digest nelle impostazioni.");
        var uri = BuildUri(phone, cfg, operation, number);
        // Whitelist HTTP Digest only; never silently downgrade to Basic authentication.
        var digest = new CredentialCache();
        digest.Add(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), "Digest",
            new NetworkCredential(cfg.SnomWebUsername, cfg.SnomWebPassword));
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PreAuthenticate = false,
            Credentials = digest
        };
        if (cfg.AllowUntrustedPhoneCertificate)
        {
            DiagnosticLog.Warn("SNOM_TLS_LAB_EXCEPTION_ACTIVE", "scope=selected_private_phone_only");
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                bool allowed = cert is not null && request.RequestUri is { } requestUri &&
                    requestUri.Scheme == Uri.UriSchemeHttps &&
                    requestUri.Host.Equals(phone.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    IsPrivatePhone(phone);
                if (allowed && errors != SslPolicyErrors.None)
                    DiagnosticLog.Warn("SNOM_TLS_LAB_CERT_EXCEPTION_USED", "errors=" + errors);
                return allowed;
            };
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.ConnectionClose = true;
        DiagnosticLog.Info("SNOM_HTTP_START", "method=GET; operation=" + operation +
            "; scheme=" + uri.Scheme + "; port=" + uri.Port + "; auth=digest");
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)response.StatusCode;
            DiagnosticLog.Info("SNOM_HTTP_HEADERS", "operation=" + operation +
                "; status=" + code + "; elapsed_ms=" + sw.ElapsedMilliseconds);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Snom HTTP Digest: credenziali web rifiutate (HTTP " + code + ").");
            if (code is >= 300 and < 400)
            {
                bool safe = IsSafeRedirect(response, uri);
                DiagnosticLog.Info("SNOM_HTTP_REDIRECT", "operation=" + operation + "; status=" + code +
                    "; same_phone_safe=" + safe.ToString().ToLowerInvariant());
                if (!safe)
                    throw new InvalidOperationException("Snom CTI: reindirizzamento non riconosciuto; comando non confermato. Vedere SNOM_HTTP_REDIRECT.");
                // Snom D717 returns HTTP 302 for accepted Click-to-Dial. Do NOT follow redirect
                // or send Digest credentials to a different endpoint. AMI confirms call state.
                return code;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Snom CTI: HTTP " + code + ".");
            return code;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            DiagnosticLog.Warn("SNOM_HTTP_TIMEOUT", "operation=" + operation + "; elapsed_ms=" + sw.ElapsedMilliseconds);
            throw new InvalidOperationException("Snom CTI: timeout HTTP Digest.");
        }
        catch (HttpRequestException e)
        {
            DiagnosticLog.Error("SNOM_HTTP_TRANSPORT_ERROR", e);
            throw new InvalidOperationException("Snom CTI: connessione web fallita. Controllare porta/protocollo e log.");
        }
    }

    public async Task DialAsync(IPAddress phone, AppConfig cfg, string number, CancellationToken ct = default)
    {
        _ = await SendAsync(phone, cfg, "dial", number, ct);
    }
    public async Task AnswerAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default)
    {
        // A single documented key-event, not an undocumented index.htm?accept=1.
        // Only issue for an AMI-confirmed incoming/ringing state; AMI Up confirms success.
        DiagnosticLog.Info("SNOM_ANSWER_METHOD", "method=remote_key; key=SPEAKER; confirmation=AMI");
        _ = await SendAsync(phone, cfg, "answer", null, ct);
    }
    public async Task EndAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default)
    {
        // An HTTP 200/302 confirms transport acceptance, not the resulting
        // audio or speaker state. Do not log a "phone idle" assertion here.
        DiagnosticLog.Info("SNOM_END_METHOD", "method=remote_keys; sequence=CANCEL_ONHOOK; confirmation=physical_phone");
        _ = await SendAsync(phone, cfg, "end", null, ct);
    }
    // Prepared for future Hold/Unhold buttons. Actual call state must be verified by AMI Hold/Unhold.
    public async Task HoldAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default)
    {
        _ = await SendAsync(phone, cfg, "hold", null, ct);
    }
    public async Task UnholdAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default)
    {
        _ = await SendAsync(phone, cfg, "unhold", null, ct);
    }

    public async Task<string> DiagnoseAsync(IPAddress phone, AppConfig cfg, CancellationToken ct = default)
    {
        int status = await SendAsync(phone, cfg, "status", null, ct);
        DiagnosticLog.Info("SNOM_DIAGNOSTIC_OK", "auth=digest; status=" + status);
        return "Snom D-Series: interfaccia web raggiungibile (HTTP " + status +
            ") tramite Digest. Nessuna chiamata effettuata durante la diagnostica.";
    }

    internal static bool SelfTest()
    {
        var cfg = new AppConfig { PhoneCtiProtocol = "Automatico", PhoneCtiPort = 0 };
        var ip = IPAddress.Parse("192.168.74.40");
        Uri dial = BuildUri(ip, cfg, "dial", "62");
        Uri answer = BuildUri(ip, cfg, "answer");
        Uri end = BuildUri(ip, cfg, "end");
        Uri hold = BuildUri(ip, cfg, "hold");
        Uri unhold = BuildUri(ip, cfg, "unhold");
        Uri status = BuildUri(ip, cfg, "status");
        bool rejectedPublic = false;
        try { BuildUri(IPAddress.Parse("8.8.8.8"), cfg, "answer"); }
        catch (InvalidOperationException) { rejectedPublic = true; }
        bool rejectedBadNumber = false;
        try { BuildUri(ip, cfg, "dial", "1\r\nX-Test"); }
        catch (InvalidOperationException) { rejectedBadNumber = true; }
        return dial.AbsolutePath == "/index.htm" && dial.Query == "?number=62" &&
            answer.AbsolutePath == "/command.htm" && answer.Query == "?key=SPEAKER" &&
            end.AbsolutePath == "/command.htm" &&
            end.Query == "?key=CANCEL;ONHOOK" && hold.Query == "?key=F_HOLD" &&
            unhold.Query == "?key=F_HOLD" && status.Query == "" &&
            dial.Scheme == "http" && dial.Port == 80 && rejectedPublic && rejectedBadNumber;
    }
}
