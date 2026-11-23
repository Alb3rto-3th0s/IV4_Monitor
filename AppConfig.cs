using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisorIV4;

public static class Paths
{
    /// <summary>C:\ProgramData\VisorIV4 — configuration, references, logs and WebView2 cache.</summary>
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VisorIV4");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogDir => Path.Combine(DataDir, "Logs");
    public static string WebViewDir => Path.Combine(DataDir, "WebView2");
}

/// <summary>
/// One of the IV4 cameras in the system. Each has its own web viewer URL, its own PLC tags
/// and its own reference images / program names. Only the active one is used by this PC.
/// </summary>
public sealed class CameraProfile : INotifyPropertyChanged
{
    private string _name = "";

    public int Number { get; set; }
    public string Name { get => _name; set { _name = value; Raise(); Raise(nameof(Display)); } }
    /// <summary>IV4-H address, e.g. http://192.168.50.93/ (a page such as /iv4-h-wm-i.html is also accepted).</summary>
    public string Url { get; set; } = "";
    /// <summary>"Image" = poll /iliveimage.jpg directly (fast, default) | "Web" = show the KEYENCE web page.</summary>
    public string ViewerMode { get; set; } = "Image";
    public string ProgramTag { get; set; } = "";
    public string OffsetXTag { get; set; } = "";
    public string OffsetYTag { get; set; } = "";
    public string ReferenceFolder { get; set; } = "";
    public Dictionary<int, string> ProgramNames { get; set; } = new();

    [JsonIgnore] public string Display => $"{Number} · {Name}";

    [JsonIgnore] public bool IsWebMode => ViewerMode.Equals("Web", StringComparison.OrdinalIgnoreCase);

