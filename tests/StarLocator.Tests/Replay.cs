using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using StarLocator.Core;

namespace StarLocator.Tests
{
    /// <summary>Re-solves a frame saved by the Android app ("Save frames for PC replay").</summary>
    public static class Replay
    {
        public static int Run(string pgmPath)
        {
            string txt = Path.ChangeExtension(pgmPath, ".txt");
            if (!File.Exists(pgmPath) || !File.Exists(txt)) { Console.WriteLine("Need both " + pgmPath + " and " + txt); return 2; }
            var kv = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(txt))
            {
                int i = line.IndexOf('=');
                if (i > 0) kv[line.Substring(0, i)] = line.Substring(i + 1);
            }
            double D(string k) => double.Parse(kv[k], CultureInfo.InvariantCulture);

            (byte[] luma, int w, int h) = ReadPgm(pgmPath);
            var cam = new CameraModel
            {
                Width = w, Height = h, Fx = D("fx"), Fy = D("fy"), Cx = D("cx"), Cy = D("cy"),
                K1 = D("k1"), K2 = D("k2"), K3 = D("k3"), P1 = D("p1"), P2 = D("p2"),
                SensorOrientationDeg = (int)D("sensorOrientation")
            };
            var g = new GravityMeasurement { Mean = new Vec3(D("ax"), D("ay"), D("az")), StdDev = D("astd"), Samples = (int)D("asamples") };
            DateTime utc = DateTime.Parse(kv["utc"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

            var loc = new CelestialLocator();
            FrameResult r = loc.ProcessFrame(luma, w, h, w, cam, g, utc);
            Console.WriteLine($"{w}x{h}, {r.Detections.Count} detections, camera from {kv.GetValueOrDefault("cameraModelSource", "?")}");
            Console.WriteLine(r.Message);
            if (r.Identification != null)
                foreach (var m in r.Identification.Matches)
                    Console.WriteLine($"  {m.Star.DisplayName,-16} mag {m.Star.Mag,5:F2}  residual {m.ResidualDeg:F3}°");
            if (r.Fix != null) Console.WriteLine("FIX: " + r.Fix + "  " + Astronomy.ToDms(r.Fix.LatitudeDeg, true) + " " + Astronomy.ToDms(r.Fix.LongitudeDeg, false));
            return r.Fix != null ? 0 : 1;
        }

        private static (byte[] Data, int W, int H) ReadPgm(string path)
        {
            byte[] all = File.ReadAllBytes(path);
            int pos = 0;
            string Token()
            {
                while (pos < all.Length && (char.IsWhiteSpace((char)all[pos]) || all[pos] == '#'))
                {
                    if (all[pos] == '#') while (pos < all.Length && all[pos] != '\n') pos++;
                    else pos++;
                }
                var sb = new StringBuilder();
                while (pos < all.Length && !char.IsWhiteSpace((char)all[pos])) sb.Append((char)all[pos++]);
                return sb.ToString();
            }
            if (Token() != "P5") throw new InvalidDataException("Not a binary PGM");
            int w = int.Parse(Token(), CultureInfo.InvariantCulture), h = int.Parse(Token(), CultureInfo.InvariantCulture);
            Token(); // maxval
            pos++;   // single whitespace after header
            var data = new byte[w * h];
            Array.Copy(all, pos, data, 0, data.Length);
            return (data, w, h);
        }
    }
}
