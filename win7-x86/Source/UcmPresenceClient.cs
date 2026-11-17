using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MedialinkCTIClient;

internal enum PresenceState { Unknown, Available, Away, Chat, Dnd, Unavailable }

internal static class PresenceRules
{
    public static PresenceState Parse(string? value) => (value ?? "").ToLowerInvariant() switch
    {
        "available" => PresenceState.Available,
        "away" => PresenceState.Away,
        "chat" => PresenceState.Chat,
        "dnd" => PresenceState.Dnd,
        "unavailable" => PresenceState.Unavailable,
        _ => PresenceState.Unknown
    };
    public static string Code(PresenceState state) => state switch
    {
        PresenceState.Available => "available", PresenceState.Away => "away",
        PresenceState.Chat => "chat", PresenceState.Dnd => "dnd",
        PresenceState.Unavailable => "unavailable", _ => throw new InvalidOperationException("Presence non valida")
    };
    public static string Label(PresenceState state) => state switch
    {
        PresenceState.Available => "Disponibile", PresenceState.Away => "Assente",
        PresenceState.Chat => "Chat", PresenceState.Dnd => "Non disturbare",
        PresenceState.Unavailable => "Non disponibile", _ => "Presence sconosciuta"
    };
    public static System.Drawing.Color Color(PresenceState state) => state switch
    {
        PresenceState.Available => System.Drawing.Color.FromArgb(27,171,86),
        PresenceState.Away => System.Drawing.Color.FromArgb(222,150,31),
        PresenceState.Chat => System.Drawing.Color.FromArgb(34,151,188),
        PresenceState.Dnd => System.Drawing.Color.FromArgb(224,55,53),
        PresenceState.Unavailable => System.Drawing.Color.FromArgb(130,135,145),
        _ => System.Drawing.Color.FromArgb(158,164,170)
    };
    public static System.Drawing.Color ColorForIdle(PresenceState state) => state switch
    {
        PresenceState.Unknown => System.Drawing.Color.FromArgb(158,164,170),
        _ => Color(state)
    };
    public static System.Drawing.Bitmap DotImage(PresenceState state)
    {
        var image = new System.Drawing.Bitmap(16,16);
        using var g = System.Drawing.Graphics.FromImage(image);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new System.Drawing.SolidBrush(Color(state));
        g.FillEllipse(brush,2,2,12,12);
        return image;
    }
}

