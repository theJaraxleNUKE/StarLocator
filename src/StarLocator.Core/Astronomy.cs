using System;

namespace StarLocator.Core
{
    /// <summary>
    /// Time scales, Earth rotation, precession and refraction needed to turn a
    /// star-tracker attitude plus a gravity vector into latitude/longitude.
    /// Accuracy target is ~1 arcsecond-level math; nutation (~17"), aberration (~20")
    /// and UT1-UTC (&lt;0.9 s) are ignored because the sensor limits are ~0.1 deg.
    /// </summary>
    public static class Astronomy
    {
        public const double DegToRad = Math.PI / 180.0;
        public const double RadToDeg = 180.0 / Math.PI;
        public const double ArcsecToRad = DegToRad / 3600.0;
        public const double J2000 = 2451545.0;
        public const double EarthRadiusKm = 6371.0;

        /// <summary>Julian Date from a UTC DateTime (UTC is used as a stand-in for UT1).</summary>
        public static double JulianDate(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
            // DateTime ticks are 100 ns since 0001-01-01 00:00 (proleptic Gregorian). JD of that epoch = 1721425.5.
            return 1721425.5 + utc.Ticks / (double)TimeSpan.TicksPerDay;
        }

        /// <summary>Greenwich Mean Sidereal Time in radians [0, 2pi) (IAU 1982 expression).</summary>
        public static double Gmst(DateTime utc)
        {
            double jd = JulianDate(utc);
            double d = jd - J2000;
            double t = d / 36525.0;
            double deg = 280.46061837 + 360.98564736629 * d + 0.000387933 * t * t - t * t * t / 38710000.0;
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            return deg * DegToRad;
        }

        /// <summary>
        /// IAU 1976 precession matrix P such that r_meanOfDate = P * r_J2000.
        /// P = R3(-z) R2(theta) R3(-zeta).
        /// </summary>
        public static Mat3 PrecessionFromJ2000(DateTime utc)
        {
            double t = (JulianDate(utc) - J2000) / 36525.0;
            double zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) * ArcsecToRad;
            double z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) * ArcsecToRad;
            double theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) * ArcsecToRad;
            return Mat3.R3(-z) * Mat3.R2(theta) * Mat3.R3(-zeta);
        }

        /// <summary>
        /// Rotation from J2000 equatorial (ICRS-like) coordinates to an Earth-fixed frame
        /// (x toward Greenwich meridian on the equator, z toward the north pole):
        /// r_ecef = R3(GMST) * P * r_J2000.
        /// </summary>
        public static Mat3 J2000ToEarthFixed(DateTime utc) => Mat3.R3(Gmst(utc)) * PrecessionFromJ2000(utc);

        /// <summary>
        /// Atmospheric refraction (Bennett 1982) for an APPARENT altitude, in radians.
        /// Standard conditions are 1010 hPa and 10 C; other conditions are scaled.
        /// </summary>
        public static double RefractionFromApparent(double apparentAltRad, double pressureHpa = 1010, double tempC = 10)
        {
            double h = apparentAltRad * RadToDeg;
            if (h < -1.0) return 0;
            double rArcmin = 1.0 / Math.Tan((h + 7.31 / (h + 4.4)) * DegToRad);
            rArcmin *= (pressureHpa / 1010.0) * (283.0 / (273.0 + tempC));
            return Math.Max(0, rArcmin) / 60.0 * DegToRad;
        }

        /// <summary>Atmospheric refraction (Saemundsson 1986) for a TRUE altitude, in radians.</summary>
        public static double RefractionFromTrue(double trueAltRad, double pressureHpa = 1010, double tempC = 10)
        {
            double h = trueAltRad * RadToDeg;
            if (h < -1.0) return 0;
            double rArcmin = 1.02 / Math.Tan((h + 10.3 / (h + 5.11)) * DegToRad);
            rArcmin *= (pressureHpa / 1010.0) * (283.0 / (273.0 + tempC));
            return Math.Max(0, rArcmin) / 60.0 * DegToRad;
        }

        /// <summary>
        /// Removes refraction from an observed direction: the star is lowered toward the horizon
        /// by R(h). 'up' is the local vertical in the same frame as 'observed'.
        /// </summary>
        public static Vec3 RemoveRefraction(Vec3 observed, Vec3 up, double pressureHpa = 1010, double tempC = 10)
        {
            Vec3 u = up.Normalized();
            Vec3 o = observed.Normalized();
            double sinH = Math.Clamp(Vec3.Dot(o, u), -1.0, 1.0);
            double h = Math.Asin(sinH);
            if (h < 3.0 * DegToRad || h > 89.99 * DegToRad) return o;
            Vec3 horiz = (o - u * sinH).Normalized();
            double hTrue = h - RefractionFromApparent(h, pressureHpa, tempC);
            return (horiz * Math.Cos(hTrue) + u * Math.Sin(hTrue)).Normalized();
        }

        /// <summary>Adds refraction to a true direction (used by the synthetic test generator).</summary>
        public static Vec3 ApplyRefraction(Vec3 trueDir, Vec3 up, double pressureHpa = 1010, double tempC = 10)
        {
            Vec3 u = up.Normalized();
            Vec3 o = trueDir.Normalized();
            double sinH = Math.Clamp(Vec3.Dot(o, u), -1.0, 1.0);
            double h = Math.Asin(sinH);
            if (h < 3.0 * DegToRad || h > 89.99 * DegToRad) return o;
            Vec3 horiz = (o - u * sinH).Normalized();
            double hApp = h + RefractionFromTrue(h, pressureHpa, tempC);
            return (horiz * Math.Cos(hApp) + u * Math.Sin(hApp)).Normalized();
        }

        /// <summary>Wraps an angle in degrees to (-180, 180].</summary>
        public static double WrapDeg180(double deg)
        {
            deg %= 360.0;
            if (deg > 180.0) deg -= 360.0;
            if (deg <= -180.0) deg += 360.0;
            return deg;
        }

        /// <summary>Great-circle distance in km between two lat/lon points (degrees).</summary>
        public static double GreatCircleKm(double lat1, double lon1, double lat2, double lon2)
        {
            Vec3 a = Vec3.FromSpherical(lon1 * DegToRad, lat1 * DegToRad);
            Vec3 b = Vec3.FromSpherical(lon2 * DegToRad, lat2 * DegToRad);
            return Vec3.Angle(a, b) * EarthRadiusKm;
        }

        /// <summary>Formats degrees as D°M'S.s" with a hemisphere letter.</summary>
        public static string ToDms(double deg, bool isLatitude)
        {
            char hemi = isLatitude ? (deg >= 0 ? 'N' : 'S') : (deg >= 0 ? 'E' : 'W');
            double a = Math.Abs(deg);
            int d = (int)Math.Floor(a);
            double mFull = (a - d) * 60.0;
            int m = (int)Math.Floor(mFull);
            double s = (mFull - m) * 60.0;
            if (s >= 59.95) { s = 0; m++; }
            if (m >= 60) { m = 0; d++; }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}°{1:00}'{2:00.0}\"{3}", d, m, s, hemi);
        }
    }
}
