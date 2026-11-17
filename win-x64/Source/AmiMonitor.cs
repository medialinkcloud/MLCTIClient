using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace MedialinkCTIClient;

internal sealed record LiveCall(string Channel, string Peer, bool Incoming, DateTime StartedUtc,
    DateTime? ConnectedUtc, int ActiveChannels, bool DirectionKnown = true);

/// <summary>Dedicated AMI event stream; one connection, no timer-based AMI polling.</summary>
internal sealed class AmiMonitor
{
    // DirectionConfidence: 0 unknown, 1 caller-ID inference, 2 DialBegin (authoritative).
    // DialBegin must win over potentially misleading Newchannel / Newstate caller IDs.
    private sealed record Leg(string Channel, string Peer, bool Incoming, DateTime StartedUtc,
        DateTime? ConnectedUtc, int DirectionConfidence);
    private readonly Dictionary<string, Leg> legs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    // A short-lived intent from the one-click AMI Originate action. The initial
    // DialBegin of an Originate directed to PJSIP/<extension> may otherwise
    // resemble an incoming call. No phone number is written to diagnostics.
    private string? outgoingIntentTarget;
    private DateTime outgoingIntentUtc;
    private string? outgoingIntentChannel;

    public void RegisterOutgoingIntent(string target)
    {
        lock (gate)
        {
            if (legs.Count != 0 || string.IsNullOrWhiteSpace(target)) return;
            outgoingIntentTarget = target;
            outgoingIntentUtc = DateTime.UtcNow;
            outgoingIntentChannel = null;
        }
        DiagnosticLog.Info("AMI_OUTGOING_INTENT_REGISTERED", "duration_limit_seconds=15");
    }

    public void ClearOutgoingIntent(string target)
    {
        lock (gate)
        {
            if (!string.Equals(target, outgoingIntentTarget, StringComparison.Ordinal)) return;
            outgoingIntentTarget = null;
            outgoingIntentChannel = null;
        }
        DiagnosticLog.Warn("AMI_OUTGOING_INTENT_CLEARED", "reason=originate_failed");
    }
    public event Action<LiveCall?>? StateChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action? InitialSnapshotComplete;
    public event Action? LocalCallEnded;
    public event Action<string, BlfState>? BlfChanged;
    private readonly Dictionary<string, BlfState> blfStates = new(StringComparer.Ordinal);
    private void SetBlf(string extension, BlfState state)
    {
        if (extension.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(extension, @"^[0-9]{1,8}$")) return;
        if (blfStates.TryGetValue(extension, out var previous) && previous == state) return;
        blfStates[extension] = state;
        BlfChanged?.Invoke(extension, state);
    }
    private void ResetBlf()
    {
        foreach(var extension in blfStates.Keys.ToArray()) BlfChanged?.Invoke(extension, BlfState.Unknown);
        blfStates.Clear();
    }

