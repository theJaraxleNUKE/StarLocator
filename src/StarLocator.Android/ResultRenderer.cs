using System;
using System.Collections.Generic;
using Android.Graphics;
using StarLocator.Core;

namespace StarLocator.Droid
{
    /// <summary>Draws a stretched, upright preview of the frame with detected / identified stars.</summary>
    public static class ResultRenderer
    {
        public static Bitmap Render(CapturedFrame f, FrameResult? result, int maxDim)
        {
            int factor = Math.Max(1, (int)Math.Ceiling(Math.Max(f.Width, f.Height) / (double)maxDim));
            int w = f.Width / factor, h = f.Height / factor;

            // Max-pool so single-pixel stars survive the downscale, then stretch the histogram.
            var pooled = new byte[w * h];
            var hist = new int[256];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int m = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int row = (y * factor + dy) * f.RowStride + x * factor;
                        for (int dx = 0; dx < factor; dx++)
                        {
                            int v = f.Luma[row + dx];
                            if (v > m) m = v;
                        }
                    }
                    pooled[y * w + x] = (byte)m;
                    hist[m]++;
                }
            }
            int lo = Percentile(hist, w * h, 0.5), hi = Math.Max(lo + 8, Percentile(hist, w * h, 0.9995));
            var lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                double t = Math.Clamp((i - lo) / (double)(hi - lo), 0, 1);
                int g = (int)(255 * Math.Pow(t, 0.6));
                lut[i] = unchecked((int)0xFF000000) | (g << 16) | (g << 8) | g;
            }
            var px = new int[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = lut[pooled[i]];

            Bitmap raw = Bitmap.CreateBitmap(px, w, h, Bitmap.Config.Argb8888!)!;
            int rot = ((f.Camera.SensorOrientationDeg % 360) + 360) % 360;
            Bitmap upright;
            if (rot != 0)
            {
                var m = new Matrix();
                m.PostRotate(rot);
                upright = Bitmap.CreateBitmap(raw, 0, 0, w, h, m, true)!;
                raw.Recycle();
            }
            else upright = raw;
            Bitmap bmp = upright.Copy(Bitmap.Config.Argb8888!, true)!;
            if (!ReferenceEquals(bmp, upright)) upright.Recycle();

            var canvas = new Canvas(bmp);
            float scale = Math.Max(1f, bmp.Width / 1080f);
            var red = new Paint { AntiAlias = true, StrokeWidth = 2f * scale, Color = Color.Argb(200, 255, 60, 60) };
            red.SetStyle(Paint.Style.Stroke!);
            var green = new Paint { AntiAlias = true, StrokeWidth = 2.5f * scale, Color = Color.Argb(230, 80, 255, 120) };
            green.SetStyle(Paint.Style.Stroke!);
            var text = new Paint { AntiAlias = true, TextSize = 26f * scale, Color = Color.Argb(255, 140, 255, 170) };
            text.SetStyle(Paint.Style.Fill!);

            if (result != null)
            {
                var matched = new HashSet<int>();
                if (result.Identification != null && result.Identification.Success)
                    foreach (var m in result.Identification.Matches) matched.Add(m.DetectionIndex);

                for (int i = 0; i < result.Detections.Count; i++)
                {
                    if (matched.Contains(i)) continue;
                    var d = result.Detections[i];
                    (float x, float y) = Map(d.U / factor, d.V / factor, w, h, rot);
                    canvas.DrawCircle(x, y, 7f * scale, red);
                }
                if (result.Identification != null && result.Identification.Success)
                {
                    foreach (var m in result.Identification.Matches)
                    {
                        var d = result.Detections[m.DetectionIndex];
                        (float x, float y) = Map(d.U / factor, d.V / factor, w, h, rot);
                        canvas.DrawCircle(x, y, 12f * scale, green);
                        if (m.Star.Mag <= 3.0 || (!string.IsNullOrEmpty(m.Star.Name) && m.Star.Mag <= 4.0))
                            canvas.DrawText(m.Star.DisplayName, x + 14f * scale, y - 10f * scale, text);
                    }
                }
            }
            return bmp;
        }

        /// <summary>Maps raw-image coordinates into the image rotated clockwise by rot degrees.</summary>
        private static (float X, float Y) Map(double u, double v, int w, int h, int rot) => rot switch
        {
            90 => ((float)(h - 1 - v), (float)u),
            180 => ((float)(w - 1 - u), (float)(h - 1 - v)),
            270 => ((float)v, (float)(w - 1 - u)),
            _ => ((float)u, (float)v)
        };

        private static int Percentile(int[] hist, int n, double q)
        {
            long target = (long)Math.Ceiling(q * n), acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) return i; }
            return 255;
        }
    }
}
