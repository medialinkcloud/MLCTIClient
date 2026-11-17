using System.Drawing;
using System.Windows.Forms;

namespace MedialinkCTIClient;

/// <summary>
/// Non-activating floating call banner. It follows the live AMI state for incoming,
/// outgoing and connected calls. Timeout dismisses the banner only, not the call.
/// </summary>
internal sealed class MiniCallForm : Form
{
    private readonly Func<Task> answer;
    private readonly Func<Task> hangup;
    private readonly Label caption = new(), peerNumber = new(), peerName = new(), elapsed = new(), notice = new();
    private readonly Button answerButton = new(), hangupButton = new(), dismissButton = new();
    private readonly System.Windows.Forms.Timer autoClose = new() { Interval = 500 };
    private string? displayedChannel;
    private DateTime displayedAtUtc;
    private int timeoutSeconds;
    public event Action? Dismissed;

    public MiniCallForm(Func<Task> answerAction, Func<Task> hangupAction)
    {
        answer = answerAction;
        hangup = hangupAction;
        Text = "Medialink CTI · Stato chiamata";
        Size = new Size(MiniPopupLayout.Width, MiniPopupLayout.Height);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(0, 125, 160);
        Padding = new Padding(2);
        var surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        Controls.Add(surface);
        caption.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
        caption.ForeColor = Color.FromArgb(0, 116, 150);
        caption.Location = new Point(12, 7); caption.Size = new Size(296, 23);
        peerNumber.Font = new Font("Segoe UI", MiniPopupLayout.NumberFontPoints, FontStyle.Bold);
        peerNumber.ForeColor = Color.Black;
        peerNumber.AutoEllipsis = true;
        peerNumber.Location = new Point(14, 34); peerNumber.Size = new Size(285, 32);
        peerName.Font = new Font("Segoe UI", MiniPopupLayout.NameFontPoints, FontStyle.Bold);
        peerName.ForeColor = Color.FromArgb(14, 88, 183);
        peerName.AutoEllipsis = true;
        peerName.Location = new Point(14, 70); peerName.Size = new Size(285, 29);
        elapsed.Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold);
        elapsed.Location = new Point(321, 8); elapsed.Size = new Size(63, 22);
        elapsed.TextAlign = ContentAlignment.MiddleRight;
        elapsed.ForeColor = Color.FromArgb(0, 116, 150);
        notice.Location = new Point(14, 109); notice.Size = new Size(399, 17);
        notice.AutoEllipsis = true;
        notice.ForeColor = Color.FromArgb(102, 114, 126);
        notice.Font = new Font("Segoe UI", 8F);
        // Normal AMI/telephone progress belongs only in the diagnostic log.
        // The mini-popup shows this row exclusively for actionable errors.
        notice.Visible = false;
        answerButton.Text = "Rispondi";
        answerButton.Location = new Point(309, 61);
        answerButton.Size = new Size(105, 39);
        answerButton.BackColor = Color.FromArgb(218, 245, 231);
        answerButton.ForeColor = Color.FromArgb(20, 109, 74);
        answerButton.FlatStyle = FlatStyle.Flat;
        answerButton.FlatAppearance.BorderSize = 0;
        answerButton.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
        answerButton.Click += async (_, _) =>
        {
            if (!answerButton.Enabled) return;
            try { await answer(); }
            catch (Exception ex) { DiagnosticLog.Error("MINI_CALL_ANSWER_UNEXPECTED", ex); SetFeedback("Errore risposta: consultare log", true); }
        };
        hangupButton.Text = "Termina";
        hangupButton.Location = answerButton.Location;
        hangupButton.Size = answerButton.Size;
        hangupButton.BackColor = Color.FromArgb(252, 232, 233);
        hangupButton.ForeColor = Color.FromArgb(165, 44, 56);
        hangupButton.FlatStyle = FlatStyle.Flat;
        hangupButton.FlatAppearance.BorderSize = 0;
        hangupButton.Font = answerButton.Font;
        hangupButton.Click += async (_, _) =>
        {
            if (!hangupButton.Enabled) return;
            try { await hangup(); }
            catch (Exception ex) { DiagnosticLog.Error("MINI_CALL_HANGUP_UNEXPECTED", ex); SetFeedback("Errore termine chiamata: consultare log", true); }
        };
        dismissButton.Text = "×";
        dismissButton.Location = new Point(392, 5);
        dismissButton.Size = new Size(27, 23);
        dismissButton.FlatStyle = FlatStyle.Flat;
        dismissButton.FlatAppearance.BorderSize = 0;
        dismissButton.BackColor = Color.White;
        dismissButton.ForeColor = Color.FromArgb(85, 99, 113);
        dismissButton.Click += (_, _) => Dismiss("manual");
        surface.Controls.Add(caption);
        surface.Controls.Add(peerNumber);
        surface.Controls.Add(peerName);
        surface.Controls.Add(elapsed);
        surface.Controls.Add(notice);
        surface.Controls.Add(answerButton);
        surface.Controls.Add(hangupButton);
        surface.Controls.Add(dismissButton);
        autoClose.Tick += (_, _) =>
        {
            if (Visible && MiniPopupRules.TimeoutReached(displayedAtUtc, DateTime.UtcNow, timeoutSeconds))
                Dismiss("timeout");
        };
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>The same channel remains a single notification even when ringing becomes connected.</summary>
    public void Display(string channel, string contactName, string contactNumber, TimeSpan callTime, string phase,
        bool showAnswer, bool canAnswer, bool showHangup, bool canHangup,
        Screen hostScreen, int seconds)
    {
        bool newCall = !string.Equals(displayedChannel, channel, StringComparison.OrdinalIgnoreCase);
        if (newCall)
        {
            displayedChannel = channel;
            displayedAtUtc = DateTime.UtcNow;
            notice.Text = "";
            notice.Visible = false;
            notice.ForeColor = Color.FromArgb(102, 114, 126);
        }
        timeoutSeconds = LegacyCompat.Clamp(seconds, 0, 3600);
        caption.Text = phase;
        peerNumber.Text = CallIdentityPresentation.NumberLine(contactNumber);
        peerName.Text = CallIdentityPresentation.NameLine(contactName);
        elapsed.Text = callTime.TotalHours >= 1 ? callTime.ToString(@"hh\:mm\:ss") : callTime.ToString(@"mm\:ss");
        answerButton.Visible = showAnswer;
        answerButton.Enabled = showAnswer && canAnswer;
        hangupButton.Visible = showHangup;
        hangupButton.Enabled = showHangup && canHangup;
        if (!Visible)
        {
            var a = hostScreen.WorkingArea;
            Location = new Point(a.Right - Width - 14, a.Bottom - Height - 14);
            Show();
            DiagnosticLog.Info("MINI_CALL_SHOWN", "state=" + MiniPopupRules.SafePhase(phase) +
                "; timeout_seconds=" + timeoutSeconds + "; window=hidden_or_minimized");
        }
        if (Visible && MiniPopupRules.TimeoutReached(displayedAtUtc, DateTime.UtcNow, timeoutSeconds))
            Dismiss("timeout");
        else if (Visible && !autoClose.Enabled)
            autoClose.Start();
    }