    public async Task RunAsync(AppConfig cfg, CancellationToken cancellation)
    {
        var delay = 2;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(cfg, cancellation);
                if (!cancellation.IsCancellationRequested)
                    DiagnosticLog.Warn("AMI_MONITOR_DISCONNECTED");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception e) { DiagnosticLog.Error("AMI_MONITOR_CONNECTION_FAILED", e); }
            if (cancellation.IsCancellationRequested) break;
            lock (gate) { legs.Clear(); outgoingIntentTarget = null; outgoingIntentChannel = null; }
            ResetBlf();
            ConnectionChanged?.Invoke(false);
            StateChanged?.Invoke(null);
            try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellation); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 30);
        }
        lock (gate) { legs.Clear(); outgoingIntentTarget = null; outgoingIntentChannel = null; }
        ResetBlf();
        ConnectionChanged?.Invoke(false);
        StateChanged?.Invoke(null);
    }

    private async Task ConnectAndListenAsync(AppConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.AmiUser) || string.IsNullOrWhiteSpace(cfg.AmiSecret))
            throw new InvalidOperationException("Credenziali AMI non configurate.");
        using var tcp = new TcpClient();
        using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(cfg.AmiHost, cfg.AmiPort, connectionTimeout.Token);
        }
        using Stream stream = cfg.AmiTls
            ? await OpenTlsAsync(tcp, cfg.AmiHost, ct)
            : tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            { NewLine = "\r\n", AutoFlush = true };
        var banner = await reader.ReadLineAsync(ct);
        if (banner is null || !banner.Contains("Asterisk", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Banner AMI non valido.");
        await WriteAsync(writer, new Dictionary<string, string> {
            ["Action"]="Login", ["Username"]=cfg.AmiUser, ["Secret"]=cfg.AmiSecret,
            ["Events"]="on" });
        bool authenticated = false;
        for (int n = 0; n < 20 && !authenticated; n++)
        {
            var packet = await ReadPacketAsync(reader, ct) ?? throw new IOException("Login AMI interrotto.");
            if (packet.TryGetValue("Response", out var res))
                authenticated = res.Equals("Success", StringComparison.OrdinalIgnoreCase);
            if (packet.TryGetValue("Response", out res) && !authenticated)
                throw new InvalidOperationException("Accesso eventi AMI rifiutato.");
        }
        if (!authenticated) throw new IOException("Risposta di login AMI mancante.");
        DiagnosticLog.Info("AMI_MONITOR_CONNECTED");
        ConnectionChanged?.Invoke(true);
        // Initial snapshot so calls already in progress appear without periodic polling.
        await WriteAsync(writer, new Dictionary<string, string> {
            ["Action"]="CoreShowChannels", ["ActionID"]="MLCTI-INIT-" + Guid.NewGuid().ToString("N") });
        foreach(var item in BlfLogic.Parse(cfg.BlfExtensions, cfg.Extension))
            await WriteAsync(writer, new Dictionary<string, string> {
                ["Action"]="ExtensionState", ["ActionID"]="MLCTI-BLF-" + item.Extension,
                ["Exten"]=item.Extension, ["Context"]=cfg.InternalContext });
        while (!ct.IsCancellationRequested)
        {
            var packet = await ReadPacketAsync(reader, ct);
            if (packet is null) throw new IOException("Connessione AMI chiusa.");
            HandlePacket(cfg.Extension, packet);
        }
    }

    internal static bool SelfTest()
    {
        var m = new AmiMonitor();
        LiveCall? last = null;
        int ended = 0;
        m.StateChanged += c => last = c;
        m.LocalCallEnded += () => ended++;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="DialBegin", ["Channel"]="PJSIP/other-00000001",
            ["DestChannel"]="PJSIP/62-00000002", ["CallerIDNum"]="3391234567" });
        if (last is null || !last.Incoming || last.Channel != "PJSIP/62-00000002" ||
            last.ActiveChannels != 1) return false;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Newstate", ["Channel"]="PJSIP/62-00000002",
            ["ChannelStateDesc"]="Up", ["CallerIDNum"]="3391234567" });
        if (last?.ConnectedUtc is null) return false;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Hangup", ["Channel"]="PJSIP/62-00000002" });
        if (last is not null || ended != 1) return false;
        // A new outgoing channel may initially have unknown CallerID: do not display Answer.
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Newchannel", ["Channel"]="PJSIP/62-00000003",
            ["CallerIDNum"]="<unknown>", ["ChannelStateDesc"]="Down" });
        if (last is null || last.DirectionKnown) return false;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="DialBegin", ["Channel"]="PJSIP/62-00000003",
            ["DestChannel"]="PJSIP/trunk-00000004", ["DialString"]="3331234567" });
        if (last is null || !last.DirectionKnown || last.Incoming) return false;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Newstate", ["Channel"]="PJSIP/62-00000003",
            ["CallerIDNum"]="3331234567", ["ChannelStateDesc"]="Up" });
        if (last is null || last.Incoming || last.ConnectedUtc is null) return false;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Hangup", ["Channel"]="PJSIP/62-00000003" });
        if (last is not null || ended != 2) return false;
        // AMI Originate to PJSIP/62: destination leg is local, but the call is outbound.
        m.RegisterOutgoingIntent("3331234567");
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="DialBegin", ["Channel"]="Local/3331234567@from-internal-00001",
            ["DestChannel"]="PJSIP/62-00000005", ["CallerIDNum"]="62" });
        if (last is null || last.Incoming || !last.DirectionKnown || last.Peer != "3331234567")
            return false;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Hangup", ["Channel"]="PJSIP/62-00000005" });
        if (last is not null || ended != 3) return false;

        // Regression: UCM may advertise the outbound trunk CallerID 0444417433,
        // while the actual dialled party is 3479784437. DialBegin must win,
        // including after Newstate says "Up" with the same local caller ID.
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Newchannel", ["Channel"]="PJSIP/62-00000006",
            ["CallerIDNum"]="0444417433", ["ChannelStateDesc"]="Down" });
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="DialBegin", ["Channel"]="PJSIP/62-00000006",
            ["DestChannel"]="PJSIP/trunk-00000007", ["CallerIDNum"]="0444417433",
            ["DestCallerIDNum"]="0444417433", ["DialString"]="3479784437" });
        if (last is null || last.Peer != "3479784437" || last.Incoming || !last.DirectionKnown)
            return false;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Newstate", ["Channel"]="PJSIP/62-00000006",
            ["CallerIDNum"]="0444417433", ["ChannelStateDesc"]="Up" });
        if (last is null || last.Peer != "3479784437" || !last.ConnectedUtc.HasValue || last.Incoming ||
            PhoneNumbers.SearchKey(last.Peer) != "3479784437") return false;
        m.HandlePacket("62", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["Event"]="Hangup", ["Channel"]="PJSIP/62-00000006" });
        return last is null && ended == 4 && OutgoingPeerRules.SelfTest();
    }

    private static async Task<Stream> OpenTlsAsync(TcpClient tcp, string host, CancellationToken ct)
    {
        var ssl = new SslStream(tcp.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
        return ssl;
    }

    private static async Task WriteAsync(StreamWriter writer, IReadOnlyDictionary<string, string> fields)
    {
        foreach (var (key, value) in fields)
        {
            if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("Valore AMI non valido.");
            await writer.WriteLineAsync(key + ": " + value);
        }
        await writer.WriteLineAsync();
        await writer.FlushAsync();
    }

    private static async Task<Dictionary<string, string>?> ReadPacketAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int n = 0; n < 500; n++)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return null;
            if (line.Length == 0) return result;
            var p = line.IndexOf(':');
            if (p > 0) result[line[..p].Trim()] = line[(p + 1)..].Trim();
        }
        throw new IOException("Pacchetto AMI troppo lungo.");
    }

    private static string Value(Dictionary<string, string> p, string key) => p.GetValueOrDefault(key, "");
    private static bool IsLocal(string channel, string ext) =>
        channel.StartsWith("PJSIP/" + ext + "-", StringComparison.OrdinalIgnoreCase) ||
        channel.StartsWith("SIP/" + ext + "-", StringComparison.OrdinalIgnoreCase);

    private void HandlePacket(string ext, Dictionary<string, string> p)
    {
        var kind = Value(p, "Event");
        var actionId = Value(p, "ActionID");
        if (kind.Equals("ExtensionStatus", StringComparison.OrdinalIgnoreCase) ||
            (actionId.StartsWith("MLCTI-BLF-", StringComparison.Ordinal) &&
             Value(p, "Response").Equals("Success", StringComparison.OrdinalIgnoreCase)))
        {
            var monitoredExtension = Value(p, "Exten");
            if (monitoredExtension.Length == 0 && actionId.StartsWith("MLCTI-BLF-", StringComparison.Ordinal))
                monitoredExtension = actionId.Substring("MLCTI-BLF-".Length);
            SetBlf(monitoredExtension, BlfLogic.FromAmi(Value(p, "Status")));
        }
        // AMI reports the end of the initial CoreShowChannels enumeration.
        // Only then can an empty call list be interpreted as idle.
        if (kind.Equals("CoreShowChannelsComplete", StringComparison.OrdinalIgnoreCase))
        {
            InitialSnapshotComplete?.Invoke();
            return;
        }
        if (kind.Length == 0) return;
        var channel = Value(p, "Channel");
        var destination = Value(p, "DestChannel");
        bool changed = false, ended = false;
        lock (gate)
        {
            if (kind.Equals("Hangup", StringComparison.OrdinalIgnoreCase))
            {
                if (legs.Remove(channel)) { changed = true; ended = legs.Count == 0; }
                if (string.Equals(outgoingIntentChannel, channel, StringComparison.OrdinalIgnoreCase))
                {
                    outgoingIntentChannel = null;
                    outgoingIntentTarget = null;
                }
            }
            else if (kind.Equals("DialBegin", StringComparison.OrdinalIgnoreCase))
            {
                if (IsLocal(destination, ext))
                {
                    AddOrUpdate(destination, Value(p, "CallerIDNum"), true, false, 2);
                    changed = true;
                }
                else if (IsLocal(channel, ext))
                {
                    // On outbound calls CallerIDNum is OUR presented identity,
                    // not the dialled party. Never prefer DestCallerIDNum over a
                    // usable DialString, and never let a later Newstate overwrite
                    // the number identified by this DialBegin.
                    var (peer, source) = OutgoingPeerRules.Resolve(p, ext);
                    AddOrUpdate(channel, peer, false, false, 2);
                    DiagnosticLog.Info("AMI_OUTGOING_PEER_SELECTED",
                        "source=" + source + "; resolved=" + (peer.Length > 0));
                    changed = true;
                }
            }
            else if (kind is "Newchannel" or "Newstate" or "CoreShowChannel" or "BridgeEnter")
            {
                if (IsLocal(channel, ext))
                {
                    var state = Value(p, "ChannelStateDesc");
                    if (kind == "CoreShowChannel") state = Value(p, "ChannelStateDesc");
                    bool up = state.Equals("Up", StringComparison.OrdinalIgnoreCase) || kind == "BridgeEnter";
                    var caller = Value(p, "CallerIDNum");
                    var peer = caller.Equals(ext, StringComparison.OrdinalIgnoreCase)
                        ? Value(p, "ConnectedLineNum") : caller;
                    bool callerKnown = !string.IsNullOrWhiteSpace(caller) &&
                        caller != "<unknown>" && caller != "s";
                    bool incoming = callerKnown && !caller.Equals(ext, StringComparison.OrdinalIgnoreCase);
                    AddOrUpdate(channel, peer, incoming, up, callerKnown ? 1 : 0);
                    changed = true;
                }
            }
        }
        if (changed)
        {
            Publish();
            if (ended) LocalCallEnded?.Invoke();
        }
    }

    // Called only while gate is held.
    private void AddOrUpdate(string channel, string peer, bool incoming, bool up, int directionConfidence)
    {
        var now = DateTime.UtcNow;
        if (outgoingIntentTarget is not null)
        {
            if (now - outgoingIntentUtc > TimeSpan.FromSeconds(15))
            {
                outgoingIntentTarget = null;
                outgoingIntentChannel = null;
            }
            else if (string.Equals(outgoingIntentChannel, channel, StringComparison.OrdinalIgnoreCase) ||
                (outgoingIntentChannel is null && legs.Count == 0))
            {
                if (outgoingIntentChannel is null)
                {
                    outgoingIntentChannel = channel;
                    DiagnosticLog.Info("AMI_OUTGOING_INTENT_MATCHED", "channel=local_sip; direction=outgoing");
                }
                peer = outgoingIntentTarget;
                incoming = false;
                directionConfidence = 3; // stronger than DialBegin inferred direction
            }
        }
        // An identity established by DialBegin (confidence 2), or by an
        // explicitly originated CTI call (confidence 3), must not be replaced
        // by CallerIDNum from Newchannel/Newstate (confidence 0/1).
        // If the more authoritative event cannot determine the dialled
        // number, show "Numero non disponibile" instead of our trunk CLI.
        bool usable = !string.IsNullOrWhiteSpace(peer) && peer != "<unknown>" && peer != "s";
        if (legs.TryGetValue(channel, out var existing))
        {
            bool moreReliable = directionConfidence > existing.DirectionConfidence;
            bool sameReliability = directionConfidence == existing.DirectionConfidence;
            legs[channel] = existing with {
                Peer = moreReliable ? (usable ? peer : "") :
                    (sameReliability && usable ? peer : existing.Peer),
                Incoming = (moreReliable || sameReliability) ? incoming : existing.Incoming,
                DirectionConfidence = Math.Max(directionConfidence, existing.DirectionConfidence),
                ConnectedUtc = existing.ConnectedUtc ?? (up ? now : null)
            };
        }
        else
            legs[channel] = new Leg(channel,
                usable ? peer : "", incoming, now, up ? now : null, directionConfidence);
    }

    private string lastLoggedState = "";

    private void Publish()
    {
        LiveCall? snapshot;
        lock (gate)
        {
            var chosen = legs.Values.OrderByDescending(x => x.ConnectedUtc.HasValue)
                .ThenBy(x => x.StartedUtc).FirstOrDefault();
            snapshot = chosen is null ? null : new LiveCall(chosen.Channel, chosen.Peer,
                chosen.Incoming, chosen.StartedUtc, chosen.ConnectedUtc, legs.Count,
                chosen.DirectionConfidence > 0);
        }
        string state = snapshot is null ? "idle" :
            $"{snapshot.Channel}|{snapshot.Incoming}|{snapshot.DirectionKnown}|{snapshot.ConnectedUtc.HasValue}|{snapshot.ActiveChannels}";
        if (state != lastLoggedState)
        {
            lastLoggedState = state;
            if (snapshot is not null)
                DiagnosticLog.Info("AMI_LOCAL_CALL_STATE", "direction=" +
                    (snapshot.DirectionKnown ? (snapshot.Incoming ? "incoming" : "outgoing") : "unknown") +
                    "; phase=" + (snapshot.ConnectedUtc.HasValue ? "connected" : "setup") +
                    "; active_channels=" + snapshot.ActiveChannels);
            else DiagnosticLog.Info("AMI_LOCAL_CALL_STATE", "direction=idle");
        }
        StateChanged?.Invoke(snapshot);
    }
}
