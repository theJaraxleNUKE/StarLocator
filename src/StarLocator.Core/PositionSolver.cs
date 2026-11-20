using System;
using System.Collections.Generic;

namespace StarLocator.Core
{
    /// <summary>Averaged accelerometer reading taken while the photo was being exposed.</summary>
    public sealed class GravityMeasurement
    {
        /// <summary>Mean specific force in the device frame (m/s^2). At rest it points UP.</summary>
        public Vec3 Mean { get; init; }
        /// <summary>RMS deviation of the samples from the mean (m/s^2): a motion indicator.</summary>
        public double StdDev { get; init; }
        public int Samples { get; init; }
    }

    /// <summary>A successfully star-tracked photo with its gravity measurement.</summary>
    public sealed class Shot
    {
        public DateTime UtcMidExposure { get; init; }
        public GravityMeasurement Gravity { get; init; } = null!;
        /// <summary>Earth-fixed -> device rotation: d = M * r_ecef.</summary>
        public Mat3 EarthFixedToDevice { get; init; }
        public double AttitudeRmsDeg { get; init; }
        public int MatchedStars { get; init; }
    }

    /// <summary>
    /// Phone-specific systematic errors learned from a multi-shot solve and re-used for single shots.
    /// </summary>
    public sealed class SensorCalibration
    {
        /// <summary>Accelerometer bias in the device frame (m/s^2).</summary>
        public Vec3 AccelBias { get; init; }
        /// <summary>
        /// Small rotation (radians, axis*angle) between the camera's idea of the device frame and the
        /// accelerometer's: covers camera-module mounting error and principal-point error.
        /// Model: measured_accel - bias = (I - [w x]) * (camera-derived up vector).
        /// </summary>
        public Vec3 CameraMisalignment { get; init; }
        public int Shots { get; init; }
        public DateTime CreatedUtc { get; init; }

        /// <summary>
        /// True when the correction is only exact for one phone pose (a one-time calibration in a fixed
        /// mount). AccelBias then holds the combined "tilt error" of that pose and CameraMisalignment is zero.
        /// </summary>
        public bool PoseSpecific { get; init; }
        /// <summary>Unit "up" vector in the device frame at calibration (for pose-change warnings).</summary>
        public Vec3 PoseUp { get; init; }
        /// <summary>How the calibration was made, e.g. "known location (GPS)".</summary>
        public string Method { get; init; } = "multi-pose";
        /// <summary>Phone (battery) temperature at calibration in °C, or NaN if unknown.</summary>
        public double TemperatureC { get; init; } = double.NaN;
        /// <summary>Uncalibrated error of the calibration capture itself, in km (known-location method only).</summary>
        public double ErrorBeforeKm { get; init; } = double.NaN;

        /// <summary>Angle (degrees) between the current gravity direction and the calibration pose.</summary>
        public double PoseDifferenceDeg(Vec3 accel) =>
            PoseUp.Length > 0.5 ? Vec3.Angle(PoseUp, accel) * Astronomy.RadToDeg : 0.0;

