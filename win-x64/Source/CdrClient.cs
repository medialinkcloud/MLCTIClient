using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace MedialinkCTIClient;

/// <summary>Grandstream UCM HTTPS JSON API. CDR nested main_cdr/sub_cdr supported.</summary>
internal sealed class CdrClient
{
    public async Task<List<CallEntry>> GetForExtensionAsync(AppConfig cfg, bool recentOnly = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiUser) || string.IsNullOrEmpty(cfg.ApiSecret))
            throw new InvalidOperationException("Configurare utente e password API CDR nelle Impostazioni.");
        if (!PhoneNumbers.ValidExtension(cfg.Extension)) throw new InvalidOperationException("Interno non valido.");
        if (cfg.ApiPort is < 1 or > 65535) throw new InvalidOperationException("Porta HTTPS API non valida.");
        using var handler = new HttpClientHandler();
        if (cfg.AllowUntrustedApiCertificate)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true; // opt-in only
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var url = $"https://{cfg.ApiHost}:{cfg.ApiPort}/api";
        using var challenge = await PostAsync(http, url,
            new { request = new { action = "challenge", user = cfg.ApiUser, version = "1.0" } }, ct);
        var challengeText = Value(challenge.RootElement, "response", "challenge");
        if (string.IsNullOrEmpty(challengeText)) throw new InvalidOperationException("API UCM non restituisce la challenge.");
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(challengeText + cfg.ApiSecret)))
            .ToLowerInvariant(); // UCM API challenge requires MD5 per protocol.
        using var login = await PostAsync(http, url,
            new { request = new { action = "login", user = cfg.ApiUser, token } }, ct);
        var cookie = Value(login.RootElement, "response", "cookie");
        if (string.IsNullOrEmpty(cookie)) throw new InvalidOperationException("Login HTTPS API non completato.");

        // Primo recupero: CDR di oggi e ieri SENZA filtro caller/callee.
        // Il filtro server si applica al record principale e può escludere
        // una chiamata che ha coinvolto l'interno solo in una sub_cdr di un gruppo.
        // I dati di altri interni non vengono visualizzati, salvati né loggati.
        int displayLimit = Math.Clamp(cfg.CdrMaxRecords, 25, 2000);
        var all = new List<CdrGroup>();
        var today = DateTime.Today;
        for (int dayBack = 0; dayBack < 2; dayBack++)
        {
            var day = today.AddDays(-dayBack);
            var from = day.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            var to = day.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            DiagnosticLog.Info("CDR_QUERY_WINDOW", $"scope=all_recent; day={day:yyyy-MM-dd}");
            all.AddRange(await FetchAllPagesAsync(http, url, cookie, null, from, to, ct));
        }
        DiagnosticLog.Info("CDR_RECENT_SCAN", $"sessions={all.Count}; relevant={BuildEntries(all, cfg.Extension, cfg.InternalExtensionMaxLength).Count}");

        // Storico: continuare a interrogare in modo selettivo l'interno,
        // per evitare di scaricare CDR di tutta la centrale nel lungo periodo.
        DateTime endExclusive = today.AddDays(-1);
        foreach (int days in new[] { 7, 30, 90, 365, 730 })
        {
            if (recentOnly) break;
            if (BuildEntries(all, cfg.Extension, cfg.InternalExtensionMaxLength).Count >= displayLimit) break;
            var fromDay = today.AddDays(-days + 1);
            var untilDay = endExclusive.AddDays(-1);
            if (fromDay > untilDay) continue;
            string from = fromDay.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            string to = endExclusive.AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            DiagnosticLog.Info("CDR_QUERY_WINDOW", $"scope=extension; from={fromDay:yyyy-MM-dd}; to={untilDay:yyyy-MM-dd}");
            all.AddRange(await FetchAllPagesAsync(http, url, cookie, "caller", cfg.Extension, from, to, ct));
            all.AddRange(await FetchAllPagesAsync(http, url, cookie, "callee", cfg.Extension, from, to, ct));
            endExclusive = fromDay;
        }
        var recent = BuildEntries(all, cfg.Extension, cfg.InternalExtensionMaxLength)
            .OrderByDescending(c => ParseStart(c.Timestamp))
            .ThenByDescending(c => c.Timestamp, StringComparer.Ordinal)
            .Take(displayLimit).ToList();
        DiagnosticLog.Info("CDR_QUERY_COMPLETE", $"scope={(recentOnly ? "recent" : "all")}; groups={all.Count}; displayed={recent.Count}; answered_elsewhere={recent.Count(c => c.AnsweredElsewhere)}; latest={(recent.Count > 0 ? recent[0].Timestamp : "none")}");
        try { using var _ = await PostAsync(http, url, new { request = new { action = "logout", cookie } }, ct); }
        catch { /* logout may be expired */ }
        return recent;
    }

    private static DateTime ParseStart(string text)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var result)) return result;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out result)) return result;
        return DateTime.MinValue;
    }

    private static async Task<List<CdrGroup>> FetchAllPagesAsync(HttpClient client, string url,
        string cookie, string? filterField, string from, string to, CancellationToken ct)
        => await FetchAllPagesAsync(client, url, cookie, filterField, null, from, to, ct);

    private static async Task<List<CdrGroup>> FetchAllPagesAsync(HttpClient client, string url,
        string cookie, string? filterField, string? extension, string from, string to, CancellationToken ct)
    {
        const int pageSize = 200;
        const int maxPages = 40;
        var all = new List<CdrGroup>();
        for (int page = 0; page < maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            int offset = page * pageSize;
            var parameters = new Dictionary<string, object>
            {
                ["action"] = "cdrapi", ["cookie"] = cookie, ["format"] = "json",
                ["numRecords"] = pageSize, ["offset"] = offset,
                ["startTime"] = from, ["endTime"] = to
            };
            if (!string.IsNullOrWhiteSpace(filterField) && !string.IsNullOrWhiteSpace(extension))
                parameters[filterField] = extension;
            using var doc = await PostAsync(client, url, new { request = parameters }, ct);
            var pageResult = ReadPage(doc);
            all.AddRange(pageResult.Groups);
            DiagnosticLog.Info("CDR_PAGE", $"scope={filterField ?? "all"}; day_from={from[..10]}; offset={offset}; raw={pageResult.RawCount}; groups={pageResult.Groups.Count}");
            if (pageResult.RawCount < pageSize) return all;
        }
        throw new InvalidOperationException($"Troppi CDR nell'intervallo {from[..10]}..{to[..10]}: 8000 record raggiunti. Servono intervalli più piccoli.");
    }

    private sealed record CdrLeg(string Src, string Dst, string ChannelExt, string DstChannelExt,
        string Start, string BillSeconds, string Disposition, string DstAnswer, string UniqueId,
        string Session, string AcctId);
    private sealed record CdrGroup(string Key, List<CdrLeg> Legs);
    private sealed record CdrPage(List<CdrGroup> Groups, int RawCount);

    private static CdrPage ReadPage(JsonDocument doc)
    {
        var root = doc.RootElement;
        JsonElement rows = default;
        if (root.TryGetProperty("cdr_root", out var direct)) rows = direct;
        else if (root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object &&
            resp.TryGetProperty("cdr_root", out var nested)) rows = nested;
        if (rows.ValueKind != JsonValueKind.Array)
        {
            DiagnosticLog.Warn("CDR_RESPONSE_NO_ARRAY", "missing_cdr_root=true");
            throw new InvalidDataException("La UCM non ha restituito l'array cdr_root: formato risposta CDR imprevisto.");
        }
        var groups = new List<CdrGroup>();
        foreach (var item in rows.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var legs = new List<CdrLeg>();
            // Non trattare il contenitore con 'main_cdr' come un record vuoto.
            if (item.TryGetProperty("main_cdr", out var main) && main.ValueKind == JsonValueKind.Object)
            {
                legs.Add(ParseLeg(main));
                foreach (var field in item.EnumerateObject())
                {
                    if (field.Name.StartsWith("sub_cdr_", StringComparison.OrdinalIgnoreCase) &&
                        field.Value.ValueKind == JsonValueKind.Object)
                        legs.Add(ParseLeg(field.Value));
                    else if (field.NameEquals("sub_cdr") && field.Value.ValueKind == JsonValueKind.Array)
                        foreach (var segment in field.Value.EnumerateArray())
                            if (segment.ValueKind == JsonValueKind.Object) legs.Add(ParseLeg(segment));
                }
            }
            else
            {
                legs.Add(ParseLeg(item));
            }
            if (legs.Count == 0) continue;
            var session = Read(item, "cdr");
            if (session.Length == 0) session = Read(item, "session");
            if (session.Length == 0) session = legs.Select(x => x.Session).FirstOrDefault(x => x.Length > 0) ?? "";
            if (session.Length == 0) session = legs.Select(x => x.UniqueId).FirstOrDefault(x => x.Length > 0) ?? "";
            if (session.Length == 0)
            {
                var p = legs[0];
                session = $"fallback:{p.Start}:{p.Src}:{p.Dst}:{p.ChannelExt}:{p.DstChannelExt}";
            }
            groups.Add(new CdrGroup(session, legs));
        }
        return new CdrPage(groups, rows.GetArrayLength());
    }

    private static CdrLeg ParseLeg(JsonElement x) => new(
        Read(x, "src"), Read(x, "dst"), Read(x, "channel_ext"), Read(x, "dstchannel_ext"),
        Read(x, "start"), Read(x, "billsec"), Read(x, "disposition"), Read(x, "dstanswer"),
        Read(x, "uniqueid"), Read(x, "session"), Read(x, "AcctId"));

    private static List<CallEntry> BuildEntries(IEnumerable<CdrGroup> raw, string extension, int maxExtensionLength)
    {
        var result = new List<CallEntry>();
        foreach (var session in raw.GroupBy(x => x.Key, StringComparer.Ordinal))
        {
            // Dupliche inevitabili tra query caller e callee: mantenere ogni segmento una sola volta.
            var legs = session.SelectMany(g => g.Legs)
                .DistinctBy(x => (x.UniqueId, x.AcctId, x.Start, x.Src, x.Dst, x.ChannelExt,
                    x.DstChannelExt, x.Disposition, x.DstAnswer, x.BillSeconds))
                .ToList();
            var incoming = legs.Where(x => x.Dst == extension || x.DstChannelExt == extension).ToList();
            var outgoing = legs.Where(x => x.Src == extension ||
                (x.ChannelExt == extension && x.Dst != extension && x.DstChannelExt != extension)).ToList();
            if (incoming.Count == 0 && outgoing.Count == 0) continue;
            // Preferire la prima interazione cronologica dell'interno nella sessione.
            var firstIn = incoming.OrderBy(x => ParseStart(x.Start)).FirstOrDefault();
            var firstOut = outgoing.OrderBy(x => ParseStart(x.Start)).FirstOrDefault();
            bool isOut = firstOut is not null && (firstIn is null ||
                ParseStart(firstOut.Start) <= ParseStart(firstIn.Start));
            var local = isOut ? outgoing : incoming;
            var focus = local.OrderBy(x => ParseStart(x.Start)).First();
            var peer = isOut ? focus.Dst : focus.Src;
            if (string.IsNullOrWhiteSpace(peer) || peer == extension)
                peer = isOut ? focus.DstChannelExt : focus.ChannelExt;
            if (string.IsNullOrWhiteSpace(peer) || peer == extension) continue;

            // Una chiamata in arrivo è 'Risposta' solo se c'è una risposta dell'interno locale.
            // dstanswer di un altro interno NON dimostra che l'utente abbia risposto.
            bool locallyAnswered = !isOut && local.Any(x => IsAnswered(x) &&
                (x.DstAnswer == extension ||
                (string.IsNullOrWhiteSpace(x.DstAnswer) && x.Dst == extension && x.DstChannelExt == extension)));
            var direction = isOut ? "Uscita" : locallyAnswered ? "Entrata" : "Persa";
            // Risposta di un altro interno: SOLO correlazione della STESSA sessione +
            // segmento ANSWERED con destinazione interna esplicita differente.
            var elsewhere = !isOut && !locallyAnswered
                ? legs.Where(IsAnswered)
                    // Prefer positively matched answering leg over a main/group destination.
                    .OrderByDescending(x => x.DstAnswer == x.DstChannelExt &&
                        ValidOtherExtension(x.DstAnswer, extension, maxExtensionLength))
                    .ThenByDescending(x => x.DstChannelExt.Length > 0)
                    .Select(x => AnsweredExtension(x, extension, maxExtensionLength))
                    .FirstOrDefault(x => x.Length > 0) ?? ""
                : "";
            var bestDuration = local.Where(x => IsAnswered(x) &&
                    (isOut || x.DstAnswer == extension ||
                    (string.IsNullOrWhiteSpace(x.DstAnswer) && x.Dst == extension && x.DstChannelExt == extension)))
                .OrderByDescending(x => ParseSeconds(x.BillSeconds)).FirstOrDefault() ?? focus;
            var timestamp = local.Select(x => x.Start).Where(x => x.Length > 0)
                .OrderBy(x => ParseStart(x)).FirstOrDefault() ?? focus.Start;
            result.Add(new CallEntry(session.Key, timestamp, direction, peer,
                FormatDuration(direction == "Persa" ? "0" : bestDuration.BillSeconds),
                focus.Disposition, "", elsewhere, elsewhere.Length > 0));
        }
        return result;
    }

    private static bool IsAnswered(CdrLeg leg) =>
        string.Equals(leg.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase);

    private static string AnsweredExtension(CdrLeg leg, string local, int maxLength)
    {
        var value = leg.DstAnswer;
        // dstanswer è il dato ufficiale preferito; dstchannel_ext è fallback SOLO
        // quando il segmento è marcato ANSWERED. Non usare numeri esterni lunghi.
        if (!ValidOtherExtension(value, local, maxLength)) value = leg.DstChannelExt;
        return ValidOtherExtension(value, local, maxLength) ? value : "";
    }

    private static bool ValidOtherExtension(string value, string local, int maxLength) =>
        value.Length > 0 && value.Length <= Math.Clamp(maxLength, 1, 8) && value != local &&
        value.All(char.IsAsciiDigit);

    private static int ParseSeconds(string text) => int.TryParse(text, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out int sec) && sec >= 0 ? sec : 0;

    private static string FormatDuration(string text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 0)
            return "—";
        return TimeSpan.FromSeconds(seconds).TotalHours >= 1
            ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    internal static bool SelfTest()
    {
        // Nessuna richiesta HTTP: documenta e collauda le regole sul formato JSON UCM.
        const string json = """
        {"cdr_root":[
          {"cdr":"sess-out", "main_cdr":{"src":"62","dst":"3331234567","start":"2026-11-07 09:00:00","billsec":"125","disposition":"ANSWERED","dstanswer":"3331234567"}},
          {"cdr":"sess-in", "main_cdr":{"src":"3395551234","dst":"62","start":"2026-11-07 09:01:00","billsec":"65","disposition":"ANSWERED","dstanswer":"62"}},
          {"cdr":"sess-other", "main_cdr":{"src":"3342341234","dst":"600","start":"2026-11-07 09:02:00","billsec":"0","disposition":"ANSWERED","dstanswer":"63"},
            "sub_cdr_1":{"src":"3342341234","dst":"62","dstchannel_ext":"62","start":"2026-11-07 09:02:00","billsec":"0","disposition":"NO ANSWER"},
            "sub_cdr_2":{"src":"3342341234","dst":"63","dstchannel_ext":"63","start":"2026-11-07 09:02:00","billsec":"45","disposition":"ANSWERED","dstanswer":"63"}},
          {"cdr":"sess-unknown", "main_cdr":{"src":"3351234567","dst":"62","start":"2026-11-07 09:03:00","billsec":"0","disposition":"NO ANSWER"}}
        ]}
        """;
        using var doc = JsonDocument.Parse(json);
        var p = ReadPage(doc);
        var entries = BuildEntries(p.Groups, "62", 4);
        return entries.Count == 4 &&
            entries.Any(x => x.Id == "sess-out" && x.Direction == "Uscita" && x.Duration == "2:05") &&
            entries.Any(x => x.Id == "sess-in" && x.Direction == "Entrata" && x.Duration == "1:05") &&
            entries.Any(x => x.Id == "sess-other" && x.Direction == "Persa" && x.AnsweredElsewhere && x.AnsweredBy == "63") &&
            entries.Any(x => x.Id == "sess-unknown" && x.Direction == "Persa" && !x.AnsweredElsewhere) &&
            p.RawCount == 4;
    }

    private static string Read(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(), _ => ""
        };
    }

    private static string Value(JsonElement root, string container, string key)
    {
        if (root.TryGetProperty(container, out var response) && response.ValueKind == JsonValueKind.Object)
            return Read(response, key);
        return "";
    }

    private static async Task<JsonDocument> PostAsync(HttpClient http, string url, object payload, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, payload, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        // Error responses from the UCM API may be HTTP 200.
        if (doc.RootElement.TryGetProperty("response", out var apiResponse) &&
            apiResponse.ValueKind == JsonValueKind.Object &&
            apiResponse.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int statusCode) && statusCode != 0)
        {
            doc.Dispose();
            throw new InvalidOperationException($"UCM API restituisce status={statusCode}.");
        }
        return doc;
    }
}
