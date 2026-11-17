using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace MedialinkCTIClient;

// A.6.2: same-user, READ-ONLY status channel. Never issues exit or install commands.
// The observation is not an authorization to install: AMI state can race new calls.
internal sealed class UpdateStatusPipe : IDisposable
{
    private readonly Func<string> status;
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private Task? worker;
    internal static string PipeName(int pid) => "MLCTI-A62-" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal UpdateStatusPipe(Func<string> readStatus) { status = readStatus ?? throw new ArgumentNullException(nameof(readStatus)); }
    internal void Start() { if (worker != null) throw new InvalidOperationException("Already started"); worker = Task.Run(ServeAsync); }
    private NamedPipeServerStream CreateServer()
    {
#if NETFRAMEWORK
        // .NET Framework 4.8: restrict pipe access to this Windows account.
        var security = new PipeSecurity();
        var sid = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Windows SID missing");
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(PipeName(System.Diagnostics.Process.GetCurrentProcess().Id),
            PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
        return new NamedPipeServerStream(PipeName(System.Diagnostics.Process.GetCurrentProcess().Id),
            PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#endif
    }
    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using (var pipe = CreateServer())
                {
                    await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    if (stop.IsCancellationRequested) break;
                    // Status is snapshotted only when the updater requests it.
                    string value = status();
                    bool idle = value.StartsWith("NESSUNA CHIAMATA RILEVATA.", StringComparison.Ordinal);
                    string line = idle ? "A62|IDLE_OBSERVED_ONLY|NO_INSTALL_AUTHORITY" : "A62|BLOCKED|NO_INSTALL_AUTHORITY";
                    byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");
                    await pipe.WriteAsync(bytes, 0, bytes.Length, stop.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                DiagnosticLog.Error("UPDATE_A62_PIPE_DIAGNOSTIC", ex);
                try { await Task.Delay(1000, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
    public void Dispose()
    {
        stop.Cancel();
        // Do not block WinForms thread on a pending client connection.
        stop.Dispose();
    }
}
