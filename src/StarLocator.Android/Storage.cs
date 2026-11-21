using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Android.Content;
using StarLocator.Core;

namespace StarLocator.Droid
{
    /// <summary>Persists calibration and the learned focal-length correction; saves and exports frames.</summary>
    public static class Storage
    {
        private const string Prefs = "starlocator";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static ISharedPreferences P(Context ctx) => ctx.GetSharedPreferences(Prefs, FileCreationMode.Private)!;

        // ---- Calibration ------------------------------------------------------------------------

        /// <summary>
        /// Calibrations are stored per camera AND per capture configuration. Version 1.0.7 turned optical
        /// stabilisation off, which moves the image centre, so calibrations made before that ("cal_")
        /// are deliberately no longer loaded.
        /// </summary>
        private const string CalPrefix = "cal2_";

        /// <summary>True if a calibration from a version before 1.0.7 exists for this camera.</summary>
        public static bool HasObsoleteCalibration(Context ctx, string cameraId) =>
            !string.IsNullOrEmpty(P(ctx).GetString("cal_" + cameraId, null));

        public static void SaveCalibration(Context ctx, string cameraId, SensorCalibration cal)
        {
            var kv = new Dictionary<string, string>
            {
                ["v"] = "2",
                ["bx"] = R(cal.AccelBias.X), ["by"] = R(cal.AccelBias.Y), ["bz"] = R(cal.AccelBias.Z),
                ["wx"] = R(cal.CameraMisalignment.X), ["wy"] = R(cal.CameraMisalignment.Y), ["wz"] = R(cal.CameraMisalignment.Z),
                ["px"] = R(cal.PoseUp.X), ["py"] = R(cal.PoseUp.Y), ["pz"] = R(cal.PoseUp.Z),
                ["pose"] = cal.PoseSpecific ? "1" : "0",
                ["shots"] = cal.Shots.ToString(Inv),
                ["ticks"] = cal.CreatedUtc.Ticks.ToString(Inv),
                ["temp"] = R(cal.TemperatureC),
                ["err"] = R(cal.ErrorBeforeKm),
                ["spread"] = R(cal.SpreadDeg),
                ["offered"] = cal.ShotsOffered.ToString(Inv),
                ["fs"] = R(cal.FocalScale),
                ["set"] = cal.CaptureSettings.Replace(";", ",").Replace("=", "-"),
                ["method"] = cal.Method.Replace(";", ",").Replace("=", "-")
            };
            var sb = new StringBuilder();
            foreach (var p in kv) sb.Append(p.Key).Append('=').Append(p.Value).Append(';');
            ISharedPreferencesEditor e = P(ctx).Edit()!;
            e.PutString(CalPrefix + cameraId, sb.ToString());
            e.Remove("cal_" + cameraId);
            e.Apply();
        }

        public static SensorCalibration? LoadCalibration(Context ctx, string cameraId)
        {
            string? v = P(ctx).GetString(CalPrefix + cameraId, null);
            if (string.IsNullOrEmpty(v)) return null;
            try
            {
                if (!v.StartsWith("v=", StringComparison.Ordinal))
                {
                    // Version 1 (comma separated, multi-pose only).
                    string[] p = v.Split(',');
                    if (p.Length < 8) return null;
                    double D1(int i) => double.Parse(p[i], Inv);
                    return new SensorCalibration
                    {
                        AccelBias = new Vec3(D1(0), D1(1), D1(2)),
                        CameraMisalignment = new Vec3(D1(3), D1(4), D1(5)),
                        Shots = int.Parse(p[6], Inv),
                        CreatedUtc = new DateTime(long.Parse(p[7], Inv), DateTimeKind.Utc),
                        Method = "multi-pose (no reference)"
                    };
                }
                var kv = new Dictionary<string, string>();
                foreach (string part in v.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int i = part.IndexOf('=');
                    if (i > 0) kv[part.Substring(0, i)] = part.Substring(i + 1);
                }
                double D(string k) => double.Parse(kv[k], Inv);
                return new SensorCalibration
                {
                    AccelBias = new Vec3(D("bx"), D("by"), D("bz")),
                    CameraMisalignment = new Vec3(D("wx"), D("wy"), D("wz")),
                    PoseUp = new Vec3(D("px"), D("py"), D("pz")),
                    PoseSpecific = kv["pose"] == "1",
                    Shots = int.Parse(kv["shots"], Inv),
                    CreatedUtc = new DateTime(long.Parse(kv["ticks"], Inv), DateTimeKind.Utc),
                    TemperatureC = D("temp"),
                    ErrorBeforeKm = D("err"),
                    SpreadDeg = kv.ContainsKey("spread") ? D("spread") : double.NaN,
                    ShotsOffered = kv.ContainsKey("offered") ? int.Parse(kv["offered"], Inv) : 0,
                    FocalScale = kv.ContainsKey("fs") ? D("fs") : double.NaN,
                    CaptureSettings = kv.TryGetValue("set", out string? set) ? set : "",
                    Method = kv["method"]
                };
            }
            catch (Exception ex) when (ex is FormatException || ex is KeyNotFoundException || ex is OverflowException)
            {
                return null;
            }
        }

