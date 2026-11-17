using System;

namespace MedialinkCTIClient;

// A.6.5 LAB: observation-only lease, NEVER an installation authorization.
// It does not prevent inbound calls, SIP operations on phones, or other CTI clients.
internal sealed class UpdateMaintenanceObservation
{
    private readonly object sync = new object();
    private readonly Func<string> amiStatus;
    private string? token;
    private DateTime deadlineUtc;
    private string revokedReason = "NON_PRENOTATO";
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);

    internal UpdateMaintenanceObservation(Func<string> amiStatus) { this.amiStatus = amiStatus ?? throw new ArgumentNullException(nameof(amiStatus)); }

    private bool IsIdle() => amiStatus().StartsWith("NESSUNA CHIAMATA RILEVATA.", StringComparison.Ordinal);

    // Called ONLY after an explicit in-app confirmation by the local user.
    internal string RequestObservedIdle()
    {
        lock (sync)
        {
            token = null;
            if (!IsIdle()) { revokedReason = "AMI_NON_SICURO"; return "A65|DENIED|" + revokedReason; }
            token = Guid.NewGuid().ToString("N");
            deadlineUtc = DateTime.UtcNow.Add(Lifetime);
            revokedReason = "";
            return "A65|OBSERVED_IDLE_LEASE|" + token + "|NO_INSTALL_AUTHORITY";
        }
    }

    // Called on connection, snapshot, call, and outgoing-intent changes.
    internal void Reevaluate()
    {
        lock (sync)
        {
            if (token == null) return;
            if (!IsIdle()) { token = null; revokedReason = "AMI_STATE_CHANGED"; }
            else if (DateTime.UtcNow >= deadlineUtc) { token = null; revokedReason = "LEASE_EXPIRED"; }
        }
    }

    internal void Revoke(string reason)
    {
        lock (sync) { token = null; revokedReason = reason; }
    }

    internal string GetDiagnostic()
    {
        lock (sync)
        {
            if (token == null) return "A65|BLOCKED|" + revokedReason + "|NO_INSTALL_AUTHORITY";
            if (DateTime.UtcNow >= deadlineUtc) { token = null; revokedReason = "LEASE_EXPIRED"; return "A65|BLOCKED|LEASE_EXPIRED|NO_INSTALL_AUTHORITY"; }
            if (!IsIdle()) { token = null; revokedReason = "AMI_STATE_CHANGED"; return "A65|BLOCKED|AMI_STATE_CHANGED|NO_INSTALL_AUTHORITY"; }
            return "A65|OBSERVED_IDLE_ONLY|NO_ATOMIC_CALL_QUIESCE|NO_INSTALL_AUTHORITY";
        }
    }
}
