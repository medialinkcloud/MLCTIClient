namespace MedialinkCTIClient;

/// <summary>
/// A Snom hardware hook-release is necessary only when this client explicitly
/// terminates a uniquely identified outgoing call. The other phone vendors use
/// their existing proven AMI Hangup path, unchanged.
/// </summary>
internal static class SnomHangupRules
{
    public static bool NeedsPhysicalRelease(string phoneType, LiveCall? call, bool monitorConnected) =>
        phoneType == "Snom" && monitorConnected && call is not null &&
        call.DirectionKnown && !call.Incoming && call.ActiveChannels == 1;

    internal static bool SelfTest()
    {
        var now = DateTime.UtcNow;
        var outbound = new LiveCall("PJSIP/62-01", "3479784437", false, now, null, 1);
        var inbound = outbound with { Incoming = true };
        var unknown = outbound with { DirectionKnown = false };
        var multi = outbound with { ActiveChannels = 2 };
        return NeedsPhysicalRelease("Snom", outbound, true) &&
            NeedsPhysicalRelease("Snom", outbound with { ConnectedUtc = now }, true) &&
            !NeedsPhysicalRelease("Grandstream", outbound, true) &&
            !NeedsPhysicalRelease("Yealink", outbound, true) &&
            !NeedsPhysicalRelease("Snom", inbound, true) &&
            !NeedsPhysicalRelease("Snom", unknown, true) &&
            !NeedsPhysicalRelease("Snom", multi, true) &&
            !NeedsPhysicalRelease("Snom", outbound, false) &&
            !NeedsPhysicalRelease("Snom", null, true);
    }
}
