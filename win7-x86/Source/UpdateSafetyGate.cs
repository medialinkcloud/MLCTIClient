using System;

namespace MedialinkCTIClient;

// A.6.1: advisory, fail-closed diagnostic gate. It never authorizes execution.
// AMI events are not a transactional lock on call state; a future updater must
// recheck live state immediately before closing and during the handoff.
internal sealed class UpdateSafetyGate
{
    private readonly object sync = new object();
    private bool connected;
    private bool snapshotComplete;
    private bool hasCall;
    private DateTime lastOutgoingIntentUtc = DateTime.MinValue;

    internal void Reset()
    {
        lock (sync) { connected = false; snapshotComplete = false; hasCall = false; lastOutgoingIntentUtc = DateTime.MinValue; }
    }
    internal void SetConnection(bool value)
    {
        lock (sync)
        {
            connected = value;
            // Even a new connection must wait for CoreShowChannelsComplete.
            snapshotComplete = false;
            hasCall = false;
        }
    }
    internal void SetSnapshotComplete()
    {
        lock (sync) { if (connected) snapshotComplete = true; }
    }
    internal void SetCall(LiveCall? call)
    {
        lock (sync) { hasCall = call != null; }
    }
    internal void OutgoingIntent()
    {
        lock (sync) { lastOutgoingIntentUtc = DateTime.UtcNow; }
    }
    internal string GetStatus()
    {
        lock (sync)
        {
            if (!connected) return "BLOCCATO: monitor AMI non connesso.";
            if (!snapshotComplete) return "BLOCCATO: snapshot iniziale AMI incompleto.";
            if (hasCall) return "BLOCCATO: chiamata in corso o in preparazione.";
            if ((DateTime.UtcNow - lastOutgoingIntentUtc) < TimeSpan.FromSeconds(30))
                return "BLOCCATO: tentativo di chiamata recente.";
            return "NESSUNA CHIAMATA RILEVATA. Installazione ancora DISABILITATA in A.6.1.";
        }
    }
}
