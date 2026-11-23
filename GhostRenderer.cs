using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VisorIV4;

/// <summary>Builds the "outline" ghost: the edges of the reference image as colored lines on a transparent
/// background, so the live image underneath stays fully visible.</summary>
public static class GhostRenderer
{
    /// <param name="keepFraction">Share of pixels kept as edges (strongest thinned gradients). 0.025 = 2.5 %.</param>
    public static BitmapSource? Outline(BitmapSource? src, Color color, double keepFraction = 0.025)
    {
        if (src is null) return null;
        try
        {
            var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
            int w = gray.PixelWidth, h = gray.PixelHeight;
            if (w < 8 || h < 8) return null;
            var g = new byte[w * h];
            gray.CopyPixels(g, w, 0);

            // 1) smooth (two 3x3 box passes ≈ light gaussian) to drop surface texture / noise
            var a = BoxBlur(BoxBlur(g, w, h), w, h);

            // 2) Sobel gradient magnitude
            var mag = new int[w * h];
            var horiz = new bool[w * h]; // gradient mostly horizontal → edge runs vertically
            for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                int gx = -a[i - w - 1] - 2 * a[i - 1] - a[i + w - 1] + a[i - w + 1] + 2 * a[i + 1] + a[i + w + 1];
                int gy = -a[i - w - 1] - 2 * a[i - w] - a[i - w + 1] + a[i + w - 1] + 2 * a[i + w] + a[i + w + 1];
                mag[i] = Math.Min(2047, (Math.Abs(gx) + Math.Abs(gy)) / 2);
                horiz[i] = Math.Abs(gx) >= Math.Abs(gy);
            }

            // thin the edges: keep only local maxima across the edge (simple non-maximum suppression)
            var thin = new int[w * h];
            var hist = new int[2048];
            for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x, m = mag[i];
                bool max = horiz[i] ? m >= mag[i - 1] && m >= mag[i + 1] : m >= mag[i - w] && m >= mag[i + w];
                if (!max) continue;
                thin[i] = m;
                hist[m]++;
            }
            mag = thin;

            // 3) threshold: keep the strongest `keepFraction` of the gradients (adapts to each image's contrast)
            long keep = (long)(w * h * keepFraction), acc = 0;
            int thr = 2047;
            while (thr > 1 && acc + hist[thr] < keep) acc += hist[thr--];
            thr = Math.Max(thr, 12);

            // 4) draw the thin edges ~2 px wide (pixel + right/below) so they survive downscaling on screen
            var px = new byte[w * h * 4];
            void Put(int o) { px[o] = color.B; px[o + 1] = color.G; px[o + 2] = color.R; px[o + 3] = 230; }
            for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                if (mag[y * w + x] < thr) continue;
                int o = (y * w + x) * 4;
                Put(o); Put(o + 4); Put(o + w * 4);
            }
            var bmp = BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error("Could not build ghost outline", ex);
            return null;
        }
    }

    /// <summary>Watermark: the reference in grayscale, so the live image keeps its colors and the reference
    /// reads as a faint layer on top.</summary>
    public static BitmapSource? Watermark(BitmapSource? src)
    {
        if (src is null) return null;
        try
        {
            var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            return gray;
        }
        catch (Exception ex)
        {
            Log.Error("Could not build ghost watermark", ex);
            return null;
        }
    }

    private static byte[] BoxBlur(byte[] s, int w, int h)
    {
        var d = (byte[])s.Clone();
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int i = y * w + x;
            int sum = s[i - w - 1] + s[i - w] + s[i - w + 1] + s[i - 1] + s[i] + s[i + 1] + s[i + w - 1] + s[i + w] + s[i + w + 1];
            d[i] = (byte)(sum / 9);
        }
        return d;
    }
}
