using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace MedialinkCTIClient;

/// <summary>One authenticated AMI session per command; never stays connected in background.</summary>
internal sealed class AmiClient
{
    public async Task TestAsync(AppConfig config, CancellationToken ct = default)
    {
        await ExecuteAsync(config, null, ct);
    }

    public async Task OriginateAsync(AppConfig config, string rawNumber, CancellationToken ct = default)
    {
        DemoLicenseGate.Require();
        var (action, extraFields) = BuildOriginate(config, rawNumber);
        await ExecuteAsync(config, action, ct, extraFields);
    }

    /// <summary>
    /// AMI Originate toward the physical extension; preserve both legacy UCM header variables
    /// which were tested with a GRP2613, and set the caller name to the dialed destination
    /// so the initial INVITE can show the called number on the originating handset.
    /// NOTE: AMI Success means command accepted, not call answered.
    /// </summary>
    internal static (Dictionary<string, string> Action, IReadOnlyList<KeyValuePair<string, string>> ExtraFields)
        BuildOriginate(AppConfig config, string rawNumber)
    {
        if (!PhoneNumbers.ValidExtension(config.Extension)) throw new InvalidOperationException("Interno non valido.");
        var target = PhoneNumbers.Dialable(rawNumber);
        if (!PhoneNumbers.ValidDialString(target)) throw new InvalidOperationException("Numero da chiamare non valido.");
        bool internalNumber = target.All(char.IsAsciiDigit) && target.Length <= config.InternalExtensionMaxLength;
        // Feature/pickup codes belong to the local UCM dialplan, not outbound trunk routes.
        bool serviceCode = target.Contains('*') || target.Contains('#');
        var dialplanContext = (internalNumber || serviceCode) ? config.InternalContext : config.OutboundContext;
        if (string.IsNullOrWhiteSpace(dialplanContext) || dialplanContext.Any(ch => ch is '\r' or '\n'))
            throw new InvalidOperationException("Contesto AMI non valido.");
        // CallerID number stays the local extension; display name is the dialed number.
        // Routing and external trunk caller-ID policy continue to be decided by UCM.
        var action = new Dictionary<string, string>
        {
            ["Action"] = "Originate", ["ActionID"] = Guid.NewGuid().ToString("N"),
            ["Channel"] = "PJSIP/" + config.Extension, ["Context"] = dialplanContext,
            ["Exten"] = target, ["Priority"] = "1", ["Timeout"] = "30000", ["Async"] = "true",
            ["CallerID"] = $"\"{target}\" <{config.Extension}>"
        };
        IReadOnlyList<KeyValuePair<string, string>> extraFields = [];
        if (config.AutoAnswerClickToDial)
        {
            if (string.IsNullOrWhiteSpace(config.AmiHost) ||
                config.AmiHost.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or ':')))
                throw new InvalidOperationException("Server AMI non adatto per Call-Info.");
            var callInfo = $"<sip:{config.AmiHost}>;answer-after=0";
            // Retain the exact two compatible header mechanisms used in the earlier GRP2613 test.
            // AMI allows repeated 'Variable' keys, therefore keep this as a list, not dictionary.
            extraFields = [
                new("Variable", "PJSIP_HEADER(add,Call-Info)=" + callInfo),
                new("Variable", "SIPADDHEADER01=Call-Info: " + callInfo)
            ];
        }
        return (action, extraFields);
    }

    internal static bool SelfTestClickToDial()
    {
        var settings = new AppConfig { Extension = "62", AmiHost = "192.168.74.160",
            AutoAnswerClickToDial = true, InternalContext = "from-internal",
            OutboundContext = "outbound-allroutes" };
        var (originate, headerFields) = BuildOriginate(settings, "3479784437");
        if (originate.GetValueOrDefault("Action") != "Originate" ||
            originate.GetValueOrDefault("Channel") != "PJSIP/62" ||
            originate.GetValueOrDefault("Exten") != "3479784437" ||
            originate.GetValueOrDefault("CallerID") != "\"3479784437\" <62>" ||
            headerFields.Count != 2 ||
            !headerFields.Any(kv => kv.Key == "Variable" && kv.Value ==
                "PJSIP_HEADER(add,Call-Info)=<sip:192.168.74.160>;answer-after=0") ||
            !headerFields.Any(kv => kv.Key == "Variable" && kv.Value ==
                "SIPADDHEADER01=Call-Info: <sip:192.168.74.160>;answer-after=0")) return false;
        settings.AutoAnswerClickToDial = false;
        var (pickup, _) = BuildOriginate(settings, "**64");
        if (pickup.GetValueOrDefault("Exten") != "**64" ||
            pickup.GetValueOrDefault("Context") != settings.InternalContext) return false;
        var (hashCode, _) = BuildOriginate(settings, "#21#");
        if (hashCode.GetValueOrDefault("Exten") != "#21#" ||
            hashCode.GetValueOrDefault("Context") != settings.InternalContext) return false;
        var (_, withoutHeaders) = BuildOriginate(settings, "23");
        return withoutHeaders.Count == 0;
    }

    /// <summary>Terminate ONLY the selected, validated channel of the configured local extension.</summary>
    public async Task HangupAsync(AppConfig cfg, string channel, CancellationToken ct = default)
    {
        DemoLicenseGate.Require();
        var prefix = "PJSIP/" + cfg.Extension + "-";
        var oldPrefix = "SIP/" + cfg.Extension + "-";
        if ((!channel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
             !channel.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) ||
            channel.Contains('\r') || channel.Contains('\n') || channel.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Canale da terminare non appartenente all'interno configurato.");
        await ExecuteAsync(cfg, new Dictionary<string, string> {
            ["Action"] = "Hangup", ["ActionID"] = Guid.NewGuid().ToString("N"), ["Channel"] = channel }, ct);
    }

    /// <summary>Emulate DTMF received from the local phone channel so the bridge
    /// can relay it to the remote party (IVR). Validate actual delivery on UCM.
    /// AMI ACK alone is not proof the far-end received the digit.</summary>
    public Task SendDtmfAsync(AppConfig cfg, string channel, string digit, CancellationToken ct = default)
    {
        DemoLicenseGate.Require();
        return ExecuteAsync(cfg, BuildDtmf(cfg, channel, digit), ct);
    }

    internal static Dictionary<string, string> BuildDtmf(AppConfig cfg, string channel, string digit)
    {
        if (digit.Length != 1 || !"0123456789*#".Contains(digit, StringComparison.Ordinal))
            throw new InvalidOperationException("DTMF consentiti: 0-9, * e #.");
        if (!PhoneNumbers.ValidExtension(cfg.Extension) ||
            !(channel.StartsWith("PJSIP/" + cfg.Extension + "-", StringComparison.OrdinalIgnoreCase) ||
              channel.StartsWith("SIP/" + cfg.Extension + "-", StringComparison.OrdinalIgnoreCase)) ||
            channel.Any(char.IsWhiteSpace) || channel.Any(char.IsControl))
            throw new InvalidOperationException("Canale DTMF non appartenente all'interno controllato.");
        return new Dictionary<string, string>
        {
            ["Action"] = "PlayDTMF", ["ActionID"] = Guid.NewGuid().ToString("N"),
            ["Channel"] = channel, ["Digit"] = digit, ["Duration"] = "160", ["Receive"] = "true"
        };
    }

    internal static bool SelfTestDtmf()
    {
        var cfg = new AppConfig { Extension = "62" };
        var command = BuildDtmf(cfg, "PJSIP/62-00000001", "#");
        if (command.GetValueOrDefault("Action") != "PlayDTMF" ||
            command.GetValueOrDefault("Receive") != "true" ||
            command.GetValueOrDefault("Digit") != "#") return false;
        try { BuildDtmf(cfg, "PJSIP/63-00000001", "5"); return false; }
        catch (InvalidOperationException) { }
        try { BuildDtmf(cfg, "PJSIP/62-00000001", "5\r\nAction: Hangup"); return false; }
        catch (InvalidOperationException) { }
        return true;
    }

    private static async Task ExecuteAsync(AppConfig cfg, Dictionary<string, string>? action,
        CancellationToken cancellation, IReadOnlyList<KeyValuePair<string, string>>? extraFields = null)
    {
        if (string.IsNullOrWhiteSpace(cfg.AmiUser) || string.IsNullOrEmpty(cfg.AmiSecret))
            throw new InvalidOperationException("Configurare utente e password AMI nelle Impostazioni.");
        if (cfg.AmiPort is < 1 or > 65535) throw new InvalidOperationException("Porta AMI non valida.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(cfg.AmiHost, cfg.AmiPort, timeout.Token);
        using Stream stream = cfg.AmiTls
            ? await CreateTlsStreamAsync(tcp, cfg.AmiHost, timeout.Token)
            : tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        { NewLine = "\r\n", AutoFlush = true };
        var greeting = await reader.ReadLineAsync(timeout.Token);
        if (greeting is null || !greeting.Contains("Asterisk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Il server non ha restituito il saluto AMI Asterisk.");
        await SendAsync(writer, new Dictionary<string, string>
        { ["Action"] = "Login", ["Username"] = cfg.AmiUser, ["Secret"] = cfg.AmiSecret, ["Events"] = "off" });
        var login = await ReadResponseAsync(reader, timeout.Token);
        if (!login.TryGetValue("Response", out var status) || !status.Equals("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Login AMI rifiutato: " + login.GetValueOrDefault("Message", "verifica credenziali e ACL."));
        if (action is not null)
        {
            if (extraFields is { Count: > 0 })
                await SendAsync(writer, action.Concat(extraFields));
            else
                await SendAsync(writer, action);
            var response = await ReadResponseAsync(reader, timeout.Token);
            if (!response.TryGetValue("Response", out var outcome) || !outcome.Equals("Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Comando AMI non accettato dalla UCM: " + response.GetValueOrDefault("Message", "errore AMI."));
        }
        await SendAsync(writer, new Dictionary<string, string> { ["Action"] = "Logoff" });
    }

    private static async Task<Stream> CreateTlsStreamAsync(TcpClient tcp, string host, CancellationToken ct)
    {
        // Certificate verification is mandatory. No validation bypass for AMI TLS.
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
        return ssl;
    }

    private static async Task SendAsync(StreamWriter writer, IEnumerable<KeyValuePair<string, string>> fields)
    {
        foreach (var (key, value) in fields)
        {
            if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("Valore AMI non valido.");
            await writer.WriteLineAsync($"{key}: {value}");
        }
        await writer.WriteLineAsync();
        await writer.FlushAsync();
    }

    private static async Task<Dictionary<string, string>> ReadResponseAsync(StreamReader reader, CancellationToken ct)
    {
        // AMI can emit asynchronous Event packets; ignore them and return the first Response packet.
        for (var packetCount = 0; packetCount < 30; packetCount++)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var lineCount = 0; lineCount < 500; lineCount++)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) throw new IOException("Connessione AMI chiusa dal centralino.");
                if (line.Length == 0) break;
                var idx = line.IndexOf(':');
                if (idx > 0) result[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
            if (result.ContainsKey("Response")) return result;
        }
        throw new InvalidOperationException("Risposta AMI non ricevuta.");
    }
}