    private void Dismiss(string reason)
    {
        if (!Visible) return;
        Hide();
        DiagnosticLog.Info("MINI_CALL_DISMISSED", "reason=" + reason);
        Dismissed?.Invoke();
    }

    public void SetFeedback(string message, bool isError)
    {
        // Show only errors in the mini-popup. Informational events remain in the file log.
        notice.Visible = MiniPopupRules.ShouldShowFeedback(message, isError);
        notice.Text = notice.Visible ? message : "";
        if (notice.Visible)
            notice.ForeColor = Color.FromArgb(172, 45, 55);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) autoClose.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { autoClose.Stop(); autoClose.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Pixel layout and font sizes of the compact mini-popup (DPI-scaled by WinForms).</summary>
internal static class MiniPopupLayout
{
    internal const int Width = 430;
    internal const int Height = 132;
    internal const float NumberFontPoints = 16F;
    internal const float NameFontPoints = 14F;

    // Keep enough space for two text rows, a call-action button and the error-only footer.
    internal static bool SelfTest() =>
        Width < 510 && Height < 155 &&
        NumberFontPoints > 11F && NameFontPoints > 10.5F &&
        (14 + 285) < 309 && (309 + 105) < Width &&
        70 > (34 + 32) && (70 + 29) < 109 &&
        (109 + 17) < Height;
}

/// <summary>Pure rules used by the mini popup; no calls to the telephone or UCM.</summary>
internal static class MiniPopupRules
{
    internal static bool ShouldDisplay(LiveCall? call, bool connected) =>
        connected && call is not null;

    // A successful command, waiting state or ordinary event is never a mini-popup notice.
    internal static bool ShouldShowFeedback(string? message, bool isError) =>
        isError && !string.IsNullOrWhiteSpace(message);

    internal static bool TimeoutReached(DateTime shownAtUtc, DateTime nowUtc, int seconds) =>
        seconds > 0 && (nowUtc - shownAtUtc).TotalSeconds >= seconds;

    internal static string Phase(LiveCall call) =>
        !call.DirectionKnown ? "Chiamata rilevata · identificazione in corso" :
        call.ConnectedUtc.HasValue ? (call.Incoming ? "↙  Entrante · in conversazione" : "↗  Uscente · in conversazione") :
        call.Incoming ? "↙  Chiamata in arrivo" : "↗  Chiamata in uscita";

    internal static string SafePhase(string phase) =>
        phase.Contains("identificazione", StringComparison.OrdinalIgnoreCase) ? "identifying" :
        phase.Contains("conversazione", StringComparison.OrdinalIgnoreCase) ? "connected" :
        phase.Contains("uscita", StringComparison.OrdinalIgnoreCase) ? "outgoing" : "ringing";

    internal static bool SelfTest()
    {
        var at = DateTime.UtcNow;
        var incoming = new LiveCall("PJSIP/62-1", "3331234567", true, at, null, 1);
        var outgoing = new LiveCall("PJSIP/62-2", "3331234567", false, at, null, 1);
        var connected = incoming with { ConnectedUtc = at };
        var unknownDirection = outgoing with { DirectionKnown = false };
        return MiniPopupLayout.SelfTest() &&
               ShouldShowFeedback("Errore telefonico", true) &&
               !ShouldShowFeedback("Comando inviato, attendo risposta AMI", false) &&
               !ShouldShowFeedback("", true) &&
               ShouldDisplay(incoming, true) && ShouldDisplay(outgoing, true) &&
               ShouldDisplay(connected, true) && !ShouldDisplay(null, true) &&
               ShouldDisplay(unknownDirection, true) &&
               Phase(unknownDirection).Contains("identificazione") &&
               !ShouldDisplay(outgoing, false) &&
               Phase(incoming).Contains("arrivo") && Phase(outgoing).Contains("uscita") &&
               Phase(connected).Contains("conversazione") &&
               !TimeoutReached(at, at.AddHours(1), 0) &&
               !TimeoutReached(at, at.AddSeconds(19), 20) &&
               TimeoutReached(at, at.AddSeconds(20), 20);
    }
}
