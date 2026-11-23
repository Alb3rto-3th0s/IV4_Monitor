using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace VisorIV4;

/// <summary>
/// Direct image mode: requests the IV4-H live JPEG (/iliveimage.jpg) in a loop, one request at a time,
/// over a single kept-alive connection (the web monitor allows max. 2 simultaneous clients).
/// The KEYENCE web page refreshes it only every 2 s; asking directly gives ~3 images/s.
/// </summary>
public sealed class LiveImageService : IDisposable
{
    private readonly string _url;
    private readonly int _minIntervalMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;

    /// <summary>New decoded (frozen) frame and measured frames per second.</summary>
    public event Action<BitmapSource, double>? Frame;
    /// <summary>Request failed; argument = consecutive failures, message.</summary>
    public event Action<int, string>? Failed;

    public LiveImageService(string url, int minIntervalMs)
    {
        _url = url;
        _minIntervalMs = Math.Max(0, minIntervalMs);
        _http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        { Timeout = TimeSpan.FromSeconds(3) };
    }

    public void Start() => Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        int failures = 0;
        var fpsWatch = Stopwatch.StartNew();
        int framesInWindow = 0;
        double fps = 0;

        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // cache-buster like the KEYENCE page does
                var bytes = await _http.GetByteArrayAsync($"{_url}?{DateTime.UtcNow.Ticks}", ct);
                var bmp = Decode(bytes);

                framesInWindow++;
                if (fpsWatch.ElapsedMilliseconds >= 1000)
                {
                    fps = framesInWindow * 1000.0 / fpsWatch.ElapsedMilliseconds;
                    framesInWindow = 0;
                    fpsWatch.Restart();
                }
                if (failures > 0) Log.Info($"Live image recovered after {failures} failed request(s)");
                failures = 0;
                Frame?.Invoke(bmp, fps);

                int wait = _minIntervalMs - (int)sw.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1) Log.Error($"Live image request failed ({_url})", ex);
                Failed?.Invoke(failures, ex.Message);
                fps = 0; framesInWindow = 0; fpsWatch.Restart();
                try { await Task.Delay(failures < 5 ? 500 : 2000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public void Dispose()
    {
        Frame = null;
        Failed = null;
        _cts.Cancel();
        _http.Dispose();
    }
}
