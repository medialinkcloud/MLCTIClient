using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace MedialinkCTIClient;

/// <summary>
/// Local IPC for the per-session single-instance tray client.
/// PipeOptions.CurrentUserOnly limits access to the same Windows user;
/// every request is validated again and always requires an on-screen confirmation.
/// </summary>
internal static class ExternalDialPipe
{
    private static string PipeName => "MedialinkCTIClientExternalDial_S" +
        Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static PipeSecurity UserOnlyPipeSecurity()
    {
        // Deny other logon identities, even with identical session IDs.
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("SID utente Windows mancante");
        var security = new PipeSecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return security;
    }

    public static async Task<bool> SendAsync(string number)
    {
        if (!ExternalDialUri.TryValidateNumber(number, out var canonical)) return false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(2500).WithCancellation(cts.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 256, leaveOpen: true)
                { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 256, leaveOpen: true);
            await writer.WriteLineAsync(canonical).WithCancellation(cts.Token).ConfigureAwait(false);
            var response = await reader.ReadLineAsync().WithCancellation(cts.Token).ConfigureAwait(false);
            return string.Equals(response, "OK", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("EXTERNAL_DIAL_PIPE_UNAVAILABLE", e.GetType().Name);
            return false;
        }
    }

    public static async Task RunAsync(Action<string> onNumber, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, UserOnlyPipeSecurity());
                await pipe.WaitForConnectionAsync().WithCancellation(ct).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 256, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, leaveOpen: true)
                    { NewLine = "\n", AutoFlush = true };
                string? request = await reader.ReadLineAsync().WithCancellation(requestTimeout.Token).ConfigureAwait(false);
                string response = "ERR";
                if (ExternalDialUri.TryValidateNumber(request, out var number))
                {
                    try { onNumber(number); response = "OK"; }
                    catch (Exception error) { DiagnosticLog.Error("EXTERNAL_DIAL_QUEUE_FAILED", error); }
                }
                else DiagnosticLog.Warn("EXTERNAL_DIAL_PIPE_REJECTED", "invalid_number=true");
                await writer.WriteLineAsync(response).WithCancellation(requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { DiagnosticLog.Warn("EXTERNAL_DIAL_PIPE_TIMEOUT"); }
            catch (Exception e)
            {
                DiagnosticLog.Error("EXTERNAL_DIAL_PIPE_SERVER_FAILED", e);
                // Never busy-loop after a pipe error.
                try { await Task.Delay(900, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
