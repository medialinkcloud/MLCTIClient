using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MedialinkCTIClient;

/// <summary>Windows-wide opt-in key combination. Registration is local to this process,
/// and is released on shutdown/settings changes. No low-level keyboard hooks, keylogging,
/// background clipboard monitoring or external listener are installed.</summary>
internal static class GlobalHotkeyRules
{
    internal static bool TryParse(string? gesture, out uint modifiers, out Keys key)
    {
        modifiers = 0; key = Keys.None;
        if (string.IsNullOrWhiteSpace(gesture) || gesture.Length > 40) return false;
        string[] parts = gesture.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
        if (parts.Length is < 2 or > 4) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            uint mod = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 0x0002U,
                "ALT" => 0x0001U,
                "SHIFT" => 0x0004U,
                _ => 0
            };
            if (mod == 0 || (modifiers & mod) != 0) return false;
            modifiers |= mod;
        }
        // Shift alone is insufficient: avoid stealing normal typing/capitalization.
        if ((modifiers & (0x0001U | 0x0002U)) == 0) return false;
        string name = parts[parts.Length - 1].ToUpperInvariant();
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z')
            key = (Keys)name[0];
        else if (Regex.IsMatch(name, @"^F(?:[1-9]|1[0-2])$", RegexOptions.CultureInvariant) &&
            Enum.TryParse<Keys>(name, true, out var functionKey))
            key = functionKey;
        else return false;
        // Do not capture system-standard editing commands globally.
        if (modifiers == 0x0002U && key is Keys.C or Keys.V or Keys.X or Keys.A or Keys.Z)
            return false;
        return true;
    }

    internal static bool SelfTest() =>
        TryParse("Ctrl+D", out var c, out var d) && c == 2 && d == Keys.D &&
        TryParse("Ctrl+Shift+D", out var cs, out var d2) && cs == 6 && d2 == Keys.D &&
        TryParse("Ctrl+Alt+F8", out var ca, out var f8) && ca == 3 && f8 == Keys.F8 &&
        !TryParse("Shift+D", out _, out _) && !TryParse("Ctrl+C", out _, out _) &&
        !TryParse("D", out _, out _) && !TryParse("Ctrl+Ctrl+D", out _, out _);
}

