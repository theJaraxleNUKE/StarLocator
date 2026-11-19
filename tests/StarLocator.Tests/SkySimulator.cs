using System;
using System.Collections.Generic;
using StarLocator.Core;

namespace StarLocator.Tests
{
    /// <summary>Scenario parameters for one synthetic photo.</summary>
    public sealed class Scenario
    {
        public double LatDeg, LonDeg;
        public DateTime Utc;
        public double AltitudeDeg = 70, AzimuthDeg = 30, RollDeg = 0;
        public double LimitingMag = 4.8;
        public double Background = 25, NoiseSigma = 3, GradientTop = 35;
        public double PsfSigma = 1.3;
        public int Outliers = 4;
        public double CloudFraction = 0.0;
        public Vec3 AccelBias = Vec3.Zero;
        public double AccelNoise = 0.02;
        /// <summary>Random per-star position error (pixels, 1 sigma): models atmosphere, optics, centroiding.</summary>
        public double PositionJitterPx = 0.0;
        /// <summary>Camera module mounting error relative to the accelerometer (axis * angle, radians).</summary>
        public Vec3 CameraMisalignment = Vec3.Zero;
        public int Seed = 1;
    }

    public sealed class SyntheticFrame
    {
        public byte[] Luma = Array.Empty<byte>();
        public int Width, Height;
        public GravityMeasurement Gravity = null!;
        public Mat3 TrueEcefToDevice;
        public int VisibleStars;
    }

    /// <summary>Renders a phone night-sky photo for a known position, time and phone orientation.</summary>
    public static class SkySimulator
    {
        public static SyntheticFrame Render(StarCatalog catalog, CameraModel trueCamera, Scenario sc)
        {
            var rnd = new Random(sc.Seed);
            double lat = sc.LatDeg * Astronomy.DegToRad, lon = sc.LonDeg * Astronomy.DegToRad;
            Vec3 zen = Vec3.FromSpherical(lon, lat);
            Vec3 east = new Vec3(-Math.Sin(lon), Math.Cos(lon), 0);
            Vec3 north = Vec3.Cross(zen, east);
            double alt = sc.AltitudeDeg * Astronomy.DegToRad, az = sc.AzimuthDeg * Astronomy.DegToRad;
            Vec3 bore = (north * Math.Cos(az) + east * Math.Sin(az)) * Math.Cos(alt) + zen * Math.Sin(alt);

            // Device axes in ECEF: camera looks along -Z.
            Vec3 zAxis = -bore;
            Vec3 yAxis = zen - bore * Vec3.Dot(zen, bore);
            if (yAxis.Length < 1e-6) yAxis = north;
            yAxis = yAxis.Normalized();
            yAxis = Mat3.AxisAngle(bore, sc.RollDeg * Astronomy.DegToRad) * yAxis;
            Vec3 xAxis = Vec3.Cross(yAxis, zAxis).Normalized();
            Mat3 ecefToDevice = Mat3.FromRows(xAxis, yAxis, zAxis);
            Mat3 j2000ToDevice = ecefToDevice * Astronomy.J2000ToEarthFixed(sc.Utc);
            Vec3 upDev = ecefToDevice * zen;
            double mis = sc.CameraMisalignment.Length;
            Mat3 q = mis > 0 ? Mat3.AxisAngle(sc.CameraMisalignment, mis) : Mat3.Identity;

            int w = trueCamera.Width, h = trueCamera.Height;
            var img = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                double bgRow = sc.GradientTop + (sc.Background - sc.GradientTop) * y / (double)h;
                for (int x = 0; x < w; x++) img[y * w + x] = (float)(bgRow + Gauss(rnd) * sc.NoiseSigma);
            }

            // Cloud: a random half-plane hides stars.
            double cAng = rnd.NextDouble() * 2 * Math.PI;
            double cOff = (sc.CloudFraction - 0.5) * Math.Sqrt(w * w + h * h);
            bool Cloudy(double u, double v) =>
                sc.CloudFraction > 0 && ((u - w / 2.0) * Math.Cos(cAng) + (v - h / 2.0) * Math.Sin(cAng)) < cOff;

            int visible = 0;
            foreach (CatalogStar s in catalog.Stars)
            {
                if (s.Mag > sc.LimitingMag + 0.5) continue;
                Vec3 d = j2000ToDevice * s.Unit;
                if (Vec3.Dot(d, upDev) < Math.Sin(5 * Astronomy.DegToRad)) continue;
                d = Astronomy.ApplyRefraction(d, upDev);
                if (!trueCamera.DeviceRayToPixel(q * d, out double u, out double v)) continue;
                if (u < 8 || v < 8 || u > w - 9 || v > h - 9) continue;
                if (Cloudy(u, v)) continue;
                // Peak so that LimitingMag lands at ~4 sigma of the noise.
                double peak = 4.0 * sc.NoiseSigma * Math.Pow(10, -0.4 * (s.Mag - sc.LimitingMag));
                u += Gauss(rnd) * sc.PositionJitterPx; v += Gauss(rnd) * sc.PositionJitterPx;
                DrawGaussian(img, w, h, u, v, peak, sc.PsfSigma);
                if (s.Mag <= sc.LimitingMag) visible++;
            }
            for (int i = 0; i < sc.Outliers; i++)
            {
                double u = 20 + rnd.NextDouble() * (w - 40), v = 20 + rnd.NextDouble() * (h - 40);
                DrawGaussian(img, w, h, u, v, 60 + rnd.NextDouble() * 200, sc.PsfSigma * (1 + rnd.NextDouble()));
            }

            var luma = new byte[w * h];
            for (int i = 0; i < luma.Length; i++) luma[i] = (byte)Math.Clamp((int)Math.Round(img[i]), 0, 255);

            Vec3 aMean = upDev * PositionSolver.StandardGravity + sc.AccelBias +
                         new Vec3(Gauss(rnd), Gauss(rnd), Gauss(rnd)) * (sc.AccelNoise / Math.Sqrt(100));
            return new SyntheticFrame
            {
                Luma = luma, Width = w, Height = h,
                Gravity = new GravityMeasurement { Mean = aMean, StdDev = sc.AccelNoise, Samples = 100 },
                TrueEcefToDevice = ecefToDevice,
                VisibleStars = visible
            };
        }

        private static void DrawGaussian(float[] img, int w, int h, double u, double v, double peak, double sigma)
        {
            int r = (int)Math.Ceiling(4 * sigma);
            int x0 = Math.Max(0, (int)u - r), x1 = Math.Min(w - 1, (int)u + r);
            int y0 = Math.Max(0, (int)v - r), y1 = Math.Min(h - 1, (int)v + r);
            double inv = 1.0 / (2 * sigma * sigma);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - u, dy = y - v;
                    img[y * w + x] += (float)(peak * Math.Exp(-(dx * dx + dy * dy) * inv));
                }
        }

        private static double Gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }
}
