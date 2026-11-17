namespace MedialinkCTIClient;

/// <summary>
/// Which command belongs to the call's *current* AMI state.
/// For an incoming ringing call only Answer is displayed; after Up, only Hangup.
/// Outgoing call setup is cancellable via Hangup; idle/disconnected has no actions.
/// </summary>
internal readonly record struct LiveCallActions(bool ShowAnswer, bool ShowHangup)
{
    public static LiveCallActions For(LiveCall? call, bool monitorConnected)
    {
        if (!monitorConnected || call is null || !call.DirectionKnown) return new(false, false);
        return call.Incoming && !call.ConnectedUtc.HasValue
            ? new(true, false)
            : new(false, true);
    }

    /// <summary>Answer controls the uniquely resolved SIP phone, not an AMI leg.
    /// Multiple local channels may represent forked ringing of the same extension.
    /// Do not apply the single-channel safety rule used by AMI Hangup here.</summary>
    public static bool CanAnswerOnDevice(LiveCall? call, bool monitorConnected,
        bool phoneConfigured, bool answerPending) =>
        For(call, monitorConnected).ShowAnswer && phoneConfigured && !answerPending;

    internal static bool SelfTest()
    {
        var now = DateTime.UtcNow;
        var incoming = new LiveCall("PJSIP/62-0001", "3331234567", true, now, null, 1);
        var outgoing = new LiveCall("PJSIP/62-0002", "3331234567", false, now, null, 1);
        var connected = incoming with { ConnectedUtc = now };
        var forkedIncoming = incoming with { ActiveChannels = 2 };
        var unknown = incoming with { DirectionKnown = false };
        var idle = For(null, true);
        var offline = For(incoming, false);
        var pending = For(unknown, true);
        var ringing = For(incoming, true);
        var active = For(connected, true);
        var dialing = For(outgoing, true);
        return !idle.ShowAnswer && !idle.ShowHangup &&
               !offline.ShowAnswer && !offline.ShowHangup &&
               !pending.ShowAnswer && !pending.ShowHangup &&
               ringing.ShowAnswer && !ringing.ShowHangup &&
               !active.ShowAnswer && active.ShowHangup &&
               !dialing.ShowAnswer && dialing.ShowHangup &&
               CanAnswerOnDevice(incoming, true, true, false) &&
               CanAnswerOnDevice(forkedIncoming, true, true, false) &&
               !CanAnswerOnDevice(forkedIncoming, true, false, false) &&
               !CanAnswerOnDevice(forkedIncoming, true, true, true) &&
               !CanAnswerOnDevice(forkedIncoming, false, true, false) &&
               !CanAnswerOnDevice(connected, true, true, false) &&
               !CanAnswerOnDevice(outgoing, true, true, false);
    }
}
