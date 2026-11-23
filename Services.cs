using System.IO;
using System.Windows.Media.Imaging;
using libplctag;

namespace VisorIV4;

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogDir);
                var file = Path.Combine(Paths.LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* nunca tumbar la app por el log */ }
    }
}

/// <summary>Imágenes de referencia: una por programa, guardadas como P01.png … P20.png.</summary>
public static class ReferenceStore
{
    private static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp" };

    public static string? Find(string folder, int program) =>
        Exts.Select(e => Path.Combine(folder, $"P{program:00}{e}")).FirstOrDefault(File.Exists);

    public static void Import(string folder, int program, string source)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"P{program:00}{Path.GetExtension(source).ToLowerInvariant()}");
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return;
        var tmp = target + ".tmp";
        File.Copy(source, tmp, overwrite: true);
        Remove(folder, program);
        File.Move(tmp, target, overwrite: true);
    }

    public static void Remove(string folder, int program)
    {
        foreach (var e in Exts)
        {
            var p = Path.Combine(folder, $"P{program:00}{e}");
            if (File.Exists(p)) File.Delete(p);
        }
    }

    /// <summary>Carga sin bloquear el archivo (se puede reemplazar mientras se muestra).</summary>
    public static BitmapImage? Load(string? path, int decodeWidth = 0)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.UriSource = new Uri(path);
            if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not load image {path}", ex);
            return null;
        }
    }
}

public sealed record PlcSnapshot(bool Connected, int? Program, double? X, double? Y, string? Error);

/// <summary>Lee programa activo y desplazamientos X/Y desde un PLC Allen-Bradley (EtherNet/IP, solo lectura).</summary>
public sealed class PlcService : IDisposable
{
    private readonly PlcConfig _cfg;
    private readonly string _progTag, _xTag, _yTag;
    private readonly CancellationTokenSource _cts = new();
    private Tag? _prog, _x, _y;

    public event Action<PlcSnapshot>? Updated;

    /// <summary>Connection settings are shared; tags come from the active camera profile.</summary>
    public PlcService(PlcConfig cfg, CameraProfile cam)
    {
        _cfg = cfg;
        _progTag = cam.ProgramTag;
        _xTag = cam.OffsetXTag;
        _yTag = cam.OffsetYTag;
    }

    public void Start() => Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _prog ??= Make(_progTag, _cfg.ProgramType);
                await _prog.ReadAsync(ct);
                int program = (int)Math.Round(ReadValue(_prog, _cfg.ProgramType)) + _cfg.ProgramOffset;

                double? x = null, y = null;
                if (_cfg.ReadOffsets)
                {
                    _x ??= Make(_xTag, _cfg.OffsetXType);
                    _y ??= Make(_yTag, _cfg.OffsetYType);
                    await _x.ReadAsync(ct);
                    await _y.ReadAsync(ct);
                    x = ReadValue(_x, _cfg.OffsetXType);
                    y = ReadValue(_y, _cfg.OffsetYType);
                }

                if (lastError is not null) Log.Info("PLC reconnected");
                lastError = null;
                Updated?.Invoke(new PlcSnapshot(true, program, x, y, null));
                await Task.Delay(Math.Max(50, _cfg.PollMs), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ex.Message != lastError) Log.Error("Error reading PLC", ex);
                lastError = ex.Message;
                DisposeTags();
                Updated?.Invoke(new PlcSnapshot(false, null, null, null, ex.Message));
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }
        DisposeTags();
    }

    private Tag Make(string name, string type) => new()
    {
        Name = name,
        Gateway = _cfg.Ip,
        Path = string.IsNullOrWhiteSpace(_cfg.Path) ? null : _cfg.Path,
        PlcType = Enum.Parse<PlcType>(_cfg.PlcType, ignoreCase: true),
        Protocol = Protocol.ab_eip,
        Timeout = TimeSpan.FromMilliseconds(_cfg.TimeoutMs),
        ElementSize = SizeOf(type),
        ElementCount = 1,
    };

    public static int SizeOf(string type) => type.ToUpperInvariant() switch
    {
        "SINT" => 1,
        "INT" => 2,
        "LREAL" => 8,
        _ => 4, // DINT, REAL
    };

    private static double ReadValue(Tag t, string type) => type.ToUpperInvariant() switch
    {
        "SINT" => t.GetInt8(0),
        "INT" => t.GetInt16(0),
        "REAL" => t.GetFloat32(0),
        "LREAL" => t.GetFloat64(0),
        _ => t.GetInt32(0),
    };

    private void DisposeTags()
    {
        _prog?.Dispose(); _x?.Dispose(); _y?.Dispose();
        _prog = _x = _y = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        Updated = null;
    }
}
