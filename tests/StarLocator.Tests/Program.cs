using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using StarLocator.Core;

namespace StarLocator.Tests
{
    public static class Program
    {
        private static int _failures;

        public static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            if (args.Length > 0 && args[0] == "sweep") { Sweep(); return 0; }
            if (args.Length > 1 && args[0] == "replay") return Replay.Run(args[1]);
            TestAstronomy();
            TestWahba();
            TestCamera();
            TestCoordinateParser();
            TestDetectorRejectsStreaks();

            var sw = Stopwatch.StartNew();
            var locator = new CelestialLocator();
            Console.WriteLine($"Hash table: {locator.Hash.Stars.Count} stars, {locator.Hash.TriangleCount:N0} triangles, built in {sw.ElapsedMilliseconds} ms");

            TestReplay(locator);
            TestSingleShots(locator);
            TestMultiShot(locator);
            TestBracket(locator);
            TestWideFocal(locator);
            TestKnownLocationCalibration(locator);
            TestImageStability(locator);

            Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TEST(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
            if (!ok) _failures++;
        }

        private static void TestAstronomy()
        {
            Console.WriteLine("Astronomy");
            // Meeus, Astronomical Algorithms, Example 12.a: 1987-04-10 0h UT -> GMST 13h10m46.3668s
            double gmst = Astronomy.Gmst(new DateTime(1987, 4, 10, 0, 0, 0, DateTimeKind.Utc)) * Astronomy.RadToDeg;
            double expected = (13 + 10 / 60.0 + 46.3668 / 3600.0) * 15.0;
            Check(Math.Abs(gmst - expected) < 1e-4, $"GMST {gmst:F6}° vs Meeus {expected:F6}°");

            // Meeus Example 21.b (theta Persei), proper motion pre-applied: J2000 -> 2028-11-13.19 TD
            double ra0 = (2 + 44 / 60.0 + 11.986 / 3600.0) * 15.0 + 0.03425 * 28.86705 * 15.0 / 3600.0;
            double dec0 = (49 + 13 / 60.0 + 42.48 / 3600.0) - 0.0895 * 28.86705 / 3600.0;
            var t = new DateTime(2028, 11, 13, 4, 33, 36, DateTimeKind.Utc); // 13.19 d (TD~UT within a minute)
            Vec3 r = Astronomy.PrecessionFromJ2000(t) * Vec3.FromSpherical(ra0 * Astronomy.DegToRad, dec0 * Astronomy.DegToRad);
            (double ra, double dec) = r.ToSpherical();
            double raDeg = ra * Astronomy.RadToDeg, decDeg = dec * Astronomy.RadToDeg;
            double raMeeus = (2 + 46 / 60.0 + 11.331 / 3600.0) * 15.0, decMeeus = 49 + 20 / 60.0 + 54.54 / 3600.0;
            Check(Math.Abs(raDeg - raMeeus) * 3600 < 1.5 && Math.Abs(decDeg - decMeeus) * 3600 < 1.5,
                  $"Precession RA {raDeg:F5}° Dec {decDeg:F5}° vs Meeus {raMeeus:F5}° {decMeeus:F5}°");

            // Refraction round trip at 20° altitude.
            Vec3 up = Vec3.UnitZ;
            Vec3 star = Vec3.FromSpherical(0.3, 20 * Astronomy.DegToRad);
            Vec3 back = Astronomy.RemoveRefraction(Astronomy.ApplyRefraction(star, up), up);
            Check(Vec3.Angle(star, back) * Astronomy.RadToDeg * 3600 < 5, $"Refraction round trip {Vec3.Angle(star, back) * Astronomy.RadToDeg * 3600:F2}\"");
        }

        private static void TestWahba()
        {
            Console.WriteLine("Attitude (Davenport q-method)");
            var rnd = new Random(7);
            Mat3 truth = Mat3.AxisAngle(new Vec3(0.3, -0.5, 0.8), 1.234);
            var b = new List<Vec3>(); var r = new List<Vec3>();
            for (int i = 0; i < 10; i++)
            {
                Vec3 v = new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5).Normalized();
                r.Add(v);
                b.Add((truth * v + new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * 1e-4).Normalized());
            }
            Mat3 est = Attitude.SolveWahba(b, r);
            Mat3 diff = est * truth.Transpose();
            double ang = Math.Acos(Math.Clamp((diff.Trace - 1) / 2, -1, 1)) * Astronomy.RadToDeg;
            Check(ang < 0.01, $"Wahba error {ang * 3600:F2}\"");
            Mat3 tri = Attitude.Triad(b[0], b[1], r[0], r[1]);
            diff = tri * truth.Transpose();
            ang = Math.Acos(Math.Clamp((diff.Trace - 1) / 2, -1, 1)) * Astronomy.RadToDeg;
            Check(ang < 0.05, $"TRIAD error {ang * 3600:F2}\"");
        }

        private static CameraModel PhoneCamera(double k1 = -0.03)
        {
            // ~ a 12 MP main camera: 4000x3000, 67° x 53° FOV.
            return new CameraModel
            {
                Width = 4000, Height = 3000, Fx = 3000, Fy = 3000, Cx = 1999.5, Cy = 1499.5,
                K1 = k1, K2 = 0.0, SensorOrientationDeg = 90
            };
        }

