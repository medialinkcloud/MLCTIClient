using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MedialinkCTIClient;

internal sealed record AvayaBlfRow(string Extension, string Name, string Status);

internal static class AvayaGatewayClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static Uri Validate(AppConfig config)
    {
        if (!Uri.TryCreate(config.AvayaGatewayUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
            throw new InvalidOperationException("Avaya: indicare un URL Gateway HTTPS o HTTP su loopback, senza percorso.");

        if (config.AvayaGatewayKey.Length is < 32 or > 256 ||
            config.AvayaGatewayKey.Any(c => c <= ' ' || c >= 127))
            throw new InvalidOperationException("Avaya: chiave API Gateway assente o non valida.");
        return uri;
    }

    internal static async Task<List<Contact>> SearchAsync(
        AppConfig config, string query, CancellationToken cancellation)
    {
        var rows = await ReadAsync<DirectoryRow>(config, "/v1/avaya/directory", cancellation)
            .ConfigureAwait(false);
        query = query.Trim();
        return rows.Where(row => row.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                row.Number.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(row => new Contact(row.Name, row.Number, "Avaya")).ToList();
    }

    internal static Task<AvayaBlfRow[]> BlfAsync(AppConfig config) =>
        ReadAsync<AvayaBlfRow>(config, "/v1/avaya/blf", CancellationToken.None);

    internal static BlfState MapStatus(string status) => status switch
    {
        "idle" => BlfState.Free,
        "busy" => BlfState.Busy,
        _ => BlfState.Unknown
    };

    internal static async Task DialAsync(AppConfig config, string number)
    {
        if (!PhoneNumbers.ValidExtension(config.Extension) ||
            !Regex.IsMatch(number, @"\A\+?[0-9*#]{2,30}\z"))
            throw new InvalidOperationException("Avaya: interno o destinazione non validi.");

        using var request = CreateRequest(config, HttpMethod.Post, "/v1/calls");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            extension = config.Extension,
            number
        }), Encoding.UTF8, "application/json");

        using var reply = await Http.SendAsync(request).ConfigureAwait(false);
        if (reply.StatusCode != HttpStatusCode.Accepted)
            throw new InvalidOperationException($"Gateway Avaya: chiamata non accettata (HTTP {(int)reply.StatusCode}).");
    }

    private static async Task<T[]> ReadAsync<T>(AppConfig config, string route, CancellationToken cancellation)
    {
        using var request = CreateRequest(config, HttpMethod.Get, route);
        using var reply = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (!reply.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gateway Avaya: HTTP {(int)reply.StatusCode}.");

        var payload = await reply.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<T[]>(payload, Json)
            ?? throw new InvalidOperationException("Gateway Avaya: risposta JSON vuota.");
    }

    private static HttpRequestMessage CreateRequest(AppConfig config, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, new Uri(Validate(config), route));
        request.Headers.Add("X-Api-Key", config.AvayaGatewayKey);
        return request;
    }

    private sealed record DirectoryRow(string Name, string Number);
}
