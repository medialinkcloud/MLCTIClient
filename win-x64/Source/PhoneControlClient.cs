using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;

namespace MedialinkCTIClient;

/// <summary>CTI remote control is separate from SIP AutoAnswer for outbound Originate.
/// All diagnostics omit passwords, URL query parameters, numbers and raw HTTP bodies.</summary>
internal sealed class PhoneControlClient
{
    private sealed record PhoneTarget(IPAddress Address, string Source);

    private static bool IsPrivateIPv4(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(addr)) return false;
        var b = addr.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    private static async Task<PhoneTarget> ResolveTargetAsync(AppConfig config, CancellationToken cancellation)
    {
        if (config.AutoDetectPhoneAddress)
        {
            if (!string.IsNullOrWhiteSpace(config.PhoneAddress))
                DiagnosticLog.Warn("CTI_MANUAL_IP_IGNORED", "reason=auto_detection_enabled");
            DiagnosticLog.Info("CTI_IP_RESOLUTION_START", "mode=AMI; action=PJSIPShowEndpoints; fallback=PJSIPShowContacts");
            var resolved = await new AmiPhoneDiscovery().ResolveAsync(config, cancellation);
            DiagnosticLog.Info("CTI_IP_RESOLUTION_OK", "mode=AMI; host_suffix=" + resolved.GetAddressBytes()[3]);
            return new PhoneTarget(resolved, "AMI");
        }
        DiagnosticLog.Info("CTI_IP_RESOLUTION_START", "mode=manual");
        if (!IPAddress.TryParse(config.PhoneAddress, out var addr) || !IsPrivateIPv4(addr))
        {
            DiagnosticLog.Warn("CTI_MANUAL_IP_INVALID");
            throw new InvalidOperationException("Inserire un IPv4 privato valido del telefono in modalita manuale.");
        }
        DiagnosticLog.Info("CTI_IP_RESOLUTION_OK", "mode=manual; host_suffix=" + addr.GetAddressBytes()[3]);
        return new PhoneTarget(addr, "manual");
    }

    private static string SafeKind(string? value) => value?.ToLowerInvariant() switch
    {
        "success" => "success", "error" => "error", "available" => "available",
        "busy" => "busy", "complete" => "complete", "failed" => "failed",
        _ => "other"
    };