        public static void ClearCalibration(Context ctx, string cameraId)
        {
            ISharedPreferencesEditor e = P(ctx).Edit()!;
            e.Remove("cal_" + cameraId);
            e.Remove(CalPrefix + cameraId);
            e.Apply();
        }

        // ---- Simple settings ----------------------------------------------------------------------

        public static string GetSetting(Context ctx, string key, string fallback) =>
            P(ctx).GetString("set_" + key, null) ?? fallback;

        public static void SetSetting(Context ctx, string key, string value)
        {
            ISharedPreferencesEditor e = P(ctx).Edit()!;
            e.PutString("set_" + key, value);
            e.Apply();
        }

        /// <summary>Number of files and total megabytes in a folder.</summary>
        public static (int Files, double Megabytes) FolderSize(string dir)
        {
            if (!Directory.Exists(dir)) return (0, 0);
            long bytes = 0;
            string[] files = Directory.GetFiles(dir);
            foreach (string f in files) bytes += new FileInfo(f).Length;
            return (files.Length, bytes / 1048576.0);
        }

        // ---- Learned focal-length correction ------------------------------------------------------

        /// <summary>Factor to apply to the phone's reported focal length (1.0 until a capture solves).</summary>
        public static double LoadFocalScale(Context ctx, string cameraId)
        {
            string? v = P(ctx).GetString("focal_" + cameraId, null);
            return v != null && double.TryParse(v, NumberStyles.Float, Inv, out double f) && f > 0.5 && f < 2.0 ? f : 1.0;
        }

        public static void SaveFocalScale(Context ctx, string cameraId, double scale)
        {
            ISharedPreferencesEditor e = P(ctx).Edit()!;
            e.PutString("focal_" + cameraId, R(scale));
            e.Apply();
        }

        // ---- Frames -------------------------------------------------------------------------------

        /// <summary>Folder holding the most recent capture (replaced by every capture).</summary>
        // ---- Fix history --------------------------------------------------------------------------
        // Kept in the app's private storage (not the shared frames folder): a location history is personal.

        private const int MaxHistory = 1000;
        private static string HistoryPath(Context ctx) => Path.Combine(ctx.FilesDir!.AbsolutePath, "history.tsv");

        /// <summary>Appends a fix to the history (oldest entries beyond 1000 are dropped).</summary>
        public static void AddHistory(Context ctx, HistoryEntry e)
        {
            string path = HistoryPath(ctx);
            File.AppendAllText(path, e.ToLine() + "\n", new UTF8Encoding(false));
            string[] lines = File.ReadAllLines(path);
            if (lines.Length > MaxHistory)
                File.WriteAllLines(path, lines[(lines.Length - MaxHistory)..], new UTF8Encoding(false));
        }

        /// <summary>All saved fixes, newest first. Unreadable lines are skipped.</summary>
        public static List<HistoryEntry> LoadHistory(Context ctx)
        {
            var list = new List<HistoryEntry>();
            string path = HistoryPath(ctx);
            if (!File.Exists(path)) return list;
            foreach (string line in File.ReadAllLines(path))
                if (HistoryEntry.TryParse(line, out HistoryEntry? e)) list.Add(e!);
            list.Reverse();
            return list;
        }

