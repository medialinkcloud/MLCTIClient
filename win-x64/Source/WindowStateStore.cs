using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace MedialinkCTIClient;

// Finestra in file separato: nessun salvataggio/ricifratura delle password per il resize.
internal static class WindowStateStore
{
    public sealed class WindowBounds
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
    private static readonly string FilePath = Path.Combine(ConfigStore.DirectoryPath, "window.json");

    public static Rectangle? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(FilePath));
            if (value is null || value.Width is < 450 or > 4000 || value.Height is < 400 or > 3000)
                return null;
            return ClampToScreen(new Rectangle(value.X, value.Y, value.Width, value.Height));
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("WINDOW_SETTINGS_LOAD_FAILED", e);
            return null;
        }
    }

    public static Rectangle ClampToScreen(Rectangle requested)
    {
        // Schermi scollegati e risoluzioni cambiate: tenere la finestra raggiungibile.
        var nearest = Screen.FromRectangle(requested).WorkingArea;
        var width = Math.Min(Math.Max(requested.Width, 450), nearest.Width);
        var height = Math.Min(Math.Max(requested.Height, 400), nearest.Height);
        return new Rectangle(
            Math.Clamp(requested.X, nearest.Left, nearest.Right - width),
            Math.Clamp(requested.Y, nearest.Top, nearest.Bottom - height), width, height);
    }

    public static void Save(Rectangle value)
    {
        if (value.Width < 450 || value.Height < 400) return;
        try
        {
            Directory.CreateDirectory(ConfigStore.DirectoryPath);
            var json = JsonSerializer.Serialize(new WindowBounds { X = value.X, Y = value.Y, Width = value.Width, Height = value.Height });
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, true);
        }
        catch (Exception e) { DiagnosticLog.Error("WINDOW_SETTINGS_SAVE_FAILED", e); }
    }
}
