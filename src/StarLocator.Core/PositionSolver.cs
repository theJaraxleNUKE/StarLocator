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

        public override string ToString() =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "bias {0:F3} m/s², misalignment {1:F2}° ({2} shots)",
                AccelBias.Length, CameraMisalignment.Length * Astronomy.RadToDeg, Shots);
    }

    public sealed class PositionFix
    {
        public double LatitudeDeg { get; init; }
        public double LongitudeDeg { get; init; }
        /// <summary>Estimated 1-sigma horizontal error (km).</summary>
        public double UncertaintyKm { get; init; }
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
            double sigma = Math.Sqrt(attErr * attErr + noise * noise + tilt * tilt);

            return new PositionFix
            {
                LatitudeDeg = lat * Astronomy.RadToDeg,
                LongitudeDeg = Astronomy.WrapDeg180(lon * Astronomy.RadToDeg),
                UncertaintyKm = sigma * Astronomy.EarthRadiusKm,
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
            double sigma = Math.Sqrt(horiz * horiz + att * att + Math.Pow(AssumedCalibratedTiltErrorDeg * Astronomy.DegToRad, 2));

            var cal = new SensorCalibration
            {
                AccelBias = bias, CameraMisalignment = wf, Shots = shots.Count, CreatedUtc = DateTime.UtcNow
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
                AccelBias = bias,
                BiasEstimated = true,
                ShotCount = shots.Count,
                Calibration = cal,
                Notes = message
            };
        }

        private static double AttitudeSigmaRad(Shot s)
        {
            int n = Math.Max(1, s.MatchedStars - 2);
            return s.AttitudeRmsDeg * Astronomy.DegToRad / Math.Sqrt(n);
        }
    }
}