        private static void TestCamera()
        {
            Console.WriteLine("Camera model");
            CameraModel cam = PhoneCamera(-0.05);
            Vec3 ray = cam.PixelToDeviceRay(3500, 200);
            cam.DeviceRayToPixel(ray, out double u, out double v);
            Check(Math.Abs(u - 3500) < 1e-6 && Math.Abs(v - 200) < 1e-6, $"Pixel->ray->pixel round trip ({u:F6}, {v:F6})");
            Check(Math.Abs(cam.CameraToDevice.Determinant - 1) < 1e-12, "Camera->device is a proper rotation");
            // Back camera looks out of the back of the phone (-Z) at the image centre.
            Vec3 c = cam.PixelToDeviceRay(cam.Cx, cam.Cy);
            Check(Vec3.Angle(c, new Vec3(0, 0, -1)) < 1e-9, "Optical axis = device -Z");
        }

        private static (double ErrKm, FrameResult R) RunOne(CelestialLocator loc, Scenario sc, CameraModel truthCam, CameraModel nominalCam, SensorCalibration? cal = null)
        {
            SyntheticFrame f = SkySimulator.Render(loc.Catalog, truthCam, sc);
            FrameResult r = loc.ProcessFrame(f.Luma, f.Width, f.Height, f.Width, nominalCam, f.Gravity, sc.Utc, cal);
            double err = r.Fix != null ? Astronomy.GreatCircleKm(r.Fix.LatitudeDeg, r.Fix.LongitudeDeg, sc.LatDeg, sc.LonDeg) : double.NaN;
            string idInfo = r.Identification != null
                ? $"matched {r.Identification.Matches.Count,2}/{f.VisibleStars,2} vis, lbl {r.Identification.ConsistentLabels,2}, rms {r.Identification.RmsDeg:F3}°, f×{r.Identification.FocalScale:F3}, {r.Identification.ElapsedMs} ms"
                : "";
            Console.WriteLine($"    det {r.Detections.Count,2}  {idInfo}  -> " +
                              (r.Fix != null ? $"{r.Fix.LatitudeDeg:F3},{r.Fix.LongitudeDeg:F3}  err {err:F1} km (est ±{r.Fix.UncertaintyKm:F0})" : "FAILED: " + r.Message));
            return (err, r);
        }