        public override string ToString()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (PoseSpecific)
                return string.Format(ci, "{0}, pose-specific tilt correction {1:F2}° ({2} frames)",
                    Method, Math.Atan2(AccelBias.Length, PositionSolver.StandardGravity) * Astronomy.RadToDeg, Shots);
            return string.Format(ci, "{0}, bias {1:F3} m/s², misalignment {2:F2}° ({3} frames)",
                Method, AccelBias.Length, CameraMisalignment.Length * Astronomy.RadToDeg, Shots);
        }
    }

    public sealed class PositionFix
    {
        public double LatitudeDeg { get; init; }
        public double LongitudeDeg { get; init; }
        /// <summary>Estimated 1-sigma horizontal error (km): sqrt(random^2 + systematic^2).</summary>
        public double UncertaintyKm { get; init; }
        /// <summary>Part of the error that differs frame to frame (star noise, accelerometer noise); averages down.</summary>
        public double RandomUncertaintyKm { get; init; }
        /// <summary>Part shared by all frames of a pose (accelerometer bias, camera tilt); does not average down.</summary>
        public double SystematicUncertaintyKm { get; init; }
        /// <summary>Number of frames combined into this fix (1 for a single frame).</summary>
        public int FramesUsed { get; init; } = 1;
        public Vec3 AccelBias { get; init; }
        public bool BiasEstimated { get; init; }
        /// <summary>Calibration learned by a multi-shot solve (null for single shots).</summary>
        public SensorCalibration? Calibration { get; init; }
        public int ShotCount { get; init; }
        public string Notes { get; init; } = "";

        public override string ToString() =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F4}, {1:F4} (±{2:F0} km)", LatitudeDeg, LongitudeDeg, UncertaintyKm);
    }

    /// <summary>
    /// Turns star-tracker attitude + gravity + UTC into latitude/longitude.
    ///
    /// The accelerometer gives the local vertical ("up") in the device frame; the star tracker
    /// gives the device orientation relative to the stars. Together they give the direction of the
    /// zenith among the stars: its declination is the (astronomical) latitude, and its right
    /// ascension minus Greenwich sidereal time is the longitude. This is classical celestial
    /// navigation, with the phone acting as both sextant and artificial horizon.
    /// </summary>
    public static class PositionSolver
    {
        public const double StandardGravity = 9.80665;

        /// <summary>Typical uncorrected phone accelerometer tilt error assumed when no bias is known (degrees).</summary>
        public const double AssumedUncalibratedTiltErrorDeg = 0.5;
        public const double AssumedCalibratedTiltErrorDeg = 0.05;

        public static Shot MakeShot(IdentificationResult id, GravityMeasurement g, DateTime utcMid)
        {
            Mat3 j2000ToEcef = Astronomy.J2000ToEarthFixed(utcMid);
            Mat3 m = id.J2000ToDevice * j2000ToEcef.Transpose();
            return new Shot
            {
                UtcMidExposure = utcMid,
                Gravity = g,
                EarthFixedToDevice = m,
                AttitudeRmsDeg = id.RmsDeg,
                MatchedStars = id.Matches.Count
            };
        }

        /// <summary>Single-photo fix, optionally correcting with a stored multi-shot calibration.</summary>
        public static PositionFix SolveSingle(Shot shot, SensorCalibration? calibration = null)
        {
            Vec3 bias = calibration?.AccelBias ?? Vec3.Zero;
            Vec3 w = calibration?.CameraMisalignment ?? Vec3.Zero;
            Vec3 a = shot.Gravity.Mean - bias;
            // Invert (I - [w x]) to first order: camera-frame up = a + w x a.
            Vec3 upCam = a + Vec3.Cross(w, a);
            Vec3 zenithEcef = (shot.EarthFixedToDevice.Transpose() * upCam).Normalized();
            (double lon, double lat) = zenithEcef.ToSpherical();

            double attErr = AttitudeSigmaRad(shot);
            double noise = shot.Gravity.StdDev / Math.Max(1.0, Math.Sqrt(shot.Gravity.Samples / 5.0)) / StandardGravity;
            double tilt = (calibration != null ? AssumedCalibratedTiltErrorDeg : AssumedUncalibratedTiltErrorDeg) * Astronomy.DegToRad;
            double random = Math.Sqrt(attErr * attErr + noise * noise);
            double sigma = Math.Sqrt(random * random + tilt * tilt);

            return new PositionFix
            {
                LatitudeDeg = lat * Astronomy.RadToDeg,
                LongitudeDeg = Astronomy.WrapDeg180(lon * Astronomy.RadToDeg),
                UncertaintyKm = sigma * Astronomy.EarthRadiusKm,
                RandomUncertaintyKm = random * Astronomy.EarthRadiusKm,
                SystematicUncertaintyKm = tilt * Astronomy.EarthRadiusKm,
                AccelBias = bias,
                BiasEstimated = false,
                ShotCount = 1,
                Notes = calibration != null
                    ? "Single shot with stored calibration (" + calibration + ")."
                    : "Single shot, uncalibrated. Take 4+ shots at different tilts to calibrate the phone."
            };
        }

        /// <summary>
        /// Multi-shot fix that also calibrates the phone.
        /// Model per shot k:  a_k = u_k - w x u_k + b,  with u_k = M_k v,
        ///   v = g * zenith_ecef (3 unknowns), b = accelerometer bias (3), w = camera/IMU misalignment (3).
        /// 3 shots: v and b only (linear). 4+ shots: all nine by Gauss-Newton. Shots must be taken at
        /// clearly different tilts (portrait, landscape, tilted left/right...).
        /// </summary>
        public static PositionFix? SolveMulti(IReadOnlyList<Shot> shots, out string message, bool estimateMisalignment = true)
        {
            message = "";
            if (shots.Count < 3) { message = "Need at least 3 shots at clearly different tilts."; return null; }
            bool fitW = estimateMisalignment && shots.Count >= 4;
            int np = 6;

            var p = new double[9]; // v(3), b(3), w(3)
            double[,]? lastNormal = null;
            double ssr = 0;
            for (int iter = 0; iter < 20; iter++)
            {
                // The first passes solve the linear v,b problem; that gives the starting point for
                // the misalignment terms (their Jacobian is zero at v = 0).
                bool useW = fitW && iter >= 2;
                np = useW ? 9 : 6;
                var n = new double[np, np];
                var rhs = new double[np];
                ssr = 0;
                var v = new Vec3(p[0], p[1], p[2]);
                var b = new Vec3(p[3], p[4], p[5]);
                var w = new Vec3(p[6], p[7], p[8]);
                foreach (Shot s in shots)
                {
                    Mat3 m = s.EarthFixedToDevice;
                    Vec3 u = m * v;
                    Vec3 model = u - Vec3.Cross(w, u) + b;
                    Vec3 r = s.Gravity.Mean - model;
                    ssr += Vec3.Dot(r, r);
                    // d(model)/dv = (I - [w x]) M ; d/db = I ; d/dw = [u x]
                    Mat3 iw = new Mat3(1, w.Z, -w.Y, -w.Z, 1, w.X, w.Y, -w.X, 1);
                    Mat3 jv = iw * m;
                    var ux = new Mat3(0, -u.Z, u.Y, u.Z, 0, -u.X, -u.Y, u.X, 0);
                    double[] res = { r.X, r.Y, r.Z };
                    for (int row = 0; row < 3; row++)
                    {
                        var j = new double[np];
                        Vec3 jvRow = row == 0 ? jv.Row0 : row == 1 ? jv.Row1 : jv.Row2;
                        j[0] = jvRow.X; j[1] = jvRow.Y; j[2] = jvRow.Z;
                        j[3 + row] = 1.0;
                        if (useW)
                        {
                            Vec3 uxRow = row == 0 ? ux.Row0 : row == 1 ? ux.Row1 : ux.Row2;
                            j[6] = uxRow.X; j[7] = uxRow.Y; j[8] = uxRow.Z;
                        }
                        for (int i2 = 0; i2 < np; i2++)
                        {
                            rhs[i2] += j[i2] * res[row];
                            for (int k = 0; k < np; k++) n[i2, k] += j[i2] * j[k];
                        }
                    }
                }
                lastNormal = n;
                double[]? d = LinAlg.Solve(n, rhs);
                if (d == null) { message = "Calibration system is singular: tilt the phone differently between shots."; return null; }
                double step = 0;
                for (int i2 = 0; i2 < np; i2++) { p[i2] += d[i2]; step += d[i2] * d[i2]; }
                if (step < 1e-16 && (useW || !fitW)) break;
            }
            if (lastNormal == null) return null;

            // Observability: normalise columns, then look at the smallest eigenvalue.
            var scaled = new double[np, np];
            for (int i2 = 0; i2 < np; i2++)
                for (int k = 0; k < np; k++)
                    scaled[i2, k] = lastNormal[i2, k] / Math.Sqrt(lastNormal[i2, i2] * lastNormal[k, k]);
            (double[] eig, _) = LinAlg.SymmetricEigen(scaled);
            double minEig = double.MaxValue;
            foreach (double e in eig) minEig = Math.Min(minEig, e);
            if (minEig < 0.01)
            {
                if (fitW) return SolveMulti(shots, out message, estimateMisalignment: false);
                message = "Shots are too similar in orientation to calibrate. Tilt the phone more between shots, about different axes.";
                return null;
            }

            double[,]? inv = LinAlg.Inverse(lastNormal);
            if (inv == null) { message = "Calibration system is singular."; return null; }

            var vf = new Vec3(p[0], p[1], p[2]);
            var bias = new Vec3(p[3], p[4], p[5]);
            var wf = new Vec3(p[6], p[7], p[8]);
            double g = vf.Length;
            Vec3 zen = vf / g;
            (double lon, double lat) = zen.ToSpherical();

            int dof = 3 * shots.Count - np;
            double s2 = dof > 0 ? ssr / dof : 0;
            double varV = 0, varPar = 0;
            double[] zv = { zen.X, zen.Y, zen.Z };
            for (int i2 = 0; i2 < 3; i2++)
            {
                varV += inv[i2, i2] * s2;
                for (int k = 0; k < 3; k++) varPar += zv[i2] * zv[k] * inv[i2, k] * s2;
            }
            double horiz = Math.Sqrt(Math.Max(0, varV - varPar)) / g;
            double att = 0;
            foreach (Shot s in shots) { double a2 = AttitudeSigmaRad(s); att += a2 * a2; }
            att = Math.Sqrt(att) / shots.Count;
            double randomM = Math.Sqrt(horiz * horiz + att * att);
            double systematicM = AssumedCalibratedTiltErrorDeg * Astronomy.DegToRad;
            double sigma = Math.Sqrt(randomM * randomM + systematicM * systematicM);

            Vec3 upAvg = Vec3.Zero;
            foreach (Shot s in shots) upAvg += s.Gravity.Mean.Normalized();
            var cal = new SensorCalibration
            {
                AccelBias = bias, CameraMisalignment = wf, Shots = shots.Count, CreatedUtc = DateTime.UtcNow,
                PoseSpecific = false, PoseUp = upAvg.Normalized(), Method = "multi-pose (no reference)"
            };
            double biasDeg = Math.Atan2(bias.Length, g) * Astronomy.RadToDeg;
            message = $"Solved {shots.Count} shots. Accelerometer bias {bias.Length:F3} m/s² (≈{biasDeg:F2}°)" +
                      (fitW ? $", camera/IMU misalignment {wf.Length * Astronomy.RadToDeg:F2}°" : ", misalignment not estimated (need 4+ shots)") +
                      $", local g {g:F3} m/s², fit residual {Math.Sqrt(s2):F3} m/s².";
            return new PositionFix
            {
                LatitudeDeg = lat * Astronomy.RadToDeg,
                LongitudeDeg = Astronomy.WrapDeg180(lon * Astronomy.RadToDeg),
                UncertaintyKm = sigma * Astronomy.EarthRadiusKm,
                RandomUncertaintyKm = randomM * Astronomy.EarthRadiusKm,
                SystematicUncertaintyKm = systematicM * Astronomy.EarthRadiusKm,
                FramesUsed = shots.Count,
                AccelBias = bias,
                BiasEstimated = true,
                ShotCount = shots.Count,
                Calibration = cal,
                Notes = message
            };
        }

        /// <summary>
        /// One-time calibration at a known position (from GPS or typed coordinates).
        ///
        /// For each frame the true zenith is known, so the error in the phone's "up" reading is measured
        /// directly: r_k = a_k - g*u_k with u_k = M_k * zenith_true.
        ///  * Frames from one pose (e.g. a fixed mount): the mean r is stored as a pose-specific tilt
        ///    correction. Bias and camera misalignment cannot be told apart from one pose, but for that
        ///    pose their sum is exactly what has to be removed.
        ///  * Frames from clearly different tilts: r_k = b + [g u_k x] w is solved for the accelerometer
        ///    bias b and camera/IMU misalignment w, which then hold for any pose.
        /// </summary>
        public static SensorCalibration? CalibrateAtKnownLocation(IReadOnlyList<Shot> shots, double latDeg, double lonDeg,
            string method, double temperatureC, out string message)
        {
            message = "";
            if (shots.Count == 0) { message = "No solved frames to calibrate with."; return null; }
            Vec3 zTrue = Vec3.FromSpherical(lonDeg * Astronomy.DegToRad, latDeg * Astronomy.DegToRad);

            // How wrong was the capture before calibration?
            var before = new List<PositionFix>();
            foreach (Shot s in shots) before.Add(SolveSingle(s));
            PositionFix? beforeFix = CombineFixes(before, out _);
            double errBefore = beforeFix == null ? double.NaN
                : Astronomy.GreatCircleKm(beforeFix.LatitudeDeg, beforeFix.LongitudeDeg, latDeg, lonDeg);

            double g = 0;
            Vec3 upSum = Vec3.Zero;
            foreach (Shot s in shots) { g += s.Gravity.Mean.Length; upSum += s.Gravity.Mean.Normalized(); }
            g /= shots.Count;
            Vec3 poseUp = upSum.Normalized();
            double spread = 0;
            foreach (Shot s in shots) spread = Math.Max(spread, Vec3.Angle(s.Gravity.Mean, poseUp) * Astronomy.RadToDeg);

            if (shots.Count >= 2 && spread >= 15.0)
            {
                // Full model: a_k = g*u_k + [u_k x] w' + b with w' = g*w.
                // 7 linear unknowns (g, b, w'); g must be free or the bias component along "up" is lost.
                const int np = 7;
                var n = new double[np, np];
                var rhs = new double[np];
                foreach (Shot s in shots)
                {
                    Vec3 u = s.EarthFixedToDevice * zTrue;
                    Vec3 a = s.Gravity.Mean;
                    var ux = new Mat3(0, -u.Z, u.Y, u.Z, 0, -u.X, -u.Y, u.X, 0);
                    double[] res = { a.X, a.Y, a.Z };
                    double[] uu = { u.X, u.Y, u.Z };
                    Vec3[] rows = { ux.Row0, ux.Row1, ux.Row2 };
                    for (int row = 0; row < 3; row++)
                    {
                        var j = new double[np];
                        j[0] = uu[row];
                        j[1 + row] = 1.0;
                        j[4] = rows[row].X; j[5] = rows[row].Y; j[6] = rows[row].Z;
                        for (int i = 0; i < np; i++)
                        {
                            rhs[i] += j[i] * res[row];
                            for (int k = 0; k < np; k++) n[i, k] += j[i] * j[k];
                        }
                    }
                }
                var scaled = new double[np, np];
                for (int i = 0; i < np; i++)
                    for (int k = 0; k < np; k++)
                        scaled[i, k] = n[i, k] / Math.Sqrt(n[i, i] * n[k, k]);
                (double[] eig, _) = LinAlg.SymmetricEigen(scaled);
                double minEig = double.MaxValue;
                foreach (double e in eig) minEig = Math.Min(minEig, e);
                double[]? x = minEig >= 0.01 ? LinAlg.Solve(n, rhs) : null;
                if (x != null && x[0] > 5 && x[0] < 15)
                {
                    double gFit = x[0];
                    var cal = new SensorCalibration
                    {
                        AccelBias = new Vec3(x[1], x[2], x[3]),
                        CameraMisalignment = new Vec3(x[4], x[5], x[6]) / gFit,
                        Shots = shots.Count,
                        CreatedUtc = DateTime.UtcNow,
                        PoseSpecific = false,
                        PoseUp = poseUp,
                        Method = method + ", multi-pose",
                        TemperatureC = temperatureC,
                        ErrorBeforeKm = errBefore
                    };
                    message = $"Calibrated from {shots.Count} frames at {spread:F0}° of tilt spread: works in any pose. " +
                              $"Uncalibrated this capture was {errBefore:F1} km off.";
                    return cal;
                }
            }

            // Pose-specific: average tilt error of this pose.
            Vec3 rSum = Vec3.Zero;
            foreach (Shot s in shots)
            {
                Vec3 a = s.Gravity.Mean;
                Vec3 u = s.EarthFixedToDevice * zTrue;
                rSum += a - u * a.Length;
            }
            Vec3 b = rSum / shots.Count;
            double tiltDeg = Math.Atan2(b.Length, g) * Astronomy.RadToDeg;
            message = $"One-time calibration for this pose: tilt error {tiltDeg:F2}° removed. " +
                      $"Uncalibrated this capture was {errBefore:F1} km off. Keep the phone in the same mount/pose.";
            return new SensorCalibration
            {
                AccelBias = b,
                CameraMisalignment = Vec3.Zero,
                Shots = shots.Count,
                CreatedUtc = DateTime.UtcNow,
                PoseSpecific = true,
                PoseUp = poseUp,
                Method = method,
                TemperatureC = temperatureC,
                ErrorBeforeKm = errBefore
            };
        }

        /// <summary>
        /// Combines single-frame fixes taken from ONE phone pose (an exposure bracket / burst).
        /// Outliers (e.g. a frame where the phone was bumped) are rejected against a robust centre,
        /// the rest are averaged with 1/random-variance weights. Only the random error shrinks with
        /// more frames; the systematic part (shared tilt error) is carried through unchanged.
        /// </summary>
        public static PositionFix? CombineFixes(IReadOnlyList<PositionFix> fixes, out string message)
        {
            message = "";
            if (fixes.Count == 0) { message = "No frames could be solved."; return null; }
            if (fixes.Count == 1) { message = "1 frame solved."; return fixes[0]; }

            var units = new Vec3[fixes.Count];
            for (int i = 0; i < fixes.Count; i++)
                units[i] = Vec3.FromSpherical(fixes[i].LongitudeDeg * Astronomy.DegToRad, fixes[i].LatitudeDeg * Astronomy.DegToRad);

            // Robust centre: geometric median on the sphere (Weiszfeld iterations on unit vectors).
            Vec3 centre = Vec3.Zero;
            foreach (Vec3 u in units) centre += u;
            centre = centre.Normalized();
            for (int it = 0; it < 50; it++)
            {
                Vec3 acc = Vec3.Zero;
                double wsum = 0;
                foreach (Vec3 u in units)
                {
                    double d = Math.Max(Vec3.Angle(u, centre), 1e-9);
                    acc += u / d; wsum += 1 / d;
                }
                Vec3 next = (acc / wsum).Normalized();
                if (Vec3.Angle(next, centre) < 1e-12) { centre = next; break; }
                centre = next;
            }

            var distKm = new double[fixes.Count];
            for (int i = 0; i < fixes.Count; i++) distKm[i] = Vec3.Angle(units[i], centre) * Astronomy.EarthRadiusKm;

            var keep = new bool[fixes.Count];
            int rejected = 0;
            if (fixes.Count >= 3)
            {
                double[] sortedD = (double[])distKm.Clone();
                Array.Sort(sortedD);
                var sortedR = new double[fixes.Count];
                for (int i = 0; i < fixes.Count; i++) sortedR[i] = Math.Max(fixes[i].RandomUncertaintyKm, 0.1);
                Array.Sort(sortedR);
                double scale = Math.Max(1.4826 * Median(sortedD), Median(sortedR));
                for (int i = 0; i < fixes.Count; i++)
                {
                    keep[i] = distKm[i] <= 3.0 * scale;
                    if (!keep[i]) rejected++;
                }
            }
            else
            {
                for (int i = 0; i < fixes.Count; i++) keep[i] = true;
            }

            Vec3 sum = Vec3.Zero;
            double wTotal = 0, sysSum = 0;
            int used = 0;
            for (int i = 0; i < fixes.Count; i++)
            {
                if (!keep[i]) continue;
                double r = Math.Max(fixes[i].RandomUncertaintyKm, 0.1);
                double w = 1.0 / (r * r);
                sum += units[i] * w;
                wTotal += w;
                sysSum += fixes[i].SystematicUncertaintyKm;
                used++;
            }
            Vec3 mean = sum.Normalized();
            (double lon, double lat) = mean.ToSpherical();

            double randomKm = 1.0 / Math.Sqrt(wTotal);
            if (used >= 3)
            {
                // If the frames scatter more than their claimed noise, trust the scatter.
                double s2 = 0;
                for (int i = 0; i < fixes.Count; i++)
                {
                    if (!keep[i]) continue;
                    double r = Math.Max(fixes[i].RandomUncertaintyKm, 0.1);
                    double d = Vec3.Angle(units[i], mean) * Astronomy.EarthRadiusKm;
                    s2 += d * d / (r * r);
                }
                double empirical = Math.Sqrt(s2 / wTotal / (used - 1));
                randomKm = Math.Max(randomKm, empirical);
            }
            double systematicKm = sysSum / used;

            message = $"Combined {used} of {fixes.Count} frames" + (rejected > 0 ? $" ({rejected} rejected as outliers)." : ".");
            PositionFix first = fixes[0];
            return new PositionFix
            {
                LatitudeDeg = lat * Astronomy.RadToDeg,
                LongitudeDeg = Astronomy.WrapDeg180(lon * Astronomy.RadToDeg),
                UncertaintyKm = Math.Sqrt(randomKm * randomKm + systematicKm * systematicKm),
                RandomUncertaintyKm = randomKm,
                SystematicUncertaintyKm = systematicKm,
                FramesUsed = used,
                AccelBias = first.AccelBias,
                BiasEstimated = false,
                ShotCount = 1,
                Notes = message + " " + first.Notes
            };
        }

        private static double Median(double[] sorted) =>
            sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);

        private static double AttitudeSigmaRad(Shot s)
        {
            int n = Math.Max(1, s.MatchedStars - 2);
            return s.AttitudeRmsDeg * Astronomy.DegToRad / Math.Sqrt(n);
        }
    }
}
