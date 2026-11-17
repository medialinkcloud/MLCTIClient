namespace MedialinkCTIClient;

/// <summary>Never send a direct Yealink hangup on AMI events or an ambiguous call.
/// Only on an explicit user request to terminate one identifiable outgoing call.</summary>
internal static class YealinkHangupRules
{
    internal static bool NeedsPhysicalRelease(string phoneType, LiveCall? call, bool monitorConnected) =>
        phoneType == "Yealink" && monitorConnected && call is not null &&
        call.DirectionKnown && !call.Incoming && call.ActiveChannels == 1;

    internal static bool SelfTest()
    {
        var call = new LiveCall("PJSIP/62-01", "3479784437", false, DateTime.UtcNow, null, 1);
        return NeedsPhysicalRelease("Yealink", call, true) &&
            !NeedsPhysicalRelease("Snom", call, true) &&
            !NeedsPhysicalRelease("Yealink", call with { Incoming = true }, true) &&
            !NeedsPhysicalRelease("Yealink", call with { ActiveChannels = 2 }, true) &&
            !NeedsPhysicalRelease("Yealink", call with { DirectionKnown = false }, true) &&
            !NeedsPhysicalRelease("Yealink", call, false) &&
            !NeedsPhysicalRelease("Yealink", null, true);
    }
}
