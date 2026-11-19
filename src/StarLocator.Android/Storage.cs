using System;
using System.Globalization;
using System.IO;
using System.Text;
using Android.Content;
using StarLocator.Core;

namespace StarLocator.Droid
{
    /// <summary>Persists the phone calibration and saves frames for offline replay.</summary>
    public static class Storage
    {
        private const string Prefs = "starlocator";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void SaveCalibration(Context ctx, string cameraId, SensorCalibration cal)
        {
            string v = string.Join(",",
                cal.AccelBias.X.ToString("R", Inv), cal.AccelBias.Y.ToString("R", Inv), cal.AccelBias.Z.ToString("R", Inv),
                cal.CameraMisalignment.X.ToString("R", Inv), cal.CameraMisalignment.Y.ToString("R", Inv), cal.CameraMisalignment.Z.ToString("R", Inv),
                cal.Shots.ToString(Inv), cal.CreatedUtc.Ticks.ToString(Inv));
            ISharedPreferencesEditor e = ctx.GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!;
            e.PutString("cal_" + cameraId, v);
            e.Apply();
        }

        public static SensorCalibration? LoadCalibration(Context ctx, string cameraId)
        {
            string? v = ctx.GetSharedPreferences(Prefs, FileCreationMode.Private)!.GetString("cal_" + cameraId, null);
            if (string.IsNullOrEmpty(v)) return null;
            string[] p = v.Split(',');
            if (p.Length < 8) return null;
            try
            {
                double D(int i) => double.Parse(p[i], Inv);
                return new SensorCalibration
                {
                    AccelBias = new Vec3(D(0), D(1), D(2)),
                    CameraMisalignment = new Vec3(D(3), D(4), D(5)),
                    Shots = int.Parse(p[6], Inv),
                    CreatedUtc = new DateTime(long.Parse(p[7], Inv), DateTimeKind.Utc)
                };
            }
            catch (FormatException) { return null; }
        }

        public static void ClearCalibration(Context ctx, string cameraId)
        {
            ISharedPreferencesEditor e = ctx.GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!;
            e.Remove("cal_" + cameraId);
            e.Apply();
        }

        /// <summary>
        /// Writes the frame as an 8-bit PGM plus a .txt sidecar (camera model, gravity, UTC) into the
        /// app's external files folder (Android/data/&lt;package&gt;/files/frames). The test console can
        /// replay these on a PC:  dotnet run --project tests/StarLocator.Tests -- replay frame.pgm
        /// </summary>
        public static string SaveFrame(Context ctx, CapturedFrame f, GravityMeasurement g)
        {
            string dir = Path.Combine(ctx.GetExternalFilesDir(null)?.AbsolutePath ?? ctx.FilesDir!.AbsolutePath, "frames");
            Directory.CreateDirectory(dir);
            string name = "frame_" + f.UtcMidExposure.ToString("yyyyMMdd_HHmmss", Inv);
            string pgm = Path.Combine(dir, name + ".pgm");
            using (var fs = new FileStream(pgm, FileMode.Create, FileAccess.Write))
            {
                byte[] header = Encoding.ASCII.GetBytes($"P5\n{f.Width} {f.Height}\n255\n");
                fs.Write(header, 0, header.Length);
                for (int y = 0; y < f.Height; y++) fs.Write(f.Luma, y * f.RowStride, f.Width);
            }
            CameraModel c = f.Camera;
            var sb = new StringBuilder();
            void Kv(string k, double v) => sb.Append(k).Append('=').Append(v.ToString("R", Inv)).Append('\n');
            sb.Append("utc=").Append(f.UtcMidExposure.ToString("o", Inv)).Append('\n');
            Kv("width", c.Width); Kv("height", c.Height);
            Kv("fx", c.Fx); Kv("fy", c.Fy); Kv("cx", c.Cx); Kv("cy", c.Cy);
            Kv("k1", c.K1); Kv("k2", c.K2); Kv("k3", c.K3); Kv("p1", c.P1); Kv("p2", c.P2);
            Kv("sensorOrientation", c.SensorOrientationDeg);
            Kv("ax", g.Mean.X); Kv("ay", g.Mean.Y); Kv("az", g.Mean.Z); Kv("astd", g.StdDev); Kv("asamples", g.Samples);
            Kv("exposureNs", f.ExposureNs); Kv("iso", f.Iso);
            sb.Append("cameraModelSource=").Append(f.CameraModelSource).Append('\n');
            File.WriteAllText(Path.Combine(dir, name + ".txt"), sb.ToString());
            return pgm;
        }
    }
}