        private static void TestSingleShots(CelestialLocator loc)
        {
            Console.WriteLine("End-to-end single shots (synthetic 12 MP frames)");
            var places = new (string Name, double Lat, double Lon)[]
            {
                ("Albany NY", 42.6526, -73.7562), ("Sydney", -33.8688, 151.2093), ("Reykjavik", 64.1466, -21.9426),
                ("Quito", -0.1807, -78.4678), ("Cape Town", -33.9249, 18.4241), ("Tokyo", 35.6762, 139.6503),
                ("Fairbanks", 64.8378, -147.7164), ("Nairobi", -1.2921, 36.8219)
            };
            var rnd = new Random(42);
            int ok = 0;
            double worst = 0;
            int i = 0;
            foreach (var p in places)
            {
                var sc = new Scenario
                {
                    LatDeg = p.Lat, LonDeg = p.Lon,
                    Utc = new DateTime(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc).AddHours(rnd.Next(0, 24)).AddMinutes(rnd.Next(60)),
                    AltitudeDeg = 50 + rnd.NextDouble() * 40, AzimuthDeg = rnd.NextDouble() * 360, RollDeg = rnd.NextDouble() * 360,
                    LimitingMag = 4.0 + rnd.NextDouble() * 1.0, Outliers = 3 + rnd.Next(4), CloudFraction = i % 3 == 0 ? 0.35 : 0.0,
                    Seed = 100 + i
                };
                Console.Write($"  {p.Name,-10} alt {sc.AltitudeDeg:F0}° lim {sc.LimitingMag:F1}{(sc.CloudFraction > 0 ? " cloud" : "      ")}");
                Console.WriteLine();
                // Truth camera has 3% different focal length and different distortion than the nominal model.
                CameraModel truth = PhoneCamera(-0.04);
                truth.Fx *= 1.03; truth.Fy *= 1.03;
                CameraModel nominal = PhoneCamera(0.0);
                (double err, _) = RunOne(loc, sc, truth, nominal);
                if (!double.IsNaN(err)) { ok++; worst = Math.Max(worst, err); }
                i++;
            }
            Check(ok == places.Length, $"{ok}/{places.Length} frames solved");
            Check(worst < 40, $"Worst single-shot error {worst:F1} km with perfect accelerometer (< 40 km)");

            Console.WriteLine("  Typical accelerometer bias (0.10 m/s² ≈ 0.6°), uncalibrated:");
            var scB = new Scenario
            {
                LatDeg = 42.6526, LonDeg = -73.7562, Utc = new DateTime(2026, 11, 19, 2, 30, 0, DateTimeKind.Utc),
                AltitudeDeg = 75, AzimuthDeg = 200, RollDeg = 40, LimitingMag = 4.5, Seed = 9,
                AccelBias = new Vec3(0.08, -0.06, 0.03)
            };
            RunOne(loc, scB, PhoneCamera(-0.04), PhoneCamera(0.0));

            Console.WriteLine("  Heavy light pollution (limiting mag 3.3):");
            var scL = new Scenario
            {
                LatDeg = 40.7128, LonDeg = -74.0060, Utc = new DateTime(2026, 11, 19, 4, 0, 0, DateTimeKind.Utc),
                AltitudeDeg = 80, AzimuthDeg = 10, RollDeg = 0, LimitingMag = 3.3, Background = 60, GradientTop = 110, Seed = 11, Outliers = 5
            };
            (double errL, _) = RunOne(loc, scL, PhoneCamera(-0.04), PhoneCamera(0.0));
            Check(!double.IsNaN(errL), "Light-polluted frame solved");

            Console.WriteLine("  Negative control: noise + random blobs only (no real stars):");
            var cam = PhoneCamera(0);
            var rng = new Random(5);
            var luma = new byte[4000 * 3000];
            for (int k = 0; k < luma.Length; k++) luma[k] = (byte)(25 + rng.Next(-4, 5));
            for (int k = 0; k < 40; k++)
            {
                int u = 50 + rng.Next(3900), v = 50 + rng.Next(2900);
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) luma[(v + dy) * 4000 + u + dx] = (byte)(120 + rng.Next(100));
            }
            var g = new GravityMeasurement { Mean = new Vec3(0, 0, -9.8), StdDev = 0.02, Samples = 100 };
            FrameResult neg = loc.ProcessFrame(luma, 4000, 3000, 4000, cam, g, DateTime.UtcNow);
            Console.WriteLine($"    det {neg.Detections.Count}, matched {neg.Identification?.Matches.Count}, lbl {neg.Identification?.ConsistentLabels} -> " + (neg.Fix == null ? "rejected: " + neg.Message : "FALSE FIX " + neg.Fix));
            Check(neg.Fix == null, "No false fix from a random field");
        }

        private static void TestBracket(CelestialLocator loc)
        {
            Console.WriteLine("Exposure bracket at one pose (1-2-4-8 s ~ limiting mag 3.5/4.0/4.5/5.0) + one bumped frame");
            double lat = 42.6526, lon = -73.7562;
            var t0 = new DateTime(2026, 11, 19, 2, 0, 0, DateTimeKind.Utc);
            var mags = new[] { 3.5, 4.0, 4.5, 5.0, 4.5 };
            var fixes = new List<PositionFix>();
            var singles = new List<double>();
            for (int k = 0; k < mags.Length; k++)
            {
                var sc = new Scenario
                {
                    LatDeg = lat, LonDeg = lon, Utc = t0.AddSeconds(5 * k), AltitudeDeg = 72, AzimuthDeg = 160, RollDeg = 20,
                    LimitingMag = mags[k], PositionJitterPx = 6, Seed = 4000 + k
                };
                SyntheticFrame f = SkySimulator.Render(loc.Catalog, PhoneCamera(-0.04), sc);
                GravityMeasurement g = f.Gravity;
                bool bumped = k == mags.Length - 1;
                if (bumped) g = new GravityMeasurement { Mean = g.Mean + new Vec3(0.35, -0.25, 0.0), StdDev = 0.3, Samples = g.Samples };
                FrameResult r = loc.ProcessFrame(f.Luma, f.Width, f.Height, f.Width, PhoneCamera(0.0), g, sc.Utc);
                if (r.Fix == null) { Console.WriteLine($"    frame {k}: not solved ({r.Message})"); continue; }
                double e = Astronomy.GreatCircleKm(r.Fix.LatitudeDeg, r.Fix.LongitudeDeg, lat, lon);
                Console.WriteLine($"    frame {k}{(bumped ? " (bumped)" : "")}: {r.Identification!.Matches.Count} stars, err {e:F1} km, random ±{r.Fix.RandomUncertaintyKm:F1} km");
                fixes.Add(r.Fix);
                if (!bumped) singles.Add(e);
            }
            PositionFix? c = PositionSolver.CombineFixes(fixes, out string msg);
            if (c == null) { Check(false, "Bracket combine"); return; }
            double ec = Astronomy.GreatCircleKm(c.LatitudeDeg, c.LongitudeDeg, lat, lon);
            singles.Sort();
            double medianSingle = singles[singles.Count / 2];
            Console.WriteLine($"    {msg} -> err {ec:F1} km (median good single {medianSingle:F1} km), ±{c.UncertaintyKm:F0} km = random {c.RandomUncertaintyKm:F1} + systematic {c.SystematicUncertaintyKm:F0}");
            Check(fixes.Count >= 4, $"{fixes.Count}/5 bracket frames solved");
            Check(c.FramesUsed == fixes.Count - 1, "Bumped frame rejected as outlier");
            Check(ec <= medianSingle + 0.5, $"Combined error {ec:F1} km <= median single {medianSingle:F1} km");
            Check(c.SystematicUncertaintyKm >= fixes[0].SystematicUncertaintyKm - 1e-9, "Systematic error is not averaged away");

            PositionFix? one = PositionSolver.CombineFixes(new List<PositionFix> { fixes[0] }, out _);
            Check(one != null && one.LatitudeDeg == fixes[0].LatitudeDeg, "Single frame passes through unchanged");
            Check(PositionSolver.CombineFixes(new List<PositionFix>(), out _) == null, "No frames -> no fix");
        }

        private static void TestCoordinateParser()
        {
            Console.WriteLine("Coordinate parser");
            double eLat = 40 + 41 / 60.0 + 21.3 / 3600, eLon = -(74 + 2 / 60.0 + 40.2 / 3600);   // Statue of Liberty
            var ok = new[]
            {
                "40°41'21.3\"N 74°02'40.2\"W", "40°41′21.3″N, 74°02′40.2″W", "40 41 21.3 N, 74 02 40.2 W",
                "40.68925, -74.0445", "40.68925 -74.0445", "40.68925N 74.0445W", "40°41.355'N 74°02.670'W"
            };
            foreach (string t in ok)
            {
                bool good = CoordinateParser.TryParse(t, out double la, out double lo, out string err);
                Check(good && Math.Abs(la - eLat) < 2e-5 && Math.Abs(lo - eLon) < 2e-5, $"\"{t}\" -> {la:F6}, {lo:F6} {err}");
            }
            foreach (string t in new[] { "", "42.7", "abc", "95, 10", "10, 190", "74°02'W 40°41'N", "40°75'N 74°W" })
                Check(!CoordinateParser.TryParse(t, out _, out _, out string err), $"rejects \"{t}\" ({err})");
        }

        private static void TestWideFocal(CelestialLocator loc)
        {
            Console.WriteLine("Phone reports a focal length 26% too short (seen on a real phone)");
            var sc = new Scenario { LatDeg = 42.65, LonDeg = -73.76, Utc = new DateTime(2026, 11, 20, 1, 20, 0, DateTimeKind.Utc),
                                    AltitudeDeg = 55, AzimuthDeg = 22, RollDeg = 0, LimitingMag = 4.6, PositionJitterPx = 3, Seed = 61 };
            CameraModel truth = PhoneCamera(0); truth.Fx *= 1.259; truth.Fy *= 1.259;
            (double err, FrameResult r) = RunOne(loc, sc, truth, PhoneCamera(0));
            Check(r.Fix != null, "Solved despite the wrong reported focal length");
            if (r.Identification != null)
                Check(Math.Abs(r.Identification.FocalScale - 1.259) < 0.01, $"Recovered focal scale {r.Identification.FocalScale:F4} (true 1.259)");

            var opts = new LocatorOptions();
            int falseFix = 0;
            var sw = Stopwatch.StartNew();
            for (int t = 0; t < 10; t++)
            {
                var rng = new Random(100 + t);
                var luma = new byte[4000 * 3000];
                for (int k = 0; k < luma.Length; k++) luma[k] = (byte)(25 + rng.Next(-4, 5));
                int blobs = 20 + rng.Next(60);
                for (int k = 0; k < blobs; k++)
                {
                    int u = 50 + rng.Next(3900), v = 50 + rng.Next(2900);
                    for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) luma[(v + dy) * 4000 + u + dx] = (byte)(80 + rng.Next(150));
                }
                var g = new GravityMeasurement { Mean = new Vec3(0, 0, -9.8), StdDev = 0.02, Samples = 100 };
                if (loc.ProcessFrame(luma, 4000, 3000, 4000, PhoneCamera(0), g, DateTime.UtcNow).Fix != null) falseFix++;
            }
            Check(falseFix == 0, $"Random fields with the wide focal search: {falseFix}/10 false fixes ({sw.ElapsedMilliseconds / 10} ms per frame)");
        }

        private static void TestKnownLocationCalibration(CelestialLocator loc)
        {
            Console.WriteLine("One-time calibration at a known location, phone in a fixed 60° mount");
            Vec3 bias = new Vec3(0.11, -0.09, 0.05);
            Vec3 mis = new Vec3(0.3, -0.6, 0.2).Normalized() * (0.4 * Astronomy.DegToRad);
            CameraModel TrueCam() { var c = PhoneCamera(-0.04); c.Cx += 20; c.Cy -= 12; return c; }
            double calLat = 42.6526, calLon = -73.7562;
            var t0 = new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);

            Shot? MakeShot(double lat, double lon, DateTime utc, double alt, double az, double roll, double mag, int seed, SensorCalibration? cal, out double err)
            {
                var sc = new Scenario { LatDeg = lat, LonDeg = lon, Utc = utc, AltitudeDeg = alt, AzimuthDeg = az, RollDeg = roll,
                                        LimitingMag = mag, AccelBias = bias, CameraMisalignment = mis, PositionJitterPx = 4, Seed = seed };
                SyntheticFrame f = SkySimulator.Render(loc.Catalog, TrueCam(), sc);
                FrameResult r = loc.ProcessFrame(f.Luma, f.Width, f.Height, f.Width, PhoneCamera(0), f.Gravity, utc, cal);
                err = r.Fix == null ? double.NaN : Astronomy.GreatCircleKm(r.Fix.LatitudeDeg, r.Fix.LongitudeDeg, lat, lon);
                return r.Shot;
            }

            // Calibration capture: one bracket (3 frames) in the mount, at the known spot.
            var calShots = new List<Shot>();
            double[] mags = { 4.0, 4.5, 5.0 };
            for (int k = 0; k < mags.Length; k++)
            {
                Shot? s = MakeShot(calLat, calLon, t0.AddSeconds(5 * k), 60, 30, 0, mags[k], 7000 + k, null, out _);
                if (s != null) calShots.Add(s);
            }
            SensorCalibration? cal = PositionSolver.CalibrateAtKnownLocation(calShots, calLat, calLon, "known location (test)", 8.0, out string msg);
            Console.WriteLine("    " + msg);
            if (cal == null) { Check(false, "Known-location calibration"); return; }
            Check(cal.PoseSpecific, "Single-pose capture gives a pose-specific calibration");

            // Later: other places, nights and compass directions, same mount pose.
            var tests = new (double Lat, double Lon, double Hours, double Az)[]
                { (42.6526, -73.7562, 1, 200), (44.30, -75.50, 30, 120), (40.71, -74.00, 75, 300), (43.10, -71.20, 170, 45) };
            double worstCal = 0, worstUncal = 0;
            int i = 0;
            foreach (var t in tests)
            {
                MakeShot(t.Lat, t.Lon, t0.AddHours(t.Hours), 60, t.Az, 0, 4.5, 7100 + i, cal, out double eCal);
                MakeShot(t.Lat, t.Lon, t0.AddHours(t.Hours), 60, t.Az, 0, 4.5, 7100 + i, null, out double eUn);
                Console.WriteLine($"    {t.Lat:F2},{t.Lon:F2} az {t.Az,3}°: calibrated {eCal:F1} km, uncalibrated {eUn:F1} km");
                worstCal = Math.Max(worstCal, double.IsNaN(eCal) ? 1e9 : eCal);
                worstUncal = Math.Max(worstUncal, eUn);
                i++;
            }
            Check(worstCal < 5, $"Same pose anywhere: worst calibrated error {worstCal:F1} km < 5 km (uncalibrated up to {worstUncal:F1} km)");

            MakeShot(41.5, -74.5, t0.AddHours(50), 55, 90, 0, 4.5, 7200, cal, out double e5);
            Console.WriteLine($"    pose 5° off the calibration pose: {e5:F1} km (warning would show: {cal.PoseDifferenceDeg(new Vec3(0, Math.Sin(35 * Math.PI / 180), -Math.Cos(35 * Math.PI / 180))) > 3})");
            Check(e5 < 10, $"5° pose change still under 10 km ({e5:F1} km)");

            Console.WriteLine("  Known location, captures at three different tilts (full model)");
            var multi = new List<Shot>();
            var poses = new (double Alt, double Az, double Roll)[] { (85, 0, 0), (55, 90, 90), (55, 210, 300), (60, 330, 180) };
            for (int k = 0; k < poses.Length; k++)
            {
                Shot? s = MakeShot(calLat, calLon, t0.AddSeconds(40 * k), poses[k].Alt, poses[k].Az, poses[k].Roll, 4.5, 7300 + k, null, out _);
                if (s != null) multi.Add(s);
            }
            SensorCalibration? full = PositionSolver.CalibrateAtKnownLocation(multi, calLat, calLon, "known location (test)", double.NaN, out string msg2);
            Console.WriteLine("    " + msg2);
            Check(full != null && !full.PoseSpecific, "Different tilts give a pose-independent calibration");
            if (full == null) return;
            Console.WriteLine($"    estimated bias {full.AccelBias} (true {bias}), misalignment {full.CameraMisalignment} = {full.CameraMisalignment.Length * Astronomy.RadToDeg:F2}° (true {mis} + principal-point offset)");
            PositionFix? sm = PositionSolver.SolveMulti(multi, out _);
            if (sm?.Calibration != null) Console.WriteLine($"    for comparison SolveMulti (unknown position): bias {sm.Calibration.AccelBias}, misalignment {sm.Calibration.CameraMisalignment}");
            double worst = 0;
            var any = new (double Alt, double Az, double Roll)[] { (70, 140, 45), (50, 270, 250), (80, 20, 120) };
            for (int k = 0; k < any.Length; k++)
            {
                MakeShot(41.0, -76.0, t0.AddHours(20 + k), any[k].Alt, any[k].Az, any[k].Roll, 4.5, 7400 + k, full, out double e);
                MakeShot(41.0, -76.0, t0.AddHours(20 + k), any[k].Alt, any[k].Az, any[k].Roll, 4.5, 7400 + k, sm?.Calibration, out double eSm);
                Console.WriteLine($"    pose alt {any[k].Alt} roll {any[k].Roll}: known-location full cal {e:F1} km, SolveMulti cal {eSm:F1} km");
                worst = Math.Max(worst, double.IsNaN(e) ? 1e9 : e);
            }
            Check(worst < 8, $"Any pose after full calibration: worst {worst:F1} km < 8 km");
        }

        /// <summary>
        /// Field test 2 (Nov 2026): the camera image shifted up to ~1° inside the phone between frames
        /// (optical stabilisation) while the accelerometer showed no movement. Simulated here as a camera
        /// misalignment that changes from frame to frame.
        /// </summary>
        private static void TestImageStability(CelestialLocator loc)
        {
            Console.WriteLine("Image stability: camera image moving inside the phone vs the phone itself moving");
            double lat = 42.6526, lon = -73.7562;
            var t0 = new DateTime(2026, 11, 21, 1, 0, 0, DateTimeKind.Utc);
            Vec3 bias = new Vec3(0.05, -0.04, 0.02);
            Shot? Make(int k, double alt, double az, Vec3 mis, int seed)
            {
                var sc = new Scenario { LatDeg = lat, LonDeg = lon, Utc = t0.AddSeconds(4 * k), AltitudeDeg = alt, AzimuthDeg = az,
                                        LimitingMag = 4.5, AccelBias = bias, CameraMisalignment = mis, PositionJitterPx = 1, Seed = seed };
                SyntheticFrame f = SkySimulator.Render(loc.Catalog, PhoneCamera(0), sc);
                return loc.ProcessFrame(f.Luma, f.Width, f.Height, f.Width, PhoneCamera(0), f.Gravity, sc.Utc).Shot;
            }
            Vec3 baseMis = new Vec3(0.2, -0.1, 0.05).Normalized() * (0.3 * Astronomy.DegToRad);

            // 1) Steady camera, phone tilted differently between frames: zeniths must still agree.
            var moved = new List<Shot>();
            double[] alts = { 60, 57, 63, 60 };
            for (int k = 0; k < 4; k++) { Shot? s = Make(k, alts[k], 30 + 3 * k, baseMis, 9000 + k); if (s != null) moved.Add(s); }
            var (rmsM, maxM) = PositionSolver.ZenithSpread(moved);
            Check(moved.Count == 4 && maxM < PositionSolver.StabilityWarnDeg,
                  $"Phone physically moved 6° between frames, camera steady: zenith spread {maxM:F3}° (no warning)");

            // 2) Phone still, image drifting 0.15° per frame (like the field test): warning + calibration refused.
            var drift = new List<Shot>();
            for (int k = 0; k < 4; k++)
            {
                Vec3 mis = baseMis + new Vec3(1, 0.4, 0).Normalized() * (0.15 * k * Astronomy.DegToRad);
                Shot? s = Make(k, 60, 30, mis, 9100 + k);
                if (s != null) drift.Add(s);
            }
            var (rmsD, maxD) = PositionSolver.ZenithSpread(drift);
            Check(drift.Count == 4 && maxD > PositionSolver.StabilityWarnDeg, $"Image drifting inside the phone: zenith spread {maxD:F3}° (warning)");
            SensorCalibration? bad = PositionSolver.CalibrateAtKnownLocation(drift, lat, lon, "test", double.NaN, out string msgBad);
            Console.WriteLine("    " + msgBad);
            Check(bad == null, "Calibration refused when the frames disagree");

            // 3) Steady capture with one jumped frame: that frame is dropped and calibration still works.
            var oneBad = new List<Shot>();
            for (int k = 0; k < 4; k++)
            {
                Vec3 mis = k == 3 ? baseMis + new Vec3(0, 1, 0) * (0.5 * Astronomy.DegToRad) : baseMis;
                Shot? s = Make(k, 60, 30, mis, 9200 + k);
                if (s != null) oneBad.Add(s);
            }
            SensorCalibration? ok = PositionSolver.CalibrateAtKnownLocation(oneBad, lat, lon, "test", double.NaN, out string msgOk);
            Console.WriteLine("    " + msgOk);
            Check(ok != null && ok.Shots == oneBad.Count - 1 && ok.SpreadDeg < 0.05, "One jumped frame is rejected; calibration from the rest");
        }

        /// <summary>
        /// Field test 3: lit tree branches crowded real stars out of the brightest-80 list and made
        /// three frames fail. Elongated blobs must be rejected while round stars are kept.
        /// </summary>
        private static void TestDetectorRejectsStreaks()
        {
            const int w = 400, h = 300;
            var img = new byte[w * h];
            var rnd = new Random(5);
            for (int i = 0; i < img.Length; i++) img[i] = (byte)(20 + rnd.Next(0, 4));
            void Blob(double cx, double cy, double sx, double sy, double angle, double peak)
            {
                double c = Math.Cos(angle), s = Math.Sin(angle);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double dx = x - cx, dy = y - cy;
                        double u = (c * dx + s * dy) / sx, v = (-s * dx + c * dy) / sy;
                        double val = peak * Math.Exp(-0.5 * (u * u + v * v));
                        if (val > 1) img[y * w + x] = (byte)Math.Min(255, img[y * w + x] + val);
                    }
            }
            Blob(100, 100, 1.4, 1.4, 0, 200);          // star
            Blob(300, 80, 1.6, 1.3, 0.3, 150);         // star, slightly trailed
            Blob(200, 200, 9.0, 1.2, 0.7, 220);        // lit branch
            Blob(80, 240, 14.0, 1.0, -0.4, 250);       // wire / plane trail
            List<DetectedStar> det = StarDetector.Detect(img, w, h, w);
            bool starsKept = det.Count(d => Math.Abs(d.U - 100) < 2 && Math.Abs(d.V - 100) < 2) == 1
                          && det.Count(d => Math.Abs(d.U - 300) < 2 && Math.Abs(d.V - 80) < 2) == 1;
            bool streaksGone = det.All(d => Math.Abs(d.U - 200) > 15 && Math.Abs(d.U - 80) > 20);
            Check(starsKept && streaksGone, $"Detector keeps round stars and drops elongated blobs ({det.Count} kept)");
        }

        private static void TestReplay(CelestialLocator loc)
        {
            Console.WriteLine("Replay of a saved frame (same .pgm/.txt format the app writes)");
            var sc = new Scenario { LatDeg = 42.6526, LonDeg = -73.7562, Utc = new DateTime(2026, 11, 19, 3, 0, 0, DateTimeKind.Utc), AltitudeDeg = 70, AzimuthDeg = 120, Seed = 3 };
            CameraModel cam = PhoneCamera(0);
            SyntheticFrame f = SkySimulator.Render(loc.Catalog, cam, sc);
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "starlocator_replay");
            System.IO.Directory.CreateDirectory(dir);
            string pgm = System.IO.Path.Combine(dir, "frame_test.pgm");
            using (var fs = System.IO.File.Create(pgm))
            {
                byte[] hdr = System.Text.Encoding.ASCII.GetBytes($"P5\n{f.Width} {f.Height}\n255\n");
                fs.Write(hdr, 0, hdr.Length);
                fs.Write(f.Luma, 0, f.Luma.Length);
            }
            var inv = CultureInfo.InvariantCulture;
            string R(double v) => v.ToString("R", inv);
            System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(pgm, ".txt"),
                $"utc={sc.Utc.ToString("o", inv)}\nwidth={cam.Width}\nheight={cam.Height}\nfx={R(cam.Fx)}\nfy={R(cam.Fy)}\ncx={R(cam.Cx)}\ncy={R(cam.Cy)}\n" +
                $"k1=0\nk2=0\nk3=0\np1=0\np2=0\nsensorOrientation=90\nax={R(f.Gravity.Mean.X)}\nay={R(f.Gravity.Mean.Y)}\naz={R(f.Gravity.Mean.Z)}\n" +
                $"astd=0.02\nasamples=100\nexposureNs=4000000000\niso=1600\ncameraModelSource=synthetic\n");
            int rc = Replay.Run(pgm);
            Check(rc == 0, "Replay solved the saved frame");
        }

        private static void Sweep()
        {
            var loc = new CelestialLocator();
            foreach (double jitter in new[] { 0.0, 3.0, 6.0, 10.0 })
            {
                var rnd = new Random(1234);
                int n = 40, solved = 0, wrong = 0;
                var errs = new List<double>();
                for (int i = 0; i < n; i++)
                {
                    var sc = new Scenario
                    {
                        LatDeg = rnd.NextDouble() * 140 - 70, LonDeg = rnd.NextDouble() * 360 - 180,
                        Utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(rnd.Next(525600)),
                        AltitudeDeg = 45 + rnd.NextDouble() * 45, AzimuthDeg = rnd.NextDouble() * 360, RollDeg = rnd.NextDouble() * 360,
                        LimitingMag = 3.3 + rnd.NextDouble() * 1.7, Outliers = rnd.Next(8), CloudFraction = rnd.NextDouble() < 0.3 ? 0.4 : 0,
                        PositionJitterPx = jitter, Seed = 9000 + i
                    };
                    var truth = PhoneCamera(-0.03 + rnd.NextDouble() * 0.04);
                    double fe = 0.95 + rnd.NextDouble() * 0.10;
                    truth.Fx *= fe; truth.Fy *= fe;
                    SyntheticFrame f = SkySimulator.Render(loc.Catalog, truth, sc);
                    FrameResult r = loc.ProcessFrame(f.Luma, f.Width, f.Height, f.Width, PhoneCamera(0), f.Gravity, sc.Utc);
                    if (r.Fix == null) continue;
                    double e = Astronomy.GreatCircleKm(r.Fix.LatitudeDeg, r.Fix.LongitudeDeg, sc.LatDeg, sc.LonDeg);
                    if (e > 300) wrong++; else { solved++; errs.Add(e); }
                }
                errs.Sort();
                string med = errs.Count > 0 ? errs[errs.Count / 2].ToString("F1") : "-";
                string p90 = errs.Count > 0 ? errs[(int)(errs.Count * 0.9)].ToString("F1") : "-";
                Console.WriteLine($"jitter {jitter,4:F1} px ({jitter * 0.019:F2}°): solved {solved}/{n}, WRONG {wrong}, median err {med} km, 90% {p90} km");
            }
            int falseFix = 0;
            for (int t = 0; t < 30; t++)
            {
                var rng = new Random(t);
                var luma = new byte[4000 * 3000];
                for (int k = 0; k < luma.Length; k++) luma[k] = (byte)(25 + rng.Next(-4, 5));
                int blobs = 15 + rng.Next(50);
                for (int k = 0; k < blobs; k++)
                {
                    int u = 50 + rng.Next(3900), v = 50 + rng.Next(2900);
                    for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) luma[(v + dy) * 4000 + u + dx] = (byte)(80 + rng.Next(150));
                }
                var g = new GravityMeasurement { Mean = new Vec3(0, 0, -9.8), StdDev = 0.02, Samples = 100 };
                if (loc.ProcessFrame(luma, 4000, 3000, 4000, PhoneCamera(0), g, DateTime.UtcNow).Fix != null) falseFix++;
            }
            Console.WriteLine($"random fields: {falseFix}/30 false fixes");
        }

        private static void TestMultiShot(CelestialLocator loc)
        {
            Console.WriteLine("Multi-shot calibration: accel bias 0.15 m/s² (≈0.9°) + camera mount error 0.4° + principal point off 20 px");
            Vec3 bias = new Vec3(0.11, -0.09, 0.05);
            Vec3 mis = new Vec3(0.3, -0.6, 0.2).Normalized() * (0.4 * Astronomy.DegToRad);
            double lat = 42.6526, lon = -73.7562;
            CameraModel TrueCam() { var c = PhoneCamera(-0.04); c.Cx += 20; c.Cy -= 12; return c; }
            var shots = new List<Shot>();
            // Portrait/landscape/tilted poses, as a user would be told to do.
            var poses = new (double Alt, double Az, double Roll)[]
                { (85, 0, 0), (55, 90, 90), (55, 210, 300), (60, 330, 180), (70, 140, 45), (50, 270, 250) };
            var t0 = new DateTime(2026, 11, 19, 1, 0, 0, DateTimeKind.Utc);
            int k = 0;
            double singleWorst = 0;
            foreach (var p in poses)
            {
                var sc = new Scenario
                {
                    LatDeg = lat, LonDeg = lon, Utc = t0.AddSeconds(45 * k), AltitudeDeg = p.Alt, AzimuthDeg = p.Az, RollDeg = p.Roll,
                    LimitingMag = 4.5, AccelBias = bias, CameraMisalignment = mis, PositionJitterPx = 4, Seed = 500 + k
                };
                (double err, FrameResult r) = RunOne(loc, sc, TrueCam(), PhoneCamera(0.0));
                if (r.Shot != null) { shots.Add(r.Shot); singleWorst = Math.Max(singleWorst, err); }
                k++;
            }

            PositionFix? fix3 = PositionSolver.SolveMulti(shots.GetRange(0, 3), out string msg3);
            double err3 = fix3 == null ? double.NaN : Astronomy.GreatCircleKm(fix3.LatitudeDeg, fix3.LongitudeDeg, lat, lon);
            Console.WriteLine($"    3 shots (bias only): err {err3:F1} km. {msg3}");

            PositionFix? fix = PositionSolver.SolveMulti(shots, out string msg);
            Console.WriteLine("    " + msg);
            if (fix == null || fix.Calibration == null) { Check(false, "Multi-shot solve"); return; }
            double errM = Astronomy.GreatCircleKm(fix.LatitudeDeg, fix.LongitudeDeg, lat, lon);
            double biasErr = (fix.AccelBias - bias).Length;
            Console.WriteLine($"    {shots.Count} shots: fix {fix}  true error {errM:F1} km; bias err {biasErr:F4} m/s²; " +
                              $"misalignment est {fix.Calibration.CameraMisalignment.Length * Astronomy.RadToDeg:F2}° (true 0.40° + pp offset); worst uncalibrated single {singleWorst:F1} km");
            Check(errM < 20, $"Calibrated multi-shot error {errM:F1} km < 20 km");
            Check(errM < singleWorst / 3, "Calibration beats uncalibrated single shots by > 3x");

            Console.WriteLine("  New single shots with stored calibration:");
            double worst2 = 0;
            for (int j = 0; j < 3; j++)
            {
                var sc2 = new Scenario { LatDeg = lat, LonDeg = lon, Utc = t0.AddMinutes(10 + j), AltitudeDeg = 60 + 10 * j, AzimuthDeg = 250 - 70 * j, RollDeg = 10 + 120 * j,
                                         LimitingMag = 4.5, AccelBias = bias, CameraMisalignment = mis, PositionJitterPx = 4, Seed = 777 + j };
                (double e2, _) = RunOne(loc, sc2, TrueCam(), PhoneCamera(0.0), fix.Calibration);
                worst2 = Math.Max(worst2, double.IsNaN(e2) ? 1e9 : e2);
            }
            Check(worst2 < 20, $"Calibrated single shots worst error {worst2:F1} km < 20 km");
        }
    }
}