    private static (bool Accepted, string Status, string BodyKind) ParseCtiResponse(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.String)
            return (false, "missing", "unknown");
        var status = SafeKind(response.GetString());
        string body = "absent";
        bool allowedBody = true;
        if (root.TryGetProperty("body", out var bodyValue))
        {
            if (bodyValue.ValueKind == JsonValueKind.False) { body = "false"; allowedBody = false; }
            else if (bodyValue.ValueKind == JsonValueKind.True) body = "true";
            else if (bodyValue.ValueKind == JsonValueKind.String) body = SafeKind(bodyValue.GetString());
            else body = bodyValue.ValueKind.ToString().ToLowerInvariant();
        }
        return (status == "success" && allowedBody, status, body);
    }

    // Never display a Location URL: it may include passcode, query strings, or session tokens.
    // We only log pre-defined classifications of the destination.
    private static (bool SamePhone, string Scheme, int Port, string PathKind) ClassifyRedirect(
        Uri? location, Uri current, IPAddress phoneIp)
    {
        if (location is null) return (false, "missing", 0, "missing");
        Uri uri;
        try { uri = location.IsAbsoluteUri ? location : new Uri(current, location); }
        catch (UriFormatException) { return (false, "invalid", 0, "invalid"); }
        bool samePhone = uri.Host.Equals(phoneIp.ToString(), StringComparison.OrdinalIgnoreCase);
        string scheme = uri.Scheme == Uri.UriSchemeHttps ? "https" :
            uri.Scheme == Uri.UriSchemeHttp ? "http" : "other";
        var path = uri.AbsolutePath.ToLowerInvariant();
        string kind = path.Equals(current.AbsolutePath, StringComparison.OrdinalIgnoreCase)
            ? "cti_endpoint" : path.Contains("login") || path.Contains("signin") ||
            path.Contains("auth") || path.Contains("index") || path == "/"
            ? "web_login_or_ui" : "other";
        return (samePhone, scheme, uri.Port, kind);
    }

    private static Uri CreatePhoneUri(PhoneTarget target, string scheme, int port,
        string function, string parameters)
    {
        if (!IsPrivateIPv4(target.Address))
            throw new InvalidOperationException("Indirizzo CTI non privato: comando bloccato.");
        if (port < 1 || port > 65535) throw new InvalidOperationException("Porta CTI telefono non valida.");
        var builder = new UriBuilder(scheme, target.Address.ToString(), port, "/cgi-bin/" + function)
            { Query = parameters };
        return builder.Uri;
    }

    private static async Task<(bool Accepted, string Status, string BodyKind)> SendCtiAsync(
        PhoneTarget target, AppConfig config, string operation, CancellationToken cancellation)
    {
        if (operation != "status") DemoLicenseGate.Require();
        string password = config.PhoneControlPassword;
        if (string.IsNullOrWhiteSpace(password))
        {
            DiagnosticLog.Warn("CTI_PASSWORD_NOT_CONFIGURED");
            throw new InvalidOperationException("Passcode CTI Grandstream non configurato.");
        }
        string function = operation == "status" ? "api-get_phone_status" : "api-phone_operation";
        // Match the request parameter order observed in the successful xtelsio/GRP2613 trace.
        // Keep passcode out of logs; never redirect the original URL to another device.
        string parameters = operation == "status"
            ? "passcode=" + Uri.EscapeDataString(password)
            : "cmd=" + Uri.EscapeDataString(operation) + "&passcode=" + Uri.EscapeDataString(password);

        string selected = config.PhoneCtiProtocol is "HTTP" or "HTTPS" ? config.PhoneCtiProtocol : "Automatico";
        // Never send a CTI passcode to HTTP when the user selected Automatico.
        // Xtelsio reaches the GRP2613 over HTTPS after port-80 redirects; use HTTPS directly.
        // This dedicated HTTP/1.1 TLS transport isolates a known .NET HttpClient headers timeout.
        if (selected != "HTTP")
        {
            int httpsPort = config.PhoneCtiPort > 0 ? config.PhoneCtiPort : 443;
            DiagnosticLog.Info("GRP_CTI_HTTPS_DIRECT", "action=" + operation + "; port=" + httpsPort +
                "; transport=SslStream; mode=" + selected);
            var result = await GrandstreamDirectTls.RequestAsync(target.Address, httpsPort,
                config.AllowUntrustedPhoneCertificate, "/cgi-bin/" + function + "?" + parameters,
                operation, cancellation);
            (bool accepted, string status, string bodyKind) parsed;
            try { parsed = ParseCtiResponse(result.Body); }
            catch (JsonException)
            {
                DiagnosticLog.Warn("CTI_INVALID_RESPONSE_FORMAT", "action=" + operation + "; response=non_json");
                throw new InvalidOperationException("Il telefono ha risposto senza JSON CTI. Nessuna azione ripetuta.");
            }
            DiagnosticLog.Info("CTI_RESPONSE_PARSED", "action=" + operation + "; response=" + parsed.status +
                "; body_kind=" + parsed.bodyKind + "; accepted=" + parsed.accepted);
            if (!parsed.accepted)
                throw new InvalidOperationException("Telefono CTI: comando non accettato (" + parsed.status +
                    "/" + parsed.bodyKind + "). Nessuna azione ripetuta.");
            return (parsed.accepted, parsed.status, parsed.bodyKind);
        }
        DiagnosticLog.Warn("GRP_CTI_HTTP_INSECURE_SELECTED", "action=" + operation + "; private_ip_only=true");
        string scheme = "http";
        int port = config.PhoneCtiPort > 0 ? config.PhoneCtiPort : 80;
        var uri = CreatePhoneUri(target, scheme, port, function, parameters);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        if (config.AllowUntrustedPhoneCertificate)
        {
            DiagnosticLog.Warn("CTI_TLS_LAB_EXCEPTION_ACTIVE", "scope=phone_private_ip_only");
            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
            {
                // This opt-in exception is ONLY for the exact private phone address, never for arbitrary redirects.
                bool accepted = certificate is not null && request.RequestUri is { } requestUri &&
                    requestUri.Scheme == Uri.UriSchemeHttps &&
                    requestUri.Host.Equals(target.Address.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    IsPrivateIPv4(target.Address);
                if (accepted && errors != SslPolicyErrors.None)
                    DiagnosticLog.Warn("CTI_TLS_LAB_CERT_EXCEPTION_USED", "device=phone; errors=" + errors);
                return accepted;
            };
        }
        // Separate the header deadline from body reading. An HTTPS connection can succeed
        // and still stall before sending HTTP response headers (observed on GRP2613).
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var sw = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string phase = "headers";
            DiagnosticLog.Info("CTI_HTTP_START", "method=GET; action=" + operation +
                "; source=" + target.Source + "; scheme=" + uri.Scheme + "; port=" + uri.Port +
                "; attempt=" + (attempt + 1));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.ConnectionClose = true;
                request.Headers.Accept.ParseAdd("application/json");
                using var headerLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                headerLimit.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await http.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, headerLimit.Token);
                int code = (int)response.StatusCode;
                DiagnosticLog.Info("CTI_HTTP_HEADERS_RECEIVED", "action=" + operation +
                    "; scheme=" + uri.Scheme + "; status=" + code +
                    "; elapsed_ms=" + sw.ElapsedMilliseconds);

                DiagnosticLog.Info("CTI_HTTP_RESPONSE", "action=" + operation + "; status=" + code +
                    "; elapsed_ms=" + sw.ElapsedMilliseconds +
                    "; content_type=" + (response.Content.Headers.ContentType?.MediaType == "application/json" ? "json" : "other"));
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    var redirect = ClassifyRedirect(response.Headers.Location, uri, target.Address);
                    DiagnosticLog.Warn("CTI_HTTP_REDIRECT", "status=" + code +
                        "; same_phone=" + redirect.SamePhone.ToString().ToLowerInvariant() +
                        "; target_scheme=" + redirect.Scheme + "; target_port=" + redirect.Port +
                        "; target_kind=" + redirect.PathKind);
                    // Never pass the passcode to a redirected URL. Instead rebuild our own original CTI path.
                    if (selected == "Automatico" && attempt == 0 && uri.Scheme == "http" &&
                        redirect.SamePhone && redirect.Scheme == "https" && redirect.Port is >= 1 and <= 65535)
                    {
                        DiagnosticLog.Info("CTI_HTTPS_RETRY_SAME_PHONE", "http_redirect=" + code +
                            "; target_path_used=original_cti_endpoint");
                        uri = CreatePhoneUri(target, "https", redirect.Port, function, parameters);
                        continue;
                    }
                    if (!redirect.SamePhone || redirect.Scheme is "other" or "missing" or "invalid")
                        throw new InvalidOperationException("CTI HTTP " + code +
                            ": redirect a destinazione diversa o sconosciuta; bloccato per proteggere il passcode. Verificare il log CTI_HTTP_REDIRECT.");
                    if (redirect.PathKind == "web_login_or_ui")
                        throw new InvalidOperationException("CTI HTTP " + code +
                            ": reindirizzamento alla pagina web/login del telefono. Verificare Action URI, PC autorizzato, credenziali e protocollo; vedere log.");
                    throw new InvalidOperationException("CTI HTTP " + code +
                        ": redirect non seguito. Impostare HTTPS nelle opzioni o leggere CTI_HTTP_REDIRECT nel log.");
                }
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Telefono CTI: HTTP " + code +
                        ". Verificare protocollo web, Action URI e permessi del PC.");
                phase = "body";
                DiagnosticLog.Info("CTI_HTTP_BODY_START", "action=" + operation +
                    "; content_length_set=" + response.Content.Headers.ContentLength.HasValue);
                using var bodyLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                bodyLimit.CancelAfter(TimeSpan.FromSeconds(8));
                string body = await response.Content.ReadAsStringAsync(bodyLimit.Token);
                DiagnosticLog.Info("CTI_HTTP_BODY_RECEIVED", "action=" + operation +
                    "; bytes=" + System.Text.Encoding.UTF8.GetByteCount(body) +
                    "; elapsed_ms=" + sw.ElapsedMilliseconds);
                (bool accepted, string status, string bodyKind) parsed;
                try { parsed = ParseCtiResponse(body); }
                catch (JsonException)
                {
                    DiagnosticLog.Warn("CTI_INVALID_RESPONSE_FORMAT", "action=" + operation + "; response=non_json");
                    throw new InvalidOperationException("Il telefono non ha restituito JSON CTI: possibile pagina di login o proxy (vedere log).");
                }
                DiagnosticLog.Info("CTI_RESPONSE_PARSED", "action=" + operation + "; response=" + parsed.status +
                    "; body_kind=" + parsed.bodyKind + "; accepted=" + parsed.accepted);
                if (!parsed.accepted)
                    throw new InvalidOperationException("Telefono CTI: comando non accettato (" + parsed.status +
                        "/" + parsed.bodyKind + "). Verificare configurazione di Remote Control.");
                return (parsed.accepted, parsed.status, parsed.bodyKind);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                DiagnosticLog.Warn(phase == "headers" ? "CTI_HTTP_HEADERS_TIMEOUT" : "CTI_HTTP_BODY_TIMEOUT",
                    "action=" + operation + "; scheme=" + uri.Scheme + "; port=" + uri.Port +
                    "; elapsed_ms=" + sw.ElapsedMilliseconds);
                throw new InvalidOperationException(phase == "headers"
                    ? "Timeout CTI: il telefono non ha inviato gli header HTTP. Controllare log CTI_HTTP_HEADERS_TIMEOUT."
                    : "Timeout CTI: il telefono ha risposto ma non ha completato il corpo JSON. Controllare log CTI_HTTP_BODY_TIMEOUT.");
            }
            catch (HttpRequestException ex)
            {
                DiagnosticLog.Error("CTI_HTTP_TRANSPORT_FAILED", ex);
                if (ex.InnerException is AuthenticationException)
                    throw new InvalidOperationException("Connessione HTTPS CTI: certificato non verificabile. " +
                        "Installare il certificato attendibile oppure, solo in LAB su LAN fidata, abilitare l'eccezione dedicata.");
                throw new InvalidOperationException("Connessione CTI al telefono fallita. Controllare IP, protocollo e porta.");
            }
        }
        throw new InvalidOperationException("Numero eccessivo di reindirizzamenti CTI; controllare log.");
    }

    public async Task AnswerAsync(AppConfig config, CancellationToken cancellation = default)
    {
        DemoLicenseGate.Require();
        DiagnosticLog.Info("CTI_ANSWER_REQUEST", "type=" + config.PhoneType + "; mode=" +
            (config.AutoDetectPhoneAddress ? "AMI" : "manual"));
        if (config.PhoneType == "Altro")
            throw new InvalidOperationException("Controllo remoto non configurato per questo tipo di telefono.");
        var target = await ResolveTargetAsync(config, cancellation);
        if (config.PhoneType == "Grandstream")
        {
            await SendCtiAsync(target, config, "acceptcall", cancellation);
        }
        else if (config.PhoneType == "Snom")
        {
            await new SnomPhoneDriver().AnswerAsync(target.Address, config, cancellation);
        }
        else if (config.PhoneType == "Yealink")
        {
            // Same device-target resolution as Snom, HTTPS Action URI, AMI-confirmed result.
            _ = await new YealinkPhoneDriver().AnswerAsync(target.Address, config, cancellation);
        }
        else
            throw new InvalidOperationException("Tipo di telefono non supportato.");
        DiagnosticLog.Info("CTI_ANSWER_COMMAND_SENT", "type=" + config.PhoneType + "; awaiting_ami_up=true");
    }

    // Phone-specific direct click-to-dial for Snom/Yealink. Grandstream retains AMI Originate.
    public async Task DialSnomAsync(AppConfig config, string number, CancellationToken cancellation = default)
    {
        DemoLicenseGate.Require();
        if (config.PhoneType != "Snom")
            throw new InvalidOperationException("La composizione diretta HTTP Digest è disponibile soltanto nel profilo Snom.");
        var target = await ResolveTargetAsync(config, cancellation);
        await new SnomPhoneDriver().DialAsync(target.Address, config, number, cancellation);
    }

    public async Task DialYealinkAsync(AppConfig config, string number, CancellationToken cancellation = default)
    {
        DemoLicenseGate.Require();
        if (config.PhoneType != "Yealink")
            throw new InvalidOperationException("Composizione diretta disponibile soltanto nel profilo Yealink.");
        var target = await ResolveTargetAsync(config, cancellation);
        _ = await new YealinkPhoneDriver().DialAsync(target.Address, config, number, cancellation);
    }

    public async Task ReleaseYealinkSpeakerAsync(AppConfig config, CancellationToken cancellation = default)
    {
        DemoLicenseGate.Require();
        if (config.PhoneType != "Yealink")
            throw new InvalidOperationException("Riaggancio dispositivo disponibile solo per Yealink.");
        var target = await ResolveTargetAsync(config, cancellation);
        _ = await new YealinkPhoneDriver().EndAsync(target.Address, config, cancellation);
    }

    /// <summary>
    /// Explicit Snom-only device release after AMI Hangup. Never used for
    /// Grandstream, Yealink, unknown profile or automatically on AMI events.
    /// </summary>
    public async Task ReleaseSnomSpeakerAsync(AppConfig config, CancellationToken cancellation = default)
    {
        DemoLicenseGate.Require();
        if (config.PhoneType != "Snom")
            throw new InvalidOperationException("Riaggancio dispositivo disponibile solo per Snom.");
        var target = await ResolveTargetAsync(config, cancellation);
        await new SnomPhoneDriver().EndAsync(target.Address, config, cancellation);
    }

    /// <summary>Non destructive reachability and credential test (never answers/ends calls).</summary>
    public async Task<string> DiagnoseAsync(AppConfig config, CancellationToken cancellation = default)
    {
        DiagnosticLog.Info("CTI_DIAGNOSTIC_BEGIN", "type=" + config.PhoneType);
        if (config.PhoneType is not ("Grandstream" or "Snom" or "Yealink"))
            return "Diagnostica diretta disponibile per Grandstream, Snom e Yealink.";
        var target = await ResolveTargetAsync(config, cancellation);
        if (config.PhoneType == "Snom")
            return await new SnomPhoneDriver().DiagnoseAsync(target.Address, config, cancellation);
        if (config.PhoneType == "Yealink")
            return await new YealinkPhoneDriver().DiagnoseAsync(target.Address, config, cancellation);
        var result = await SendCtiAsync(target, config, "status", cancellation);
        DiagnosticLog.Info("CTI_DIAGNOSTIC_OK", "device=Grandstream; mode=" + target.Source);
        return "Telefono CTI raggiungibile. Metodo IP: " + target.Source +
            ". Risposta: " + result.Status + ", stato: " + result.BodyKind +
            ". Il test non effettua chiamate. Per Rispondi verificare su chiamata entrante.";
    }

    internal static bool SelfTest()
    {
        var ok = ParseCtiResponse("{\"response\":\"success\",\"body\":true}");
        var denied = ParseCtiResponse("{\"response\":\"error\",\"body\":false}");
        var available = ParseCtiResponse("{\"response\":\"success\",\"body\":\"available\"}");
        var current = new Uri("http://192.168.74.166/cgi-bin/api-get_phone_status?test=1");
        var toHttps = ClassifyRedirect(new Uri("https://192.168.74.166/login"), current, IPAddress.Parse("192.168.74.166"));
        var toOtherHost = ClassifyRedirect(new Uri("https://192.168.74.165/login"), current, IPAddress.Parse("192.168.74.166"));
        return ok.Accepted && !denied.Accepted && available.Accepted &&
            ok.Status == "success" && denied.BodyKind == "false" &&
            toHttps.SamePhone && toHttps.Scheme == "https" &&
            toHttps.PathKind == "web_login_or_ui" && !toOtherHost.SamePhone;
    }
}
