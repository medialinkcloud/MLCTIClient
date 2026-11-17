using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace MedialinkCTIClient;

/// <summary>
/// Dedicated, direct HTTPS/HTTP1.1 CTI transport for Grandstream GRP.
/// Does not use HttpClient for TLS: separates TCP/TLS, request write and response headers.
/// Never logs complete URLs, query parameters, password, response body or cookies.
/// Doesn't retry non-idempotent operations after a timeout (answer may have succeeded).
/// </summary>
internal static class GrandstreamDirectTls
{
    internal sealed record Result(int StatusCode, string Body);

    private static int HeaderEnd(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i + 4;
        }
        return -1;
    }

    // Do not parse unbounded server input. Unexpected headers/cookies are never logged.
    private static (int Code, int? Length, bool Chunked) ParseHeaders(string text)
    {
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var statusParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2 || !statusParts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) ||
            !int.TryParse(statusParts[1], out int code))
            throw new InvalidDataException("Risposta HTTPS CTI: status HTTP non valido.");
        int? length = null;
        bool chunked = false;
        foreach (var line in lines.Skip(1))
        {
            int pos = line.IndexOf(':');
            if (pos <= 0) continue;
            var name = line[..pos].Trim();
            var val = line[(pos + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(val, out int len) && len >= 0 && len <= 262144) length = len;
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                val.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
        }
        return (code, length, chunked);
    }

    private static byte[] Unchunk(byte[] bytes)
    {
        using var output = new MemoryStream();
        int i = 0;
        while (i < bytes.Length)
        {
            int e = Array.IndexOf(bytes, (byte)'\n', i);
            if (e < 0) throw new InvalidDataException("CTI: chunk HTTP incompleto.");
            string sizeText = Encoding.ASCII.GetString(bytes, i, e - i).Trim().Split(';')[0];
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int length) || length < 0)
                throw new InvalidDataException("CTI: chunk HTTP non valido.");
            i = e + 1;
            if (length == 0) return output.ToArray();
            if (length > 262144 || i + length + 2 > bytes.Length)
                throw new InvalidDataException("CTI: chunk HTTP troppo grande o incompleto.");
            output.Write(bytes, i, length);
            i += length;
            if (bytes[i++] != '\r' || bytes[i++] != '\n')
                throw new InvalidDataException("CTI: terminatore chunk non valido.");
        }
        throw new InvalidDataException("CTI: chunk terminatore mancante.");
    }

    private static bool HasCompleteChunked(ReadOnlySpan<byte> bytes)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            int rel = bytes[i..].IndexOf((byte)'\n');
            if (rel < 0) return false;
            int e = i + rel;
            var sizeText = Encoding.ASCII.GetString(bytes[i..e]).Trim().Split(';')[0];
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int size) || size < 0)
                return false;
            i = e + 1;
            if (size == 0) return true;
            if (size > 262144 || i + size + 2 > bytes.Length) return false;
            i += size + 2;
        }
        return false;
    }

    internal static bool SelfTest()
    {
        var parsed = ParseHeaders("HTTP/1.1 200 OK\r\nContent-Length: 35\r\nConnection: close\r\n");
        var chunked = ParseHeaders("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n");
        var b = Encoding.ASCII.GetBytes("4\r\nTEST\r\n0\r\n\r\n");
        return parsed.Code == 200 && parsed.Length == 35 && !parsed.Chunked && chunked.Chunked &&
            HeaderEnd("HTTP/1.1 200 OK\r\n\r\nbody"u8) == 19 &&
            HasCompleteChunked(b) && Encoding.ASCII.GetString(Unchunk(b)) == "TEST";
    }

    internal static async Task<Result> RequestAsync(IPAddress ip, int port, bool allowUntrusted,
        string pathAndQuery, string operation, CancellationToken cancellation)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("CTI: telefono non IPv4.");
        var parts = ip.GetAddressBytes();
        bool privateIp = parts[0] == 10 || (parts[0] == 172 && parts[1] is >= 16 and <= 31) ||
                         (parts[0] == 192 && parts[1] == 168);
        if (!privateIp || port is < 1 or > 65535)
            throw new InvalidOperationException("CTI: destinazione HTTPS non autorizzata.");
        if (!pathAndQuery.StartsWith("/cgi-bin/api-", StringComparison.Ordinal) ||
            pathAndQuery.Contains('\r') || pathAndQuery.Contains('\n'))
            throw new InvalidOperationException("CTI: percorso API non autorizzato.");

        var sw = Stopwatch.StartNew();
        using var tcp = new TcpClient { NoDelay = true };
        DiagnosticLog.Info("GRP_CTI_TCP_CONNECT", "action=" + operation + "; port=" + port);
        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            timer.CancelAfter(TimeSpan.FromSeconds(5));
            try { await tcp.ConnectAsync(ip, port, timer.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                DiagnosticLog.Warn("GRP_CTI_TCP_TIMEOUT", "action=" + operation);
                throw new InvalidOperationException("CTI: timeout connessione TCP al telefono.");
            }
        }
        DiagnosticLog.Info("GRP_CTI_TCP_OK", "action=" + operation + "; elapsed_ms=" + sw.ElapsedMilliseconds);
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
            (sender, certificate, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (allowUntrusted && certificate is not null && privateIp)
                {
                    DiagnosticLog.Warn("GRP_CTI_TLS_LAB_CERT_EXCEPTION_USED", "errors=" + errors);
                    return true;
                }
                return false;
            });
        using (ssl)
        {
            DiagnosticLog.Info("GRP_CTI_TLS_START", "action=" + operation);
            using (var tlsTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                tlsTimer.CancelAfter(TimeSpan.FromSeconds(7));
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = ip.ToString(),
                        EnabledSslProtocols = SslProtocols.None,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, tlsTimer.Token);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    DiagnosticLog.Warn("GRP_CTI_TLS_TIMEOUT", "action=" + operation);
                    throw new InvalidOperationException("CTI: timeout handshake TLS.");
                }
                catch (AuthenticationException)
                {
                    DiagnosticLog.Warn("GRP_CTI_TLS_AUTH_FAILED", "action=" + operation);
                    throw new InvalidOperationException("CTI: certificato TLS non attendibile oppure handshake fallito.");
                }
            }
            DiagnosticLog.Info("GRP_CTI_TLS_OK", "action=" + operation + "; protocol=" + ssl.SslProtocol +
                "; elapsed_ms=" + sw.ElapsedMilliseconds);
            // Explicit HTTP/1.1, no cookies/redirects/proxy, credentials are only sent through TLS.
            var requestText = "GET " + pathAndQuery + " HTTP/1.1\r\nHost: " + ip +
                "\r\nUser-Agent: Medialink CTI Client\r\nAccept: application/json, */*\r\n" +
                "Accept-Encoding: identity\r\nConnection: close\r\n\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(requestText);
            DiagnosticLog.Info("GRP_CTI_HTTPS_REQUEST_START", "action=" + operation + "; protocol=http1.1");
            using (var sendTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                sendTimer.CancelAfter(TimeSpan.FromSeconds(5));
                try { await ssl.WriteAsync(requestBytes, sendTimer.Token); await ssl.FlushAsync(sendTimer.Token); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    DiagnosticLog.Warn("GRP_CTI_HTTPS_WRITE_TIMEOUT", "action=" + operation);
                    throw new InvalidOperationException("CTI: timeout invio richiesta HTTPS; esito del comando sconosciuto.");
                }
            }
            DiagnosticLog.Info("GRP_CTI_HTTPS_REQUEST_SENT", "action=" + operation + "; elapsed_ms=" + sw.ElapsedMilliseconds);
            // Read response headers, then body; never assume a keep-alive close means body complete.
            using var memory = new MemoryStream();
            var buf = new byte[4096];
            int pos = -1;
            using (var headersTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                headersTimer.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    while ((pos = HeaderEnd(memory.ToArray())) < 0)
                    {
                        int got = await ssl.ReadAsync(buf, headersTimer.Token);
                        if (got == 0) throw new EndOfStreamException("CTI: server ha chiuso senza header.");
                        memory.Write(buf, 0, got);
                        if (memory.Length > 32768) throw new InvalidDataException("CTI: header HTTP troppo grandi.");
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    DiagnosticLog.Warn("GRP_CTI_HTTPS_HEADERS_TIMEOUT", "action=" + operation +
                        "; elapsed_ms=" + sw.ElapsedMilliseconds);
                    throw new InvalidOperationException("CTI: il GRP2613 non ha restituito header HTTPS entro 8s. Nessun ritentativo automatico.");
                }
            }
            var all = memory.ToArray();
            var h = ParseHeaders(Encoding.ASCII.GetString(all, 0, pos));
            DiagnosticLog.Info("GRP_CTI_HTTPS_HEADERS_OK", "action=" + operation + "; status=" + h.Code +
                "; elapsed_ms=" + sw.ElapsedMilliseconds + "; chunked=" + h.Chunked);
            if (h.Code < 200 || h.Code >= 300)
                throw new InvalidOperationException("CTI: telefono HTTPS HTTP " + h.Code + ". Nessun redirect seguito.");
            using var bodyBuffer = new MemoryStream();
            bodyBuffer.Write(all, pos, all.Length - pos);
            using (var bodyTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                bodyTimer.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    while (true)
                    {
                        var bodyNow = bodyBuffer.ToArray();
                        if (h.Chunked ? HasCompleteChunked(bodyNow) : h.Length.HasValue ?
                                bodyNow.Length >= h.Length.Value : TryJson(bodyNow)) break;
                        var got = await ssl.ReadAsync(buf, bodyTimer.Token);
                        if (got == 0) break;
                        bodyBuffer.Write(buf, 0, got);
                        if (bodyBuffer.Length > 262144) throw new InvalidDataException("CTI: risposta troppo grande.");
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    DiagnosticLog.Warn("GRP_CTI_HTTPS_BODY_TIMEOUT", "action=" + operation);
                    throw new InvalidOperationException("CTI: risposta HTTPS incompleta; esito del comando sconosciuto.");
                }
            }
            var bytes = bodyBuffer.ToArray();
            if (h.Chunked) bytes = Unchunk(bytes);
            else if (h.Length.HasValue && bytes.Length >= h.Length.Value)
                bytes = bytes[..h.Length.Value];
            DiagnosticLog.Info("GRP_CTI_HTTPS_BODY_OK", "action=" + operation + "; bytes=" + bytes.Length);
            return new Result(h.Code, Encoding.UTF8.GetString(bytes));
        }
    }

    private static bool TryJson(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        try { using var parsed = JsonDocument.Parse(bytes); return true; }
        catch (JsonException) { return false; }
    }
}