        /// <summary>Removes one fix (matched by its time stamp and position).</summary>
        public static void DeleteHistory(Context ctx, HistoryEntry e)
        {
            string path = HistoryPath(ctx);
            if (!File.Exists(path)) return;
            string target = e.ToLine();
            var keep = new List<string>();
            bool removed = false;
            foreach (string line in File.ReadAllLines(path))
            {
                if (!removed && line == target) { removed = true; continue; }
                keep.Add(line);
            }
            File.WriteAllLines(path, keep, new UTF8Encoding(false));
        }

        public static void ClearHistory(Context ctx)
        {
            string path = HistoryPath(ctx);
            if (File.Exists(path)) File.Delete(path);
        }

        public static string LastCaptureDir(Context ctx) => Path.Combine(BaseDir(ctx), "last");

        /// <summary>Folder for permanently kept frames ("Save frames for PC replay").</summary>
        public static string KeptFramesDir(Context ctx) => Path.Combine(BaseDir(ctx), "frames");

        private static string BaseDir(Context ctx) =>
            ctx.GetExternalFilesDir(null)?.AbsolutePath ?? ctx.FilesDir!.AbsolutePath;

        public static void ClearDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
        }

        /// <summary>
        /// Writes the frame as an 8-bit PGM plus a .txt sidecar (camera model, gravity, UTC).
        /// The test console can replay these on a PC:  dotnet run --project tests/StarLocator.Tests -- replay frame.pgm
        /// </summary>
        public static string SaveFrame(string dir, CapturedFrame f, GravityMeasurement g)
        {
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
            sb.Append("preciseTiming=").Append(f.PreciseTiming ? "1" : "0").Append('\n');
            foreach (var d in f.Diagnostics) sb.Append(d.Key).Append('=').Append(d.Value.Replace('\n', ' ')).Append('\n');
            File.WriteAllText(Path.Combine(dir, name + ".txt"), sb.ToString());
            return pgm;
        }

        /// <summary>Writes the human-readable capture report next to the frames.</summary>
        public static void SaveReport(string dir, string name, string text)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
        }

        /// <summary>Zips every file of a folder (plus a summary) into the given stream. Returns the file count.</summary>
        public static int WriteZip(Stream output, string dir, string summary)
        {
            int n = 0;
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                if (Directory.Exists(dir))
                {
                    foreach (string path in Directory.GetFiles(dir))
                    {
                        ZipArchiveEntry entry = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Fastest);
                        using Stream es = entry.Open();
                        using FileStream fs = File.OpenRead(path);
                        fs.CopyTo(es);
                        n++;
                    }
                }
                ZipArchiveEntry s = zip.CreateEntry("summary.txt", CompressionLevel.Fastest);
                using (var w = new StreamWriter(s.Open(), new UTF8Encoding(false))) w.Write(summary);
            }
            return n;
        }

        private static string R(double v) => v.ToString("R", Inv);
    }

    /// <summary>One fix in the history list.</summary>
    public sealed class HistoryEntry
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public DateTime Utc { get; init; }
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double UncertaintyKm { get; init; }
        public int Frames { get; init; }
        public bool Calibrated { get; init; }
        public int Warnings { get; init; }

        public string ToLine() => string.Join("\t",
            Utc.ToString("o", Inv), Latitude.ToString("F6", Inv), Longitude.ToString("F6", Inv),
            UncertaintyKm.ToString("F2", Inv), Frames.ToString(Inv), Calibrated ? "1" : "0", Warnings.ToString(Inv));

        public static bool TryParse(string line, out HistoryEntry? e)
        {
            e = null;
            string[] f = line.Split('\t');
            if (f.Length < 7) return false;
            if (!DateTime.TryParse(f[0], Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc)) return false;
            if (!double.TryParse(f[1], NumberStyles.Float, Inv, out double lat) || !double.TryParse(f[2], NumberStyles.Float, Inv, out double lon)) return false;
            double.TryParse(f[3], NumberStyles.Float, Inv, out double unc);
            int.TryParse(f[4], NumberStyles.Integer, Inv, out int frames);
            int.TryParse(f[6], NumberStyles.Integer, Inv, out int warnings);
            e = new HistoryEntry { Utc = utc, Latitude = lat, Longitude = lon, UncertaintyKm = unc, Frames = frames, Calibrated = f[5] == "1", Warnings = warnings };
            return true;
        }
    }
}
