using System;
using System.Collections.Generic;

namespace StarLocator.Core
{
    /// <summary>A star (or star-like blob) found in an image, with a sub-pixel centroid.</summary>
    public sealed class DetectedStar
    {
        public double U { get; init; }
        public double V { get; init; }
        public double Flux { get; init; }
        public int Area { get; init; }
        public int Peak { get; init; }
        /// <summary>Ratio of the blob's long to short axis (flux-weighted second moments); 1 = round.</summary>
        public double Elongation { get; init; } = 1.0;
    }

    public sealed class DetectorOptions
    {
        /// <summary>Detection threshold above local background, in robust standard deviations.</summary>
        public double SigmaThreshold { get; set; } = 5.0;
        /// <summary>Minimum absolute threshold above background (8-bit units).</summary>
        public int MinAboveBackground { get; set; } = 6;
        /// <summary>Background is estimated per square block of this size.</summary>
        public int BlockSize { get; set; } = 64;
        /// <summary>Blobs with fewer pixels are treated as hot pixels / noise.</summary>
        public int MinArea { get; set; } = 2;
        /// <summary>Blobs with more pixels (moon, street lights, buildings) are rejected. 0 = automatic.</summary>
        public int MaxArea { get; set; } = 0;
        /// <summary>Pixels this close to the border are ignored.</summary>
        public int BorderMargin { get; set; } = 4;
        /// <summary>
        /// Blobs longer than this ratio (long/short axis) are rejected: lit tree branches, wires and
        /// aircraft trails, which otherwise crowd real stars out of the brightest-N list. Stars are
        /// round, or slightly drawn out by sky rotation in long exposures. 0 = keep all.
        /// </summary>
        public double MaxElongation { get; set; } = 3.5;
        /// <summary>Only the brightest N blobs are returned.</summary>
        public int MaxStars { get; set; } = 80;
    }

    /// <summary>
    /// Step 1 of the paper's pipeline: find star centres with sub-pixel accuracy.
    /// Local background + robust noise per block, threshold, 8-connected components,
    /// background-subtracted intensity-weighted centroids.
    /// </summary>
    public static class StarDetector
    {
        public static List<DetectedStar> Detect(byte[] luma, int width, int height, int rowStride, DetectorOptions? options = null)
        {
            DetectorOptions o = options ?? new DetectorOptions();
            if (rowStride < width) throw new ArgumentException("rowStride < width");
            if (luma.Length < (long)rowStride * (height - 1) + width) throw new ArgumentException("buffer too small");

            int bs = Math.Max(16, o.BlockSize);
            int bw = (width + bs - 1) / bs, bh = (height + bs - 1) / bs;
            var bg = new float[bw * bh];
            var thr = new float[bw * bh];
            var hist = new int[256];

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    Array.Clear(hist, 0, 256);
                    int x0 = bx * bs, y0 = by * bs;
                    int x1 = Math.Min(width, x0 + bs), y1 = Math.Min(height, y0 + bs);
                    int n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * rowStride;
                        for (int x = x0; x < x1; x++) { hist[luma[row + x]]++; n++; }
                    }
                    double median = Percentile(hist, n, 0.5);
                    // Robust sigma from the lower half of the distribution (stars only add a bright tail).
                    double p16 = Percentile(hist, n, 0.1587);
                    double sigma = Math.Max(1.0, median - p16);
                    bg[by * bw + bx] = (float)median;
                    thr[by * bw + bx] = (float)(median + Math.Max(o.MinAboveBackground, o.SigmaThreshold * sigma));
                }
            }

            int maxArea = o.MaxArea > 0 ? o.MaxArea : Math.Max(400, width * height / 20000);
            var visited = new bool[width * height];
            var stack = new Stack<int>(1024);
            var found = new List<DetectedStar>();
            int m = Math.Max(1, o.BorderMargin);

            for (int y = m; y < height - m; y++)
            {
                int row = y * rowStride;
                int brow = (y / bs) * bw;
                for (int x = m; x < width - m; x++)
                {
                    int idx = y * width + x;
                    if (visited[idx]) continue;
                    if (luma[row + x] <= thr[brow + x / bs]) continue;

                    // Flood fill this component.
                    double sumW = 0, sumX = 0, sumY = 0, sumXX = 0, sumYY = 0, sumXY = 0;
                    int area = 0, peak = 0;
                    bool touchesBorder = false;
                    visited[idx] = true;
                    stack.Push(idx);
                    while (stack.Count > 0)
                    {
                        int p = stack.Pop();
                        int py = p / width, px = p - py * width;
                        int val = luma[py * rowStride + px];
                        int bIdx = (py / bs) * bw + px / bs;
                        double w = val - bg[bIdx];
                        if (w > 0)
                        {
                            sumW += w; sumX += w * px; sumY += w * py;
                            sumXX += w * px * px; sumYY += w * py * py; sumXY += w * px * py;
                        }
                        area++;
                        if (val > peak) peak = val;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = py + dy;
                            if (ny < m || ny >= height - m) { touchesBorder = true; continue; }
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = px + dx;
                                if (nx < m || nx >= width - m) { touchesBorder = true; continue; }
                                int nIdx = ny * width + nx;
                                if (visited[nIdx]) continue;
                                int nb = (ny / bs) * bw + nx / bs;
                                if (luma[ny * rowStride + nx] <= thr[nb]) continue;
                                visited[nIdx] = true;
                                stack.Push(nIdx);
                            }
                        }
                    }

                    if (area < o.MinArea || area > maxArea || touchesBorder || sumW <= 0) continue;
                    double cu = sumX / sumW, cv = sumY / sumW;
                    // Covariance of the blob (+1/12 px² for pixel size) -> axis ratio.
                    double vxx = sumXX / sumW - cu * cu + 1.0 / 12, vyy = sumYY / sumW - cv * cv + 1.0 / 12, vxy = sumXY / sumW - cu * cv;
                    double tr = vxx + vyy, det = vxx * vyy - vxy * vxy;
                    double disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
                    double l1 = tr / 2 + disc, l2 = Math.Max(tr / 2 - disc, 1e-6);
                    double elong = Math.Sqrt(Math.Max(1.0, l1 / l2));
                    if (o.MaxElongation > 0 && elong > o.MaxElongation) continue;
                    found.Add(new DetectedStar
                    {
                        U = cu,
                        V = cv,
                        Flux = sumW,
                        Area = area,
                        Peak = peak,
                        Elongation = elong
                    });
                }
            }

            found.Sort((a, b) => b.Flux.CompareTo(a.Flux));
            if (found.Count > o.MaxStars) found.RemoveRange(o.MaxStars, found.Count - o.MaxStars);
            return found;
        }

        private static double Percentile(int[] hist, int n, double q)
        {
            long target = (long)Math.Ceiling(q * n);
            long acc = 0;
            for (int i = 0; i < 256; i++)
            {
                acc += hist[i];
                if (acc >= target) return i;
            }
            return 255;
        }
    }
}