internal sealed class GlobalDialHotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312, HotkeyId = 0x4D4C;
    private const uint ModNoRepeat = 0x4000;
    private readonly Func<Task> action;
    private bool registered;
    private bool disposed;
    private int handling;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    internal GlobalDialHotkey(string gesture, Func<Task> callback)
    {
        action = callback;
        if (!GlobalHotkeyRules.TryParse(gesture, out uint mods, out Keys key))
            throw new ArgumentException("Scorciatoia globale non valida.", nameof(gesture));
        // Message-only HWND, owned by the UI thread; not visible in taskbar or Alt+Tab.
        CreateHandle(new CreateParams { Caption = "MLCTI Hotkey", Parent = (nint)(-3) });
        registered = RegisterHotKey(Handle, HotkeyId, mods | ModNoRepeat, (uint)key);
        if (!registered)
        {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new InvalidOperationException("Scorciatoia già occupata o non registrabile (Win32 " + error + ").");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == HotkeyId)
        {
            if (Interlocked.CompareExchange(ref handling, 1, 0) == 0)
                _ = DispatchAsync();
            return;
        }
        base.WndProc(ref m);
    }

    private async Task DispatchAsync()
    {
        try { if (!disposed) await action(); }
        catch (Exception e) { DiagnosticLog.Error("EXTERNAL_HOTKEY_DISPATCH_FAILED", e); }
        finally { Interlocked.Exchange(ref handling, 0); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (registered && Handle != IntPtr.Zero) UnregisterHotKey(Handle, HotkeyId);
        registered = false;
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}

/// <summary>Reads only the currently selected text after an explicit user hotkey.
/// The previous clipboard data object is restored on a best-effort basis.</summary>
internal static class ClipboardDialSelection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }
    // Win32 INPUT contains a union whose largest member is MOUSEINPUT.
    // On win-x64 this must be 40 bytes (the old sequential Type+KEYBDINPUT was 32).
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    private static bool CopySelection()
    {
        const ushort ctrl = 0x11, c = 0x43;
        const uint keyUp = 0x0002;
        var inputs = new[]
        {
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = ctrl } } },
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = c } } },
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = c, Flags = keyUp } } },
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = ctrl, Flags = keyUp } } }
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            DiagnosticLog.Warn("EXTERNAL_HOTKEY_SENDINPUT_FAILED", "win32_error=" + Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    internal static bool TryExtractSingleNumber(string? selection, out string number)
    {
        number = "";
        if (string.IsNullOrWhiteSpace(selection) || selection.Length > 2048) return false;
        string value = selection.Trim();
        if (ExternalDialUri.TryParse(value, out number) || ExternalDialUri.TryValidateNumber(value, out number))
            return true;
        // The selection may contain visual telephone separators (/, en dash, etc.)
        // or a short label such as "Tel:". Only extract exactly one numeric token.
        // Do not remove arbitrary letters *inside* a candidate number.
        static bool ValidSelectedToken(string token) =>
            Regex.IsMatch(token, @"^\+?[0-9 ./()\-–—]{1,80}$", RegexOptions.CultureInvariant) &&
            PhoneNumbers.Dialable(token).Count(LegacyCompat.IsAsciiDigit) is >= 1 and <= 32 &&
            PhoneNumbers.ValidDialString(PhoneNumbers.Dialable(token));
        if (ValidSelectedToken(value))
        {
            number = PhoneNumbers.Dialable(value);
            return true;
        }
        var matches = Regex.Matches(value, @"(?<![\p{L}\d+])\+?\d[\d ./()\-–—]{0,50}\d(?![\p{L}\d])",
            RegexOptions.CultureInvariant);
        var candidates = matches.Cast<Match>().Select(m => m.Value.Trim())
            .Where(ValidSelectedToken)
            .Select(PhoneNumbers.Dialable)
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        if (candidates.Length != 1) return false;
        number = candidates[0];
        return true;
    }

    internal static async Task<string?> CaptureAsync(string gesture)
    {
        // Wait for the user's modifier keys and hotkey to be released before
        // synthesizing Ctrl+C, or the two shortcuts can overlap.
        if (!GlobalHotkeyRules.TryParse(gesture, out _, out Keys trigger))
            throw new InvalidOperationException("Scorciatoia non valida.");
        bool released = false;
        for (int attempt = 0; attempt < 25; attempt++)
        {
            if ((GetAsyncKeyState((int)trigger) & 0x8000) == 0 &&
                (GetAsyncKeyState(0x11) & 0x8000) == 0 &&
                (GetAsyncKeyState(0x12) & 0x8000) == 0 &&
                (GetAsyncKeyState(0x10) & 0x8000) == 0)
            { released = true; break; }
            await Task.Delay(50);
        }
        if (!released) throw new InvalidOperationException("Rilascia la scorciatoia e riprova.");
        // Must be invoked on the WinForms STA context, not inside Task.Run.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Lettura selezione possibile solo sul thread UI STA.");
        System.Windows.Forms.IDataObject? previous = Clipboard.GetDataObject();
        uint before = GetClipboardSequenceNumber();
        bool copied = false;
        try
        {
            if (!CopySelection()) throw new InvalidOperationException("Il comando Copia non è stato inviato.");
            // Outlook and browsers can update clipboard asynchronously.
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(85);
                if (GetClipboardSequenceNumber() != before) { copied = true; break; }
            }
            if (!copied) throw new InvalidOperationException("Nessun testo selezionato è stato copiato.");
            string text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (!TryExtractSingleNumber(text, out string number))
                throw new InvalidOperationException("Seleziona un unico numero telefonico e riprova.");
            DiagnosticLog.Info("EXTERNAL_HOTKEY_SELECTION_VALID", "source=selection; digits_not_logged=true");
            return number;
        }
        finally
        {
            // Restore even on selection validation failure. Do not log clipboard contents.
            if (copied)
            {
                try
                {
                    if (previous is not null) Clipboard.SetDataObject(previous, true);
                    else Clipboard.Clear();
                }
                catch (Exception e) { DiagnosticLog.Error("EXTERNAL_HOTKEY_CLIPBOARD_RESTORE_FAILED", e); }
            }
        }
    }

    internal static bool SelfTest() =>
        Marshal.SizeOf<Input>() == (IntPtr.Size == 8 ? 40 : 28) &&
        TryExtractSingleNumber("3479784437", out var a) && a == "3479784437" &&
        TryExtractSingleNumber("Chiamare: +39 347 9784437", out var b) && b == "+393479784437" &&
        TryExtractSingleNumber("t. +39 0444/417433", out var tel) && tel == "+390444417433" &&
        TryExtractSingleNumber("62", out var c) && c == "62" &&
        !TryExtractSingleNumber("Tel 62, cellulare 3479784437", out _) &&
        !TryExtractSingleNumber("Nessun recapito", out _);
}