    /// <summary>Live JPEG served by the IV4-H web monitor (same host as Url).</summary>
    [JsonIgnore]
    public string? LiveImageUrl =>
        Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var u) ? new Uri(u, "/iliveimage.jpg").ToString() : null;

    public static CameraProfile Default(int n) => new()
    {
        Number = n,
        Name = $"IV4 #{n}",
        Url = $"http://192.168.0.{9 + n}/",
        ProgramTag = $"IV4_{n}_Program",
        OffsetXTag = $"IV4_{n}_OffsetX",
        OffsetYTag = $"IV4_{n}_OffsetY",
        ReferenceFolder = Path.Combine(Paths.DataDir, "References", $"Cam{n}"),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class AppConfig
{
    public const int CameraSlots = 4;

    // ---- Cameras ----
    public List<CameraProfile> Cameras { get; set; } = new();
    /// <summary>Camera connected to this PC (1..4).</summary>
    public int ActiveCamera { get; set; } = 1;
    public double CameraZoom { get; set; } = 1.0;
    /// <summary>Periodic web viewer reload (minutes). 0 = off.</summary>
    public int CameraReloadMinutes { get; set; } = 0;
    /// <summary>Direct image mode: minimum time between image requests (ms). 0 = as fast as the camera answers.</summary>
    public int ImageIntervalMs { get; set; } = 0;
    /// <summary>Ghost overlay: reference image drawn semi-transparent over the live image (direct image mode only).</summary>
    public bool GhostEnabled { get; set; } = false;
    /// <summary>Ghost overlay opacity in % (5–95).</summary>
    public int GhostOpacity { get; set; } = 40;
    /// <summary>"Watermark" (default) = reference in gray, semi-transparent over the live image;
    /// "Image" = reference in color, semi-transparent; "Outline" = reference edges as green lines.</summary>
    public string GhostStyle { get; set; } = "Watermark";
    [JsonIgnore] public string GhostStyleNorm =>
        string.Equals(GhostStyle, "Image", StringComparison.OrdinalIgnoreCase) ? "Image"
        : string.Equals(GhostStyle, "Outline", StringComparison.OrdinalIgnoreCase) ? "Outline" : "Watermark";

    // ---- Display ----
    /// <summary>"Horizontal" = reference and camera side by side; "Vertical" = one above the other.</summary>
    public string Layout { get; set; } = "Horizontal";
    public bool ReferenceFirst { get; set; } = true;
    public bool Kiosk { get; set; } = false;
    public double ReferenceRatio { get; set; } = 0.5;
    /// <summary>"en" (default) | "es"</summary>
    public string Language { get; set; } = "en";
    /// <summary>"Dark" (default) | "Light" (white background)</summary>
    public string Theme { get; set; } = "Dark";

    // ---- Programs ----
    public int ProgramCount { get; set; } = 20;
    public int ManualProgram { get; set; } = 1;

    // ---- Security ----
    public string AdminPasswordHash { get; set; } = Hash("1234");

    // ---- Allen-Bradley PLC (shared by all cameras) ----
    public PlcConfig Plc { get; set; } = new();

    // ---- Legacy single-camera fields (read once and migrated into Cameras[0]) ----
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CameraUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ReferenceFolder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<int, string>? ProgramNames { get; set; }

    /// <summary>Active camera profile.</summary>
    [JsonIgnore] public CameraProfile Cam => Cameras[Math.Clamp(ActiveCamera, 1, Cameras.Count) - 1];

    public string ProgramName(int n) => ProgramName(Cam, n);

    public static string ProgramName(CameraProfile cam, int n) =>
        cam.ProgramNames.TryGetValue(n, out var s) && !string.IsNullOrWhiteSpace(s) ? s : Loc.F("S_ProgramN", n);

    /// <summary>Guarantees 4 camera profiles and migrates the old single-camera settings.</summary>
    public void Normalize()
    {
        if (Cameras.Count == 0)
        {
            var c1 = CameraProfile.Default(1);
            if (!string.IsNullOrWhiteSpace(CameraUrl)) c1.Url = CameraUrl!;
            if (!string.IsNullOrWhiteSpace(ReferenceFolder)) c1.ReferenceFolder = ReferenceFolder!;
            if (ProgramNames is not null) c1.ProgramNames = ProgramNames;
            if (!string.IsNullOrWhiteSpace(Plc.ProgramTag)) c1.ProgramTag = Plc.ProgramTag!;
            if (!string.IsNullOrWhiteSpace(Plc.OffsetXTag)) c1.OffsetXTag = Plc.OffsetXTag!;
            if (!string.IsNullOrWhiteSpace(Plc.OffsetYTag)) c1.OffsetYTag = Plc.OffsetYTag!;
            Cameras.Add(c1);
        }
        while (Cameras.Count < CameraSlots) Cameras.Add(CameraProfile.Default(Cameras.Count + 1));
        if (Cameras.Count > CameraSlots) Cameras.RemoveRange(CameraSlots, Cameras.Count - CameraSlots);
        for (int i = 0; i < Cameras.Count; i++)
        {
            var c = Cameras[i];
            c.Number = i + 1;
            if (string.IsNullOrWhiteSpace(c.Name)) c.Name = $"IV4 #{i + 1}";
            if (string.IsNullOrWhiteSpace(c.ReferenceFolder)) c.ReferenceFolder = CameraProfile.Default(i + 1).ReferenceFolder;
            c.ProgramNames ??= new();
            c.ViewerMode = c.IsWebMode ? "Web" : "Image";
        }
        ActiveCamera = Math.Clamp(ActiveCamera, 1, CameraSlots);
        CameraUrl = null; ReferenceFolder = null; ProgramNames = null;
        Plc.ProgramTag = null; Plc.OffsetXTag = null; Plc.OffsetYTag = null;
    }

    public static string Hash(string pwd) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("VisorIV4|" + pwd)));

    public bool CheckPassword(string pwd) => Hash(pwd) == AdminPasswordHash;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigFile), JsonOpts);
                if (loaded is not null)
                {
                    bool migrate = loaded.Cameras.Count == 0;
                    loaded.Normalize();
                    if (migrate) { loaded.Save(); Log.Info("Configuration migrated to multi-camera format"); }
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read config.json, using defaults", ex);
        }
        var cfg = new AppConfig();
        cfg.Normalize();
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.DataDir);
        var tmp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, Paths.ConfigFile, overwrite: true);
    }

    public AppConfig Clone()
    {
        var c = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
        c.Normalize();
        return c;
    }
}

public sealed class PlcConfig
{
    public bool Enabled { get; set; } = false;
    public string Ip { get; set; } = "192.168.1.10";
    /// <summary>ControlLogix/CompactLogix: "1,0" (backplane, slot 0). Micro800: empty.</summary>
    public string Path { get; set; } = "1,0";
    /// <summary>ControlLogix (also CompactLogix) | Micro800 | MicroLogix | Slc500 | Plc5</summary>
    public string PlcType { get; set; } = "ControlLogix";
    public int TimeoutMs { get; set; } = 1000;
    public int PollMs { get; set; } = 200;

    public string ProgramType { get; set; } = "DINT";
    /// <summary>Added to the value read (e.g. 1 if the PLC uses programs 0..19).</summary>
    public int ProgramOffset { get; set; } = 0;

    public bool ReadOffsets { get; set; } = true;
    public string OffsetXType { get; set; } = "REAL";
    public string OffsetYType { get; set; } = "REAL";

    public double ToleranceX { get; set; } = 1.0;
    public double ToleranceY { get; set; } = 1.0;
    /// <summary>Inverts the axis so the arrow matches what the operator sees.</summary>
    public bool InvertX { get; set; } = false;
    public bool InvertY { get; set; } = false;
    public string Units { get; set; } = "mm";

    // Legacy (tags now live in each CameraProfile)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProgramTag { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OffsetXTag { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OffsetYTag { get; set; }
}