// UCM HTTPS API; non registra mai cookie/password o dati account nei log.
internal sealed class UcmPresenceClient
{
    private static string Prop(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        return "";
    }
    private static async Task<JsonDocument> Send(HttpClient http, string endpoint, object request, CancellationToken ct)
    {
        using var response = await http.PostAsync(endpoint,
            new StringContent(JsonSerializer.Serialize(new { request }), Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        return JsonDocument.Parse(stream);
    }
    private static void Check(JsonElement root)
    {
        if (Prop(root,"status") != "0") throw new InvalidOperationException("La UCM ha rifiutato la richiesta API");
    }
    private static string Nested(JsonElement root, string container, string name)
    {
        if (root.TryGetProperty(container, out var body)) return Prop(body,name);
        return "";
    }
    private static async Task<string> Login(HttpClient http,string url,AppConfig cfg,CancellationToken ct)
    {
        using var challenge = await Send(http,url,new {action="challenge",user=cfg.ApiUser,version="1.0"},ct);
        var nonce=Nested(challenge.RootElement,"response","challenge");
        if (string.IsNullOrEmpty(nonce)) throw new InvalidOperationException("Challenge API UCM assente");
        using var md5=MD5.Create();
        var bytes=md5.ComputeHash(Encoding.UTF8.GetBytes(nonce+cfg.ApiSecret));
        var token=BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        using var login=await Send(http,url,new {action="login",user=cfg.ApiUser,token},ct);
        var cookie=Nested(login.RootElement,"response","cookie");
        if (string.IsNullOrEmpty(cookie)) throw new InvalidOperationException("Login API UCM rifiutato");
        return cookie;
    }
    public async Task<Dictionary<string,PresenceState>> ReadAsync(AppConfig cfg,IReadOnlyCollection<string> extensions,CancellationToken ct)
    {
        var result=new Dictionary<string,PresenceState>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(cfg.ApiUser)||string.IsNullOrEmpty(cfg.ApiSecret)) return result;
        using var handler=new HttpClientHandler();
        if(cfg.AllowUntrustedApiCertificate) handler.ServerCertificateCustomValidationCallback=(_,_,_,_)=>true;
        using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(18)};
        string url="https://"+cfg.ApiHost+":"+cfg.ApiPort+"/api";
        var cookie=await Login(http,url,cfg,ct);
        try
        {
            foreach(var ext in extensions)
            {
                ct.ThrowIfCancellationRequested();
                if (ext.Length < 2 || ext.Length > 18 || !AllDigits(ext)) continue;
                using var doc=await Send(http,url,new {action="getSIPAccount",cookie,extension=ext},ct);
                if (Prop(doc.RootElement,"status")!="0") continue;
                if (!doc.RootElement.TryGetProperty("response",out var response) ||
                    response.ValueKind != JsonValueKind.Object ||
                    !response.TryGetProperty("extension",out var account)) continue;
                // getSIPAccount: response.extension è un oggetto. Non fare fallback
                // a "available" quando il campo manca o non è valido.
                if (account.ValueKind != JsonValueKind.Object) continue;
                result[ext]=PresenceRules.Parse(Prop(account,"presence_status").Trim());
            }
        }
        finally
        {
            try { using var logout=await Send(http,url,new {action="logout",cookie},CancellationToken.None); }
            catch { /* session expiry */ }
        }
        return result;
    }
    // Modifica solo la SIP Presence dell'interno configurato.
    // Non esegue applyChanges globale né altera le regole di inoltro esistenti.
    public async Task<(bool Applied,PresenceState Verified)> SetOwnAsync(AppConfig cfg,PresenceState state,CancellationToken ct)
    {
        if (state==PresenceState.Unknown || cfg.Extension.Length<2 || cfg.Extension.Length>18 || !AllDigits(cfg.Extension))
            throw new InvalidOperationException("Interno o Presence non valida");
        if (string.IsNullOrWhiteSpace(cfg.ApiUser)||string.IsNullOrEmpty(cfg.ApiSecret))
            throw new InvalidOperationException("Credenziali HTTPS API mancanti");
        using var handler=new HttpClientHandler();
        if(cfg.AllowUntrustedApiCertificate) handler.ServerCertificateCustomValidationCallback=(_,_,_,_)=>true;
        using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(18)};
        string url="https://"+cfg.ApiHost+":"+cfg.ApiPort+"/api";
        var cookie=await Login(http,url,cfg,ct);
        try
        {
            // La UCM richiede l'intera lista sip_presence_settings per updateSIPAccount.
            // Leggerla prima e riportarla IDENTICA, senza ricreare o azzerare i forwarding.
            using var before=await Send(http,url,new {action="getSIPAccount",cookie,extension=cfg.Extension},ct);
            Check(before.RootElement);
            if (!before.RootElement.TryGetProperty("response",out var response) ||
                response.ValueKind!=JsonValueKind.Object ||
                !response.TryGetProperty("extension",out var account) ||
                account.ValueKind!=JsonValueKind.Object)
                throw new InvalidOperationException("Risposta getSIPAccount incompleta");
            if (!response.TryGetProperty("sip_presence_settings",out var settings) ||
                settings.ValueKind!=JsonValueKind.Array || settings.GetArrayLength()==0)
                throw new InvalidOperationException("sip_presence_settings assente: aggiornamento annullato per sicurezza");
            foreach(var item in settings.EnumerateArray())
            {
                if (item.ValueKind!=JsonValueKind.Object || string.IsNullOrWhiteSpace(Prop(item,"presence_status")))
                    throw new InvalidOperationException("Configurazione Presence incompleta: aggiornamento annullato");
            }
            var beforeState=PresenceRules.Parse(Prop(account,"presence_status").Trim());
            if (beforeState==state) return (true,state);

            // Il nuovo stato è separato dalle definizioni/forwarding dei singoli stati.
            // JsonElement.Clone preserva valori, null, regole e tipologie dell'elenco originale.
            using var update=await Send(http,url,new {
                action="updateSIPAccount",cookie,extension=cfg.Extension,
                presence_status=PresenceRules.Code(state),
                sip_presence_settings=settings.Clone()
            },ct);
            Check(update.RootElement);

            var applyRequired=Prop(update.RootElement,"need_apply").Equals("yes",StringComparison.OrdinalIgnoreCase);
            if (update.RootElement.TryGetProperty("response",out var updateResponse))
                applyRequired |= Prop(updateResponse,"need_apply").Equals("yes",StringComparison.OrdinalIgnoreCase);

            // Mai confermare per la sola risposta HTTP/API: rileggere la UCM.
            PresenceState verified=PresenceState.Unknown;
            for(int attempt=0;attempt<3;attempt++)
            {
                if(attempt>0) await Task.Delay(600,ct);
                using var check=await Send(http,url,new {action="getSIPAccount",cookie,extension=cfg.Extension},ct);
                Check(check.RootElement);
                if(check.RootElement.TryGetProperty("response",out var body) &&
                   body.ValueKind==JsonValueKind.Object &&
                   body.TryGetProperty("extension",out var current) &&
                   current.ValueKind==JsonValueKind.Object)
                    verified=PresenceRules.Parse(Prop(current,"presence_status").Trim());
                if(verified==state) break;
            }
            DiagnosticLog.Info("PRESENCE_SET_RESULT",
                "verified="+(verified==state)+"; need_apply="+applyRequired);
            return (verified==state,verified);
        }
        finally
        {
            try { using var logout=await Send(http,url,new {action="logout",cookie},CancellationToken.None); }
            catch { /* session expiry */ }
        }
    }
    private static bool AllDigits(string s)
    {
        if(s.Length==0) return false;
        foreach(var c in s) if(c<'0'||c>'9')return false;
        return true;
    }
}
