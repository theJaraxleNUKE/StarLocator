using System;

namespace StarLocator.Core
{
    /// <summary>Immutable 3-vector of doubles.</summary>
    public readonly struct Vec3
    {
        public readonly double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 UnitX = new Vec3(1, 0, 0);
        public static readonly Vec3 UnitY = new Vec3(0, 1, 0);
        public static readonly Vec3 UnitZ = new Vec3(0, 0, 1);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Vec3 Normalized()
        {
            double l = Length;
            return l > 0 ? new Vec3(X / l, Y / l, Z / l) : this;
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>Angle between two vectors in radians (numerically stable form).</summary>
        public static double Angle(Vec3 a, Vec3 b) => Math.Atan2(Cross(a, b).Length, Dot(a, b));

        /// <summary>Unit vector from spherical angles (radians): x toward lon 0, z toward lat +90.</summary>
        public static Vec3 FromSpherical(double lonRad, double latRad)
        {
            double c = Math.Cos(latRad);
            return new Vec3(c * Math.Cos(lonRad), c * Math.Sin(lonRad), Math.Sin(latRad));
        }

        /// <summary>Returns (longitude, latitude) in radians; longitude in (-pi, pi].</summary>
        public (double Lon, double Lat) ToSpherical()
        {
            Vec3 n = Normalized();
            return (Math.Atan2(n.Y, n.X), Math.Asin(Math.Clamp(n.Z, -1.0, 1.0)));
        }

        public override string ToString() => $"({X:F6}, {Y:F6}, {Z:F6})";
    }

    /// <summary>Immutable row-major 3x3 matrix.</summary>
    public readonly struct Mat3
    {
        public readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public Mat3(double m00, double m01, double m02,
                    double m10, double m11, double m12,
                    double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public static readonly Mat3 Identity = new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public static Mat3 FromColumns(Vec3 c0, Vec3 c1, Vec3 c2) =>
            new Mat3(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);

        public static Mat3 FromRows(Vec3 r0, Vec3 r1, Vec3 r2) =>
            new Mat3(r0.X, r0.Y, r0.Z, r1.X, r1.Y, r1.Z, r2.X, r2.Y, r2.Z);

        public Vec3 Row0 => new Vec3(M00, M01, M02);
        public Vec3 Row1 => new Vec3(M10, M11, M12);
        public Vec3 Row2 => new Vec3(M20, M21, M22);
        public Vec3 Col0 => new Vec3(M00, M10, M20);
        public Vec3 Col1 => new Vec3(M01, M11, M21);
        public Vec3 Col2 => new Vec3(M02, M12, M22);

        public Mat3 Transpose() => new Mat3(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        public double Trace => M00 + M11 + M22;

        public double Determinant =>
            M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

        public static Vec3 operator *(Mat3 m, Vec3 v) => new Vec3(
            m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
            m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
            m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

        public static Mat3 operator *(Mat3 a, Mat3 b) => new Mat3(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
            a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
            a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
            a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
            a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
            a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
            a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        /// <summary>Coordinate-frame rotation about X by angle a (radians): R1(a).</summary>
        public static Mat3 R1(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(1, 0, 0, 0, c, s, 0, -s, c);
        }

        /// <summary>Coordinate-frame rotation about Y by angle a (radians): R2(a).</summary>
        public static Mat3 R2(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(c, 0, -s, 0, 1, 0, s, 0, c);
        }

        /// <summary>Coordinate-frame rotation about Z by angle a (radians): R3(a).</summary>
        public static Mat3 R3(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(c, s, 0, -s, c, 0, 0, 0, 1);
        }

        /// <summary>Active rotation of vectors by angle a about a unit axis (Rodrigues).</summary>
        public static Mat3 AxisAngle(Vec3 axis, double a)
        {
            Vec3 k = axis.Normalized();
            double c = Math.Cos(a), s = Math.Sin(a), t = 1 - c;
            return new Mat3(
                t * k.X * k.X + c, t * k.X * k.Y - s * k.Z, t * k.X * k.Z + s * k.Y,
                t * k.X * k.Y + s * k.Z, t * k.Y * k.Y + c, t * k.Y * k.Z - s * k.X,
                t * k.X * k.Z - s * k.Y, t * k.Y * k.Z + s * k.X, t * k.Z * k.Z + c);
        }
    }

    /// <summary>Small dense linear-algebra helpers.</summary>
    public static class LinAlg
    {
        /// <summary>
        /// Jacobi eigen-decomposition of a symmetric n x n matrix.
        /// Returns eigenvalues and eigenvectors (eigenvectors[k] is the k-th eigenvector).
        /// </summary>
        public static (double[] Values, double[][] Vectors) SymmetricEigen(double[,] input)
        {
            int n = input.GetLength(0);
            var a = (double[,])input.Clone();
            var v = new double[n, n];
            for (int i = 0; i < n; i++) v[i, i] = 1.0;

            for (int sweep = 0; sweep < 100; sweep++)
            {
                double off = 0;
                for (int p = 0; p < n; p++)
                    for (int q = p + 1; q < n; q++)
                        off += a[p, q] * a[p, q];
                if (off < 1e-30) break;

                for (int p = 0; p < n; p++)
                {
                    for (int q = p + 1; q < n; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-300) continue;
                        double theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                        if (theta == 0) t = 1.0;
                        double c = 1.0 / Math.Sqrt(t * t + 1.0);
                        double s = t * c;

                        for (int k = 0; k < n; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }

            var values = new double[n];
            var vectors = new double[n][];
            for (int k = 0; k < n; k++)
            {
                values[k] = a[k, k];
                vectors[k] = new double[n];
                for (int i = 0; i < n; i++) vectors[k][i] = v[i, k];
            }
            return (values, vectors);
        }

        /// <summary>Solves A x = b by Gaussian elimination with partial pivoting. Returns null if singular.</summary>
        public static double[]? Solve(double[,] aIn, double[] bIn)
        {
            int n = bIn.Length;
            var a = (double[,])aIn.Clone();
            var b = (double[])bIn.Clone();
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
                if (Math.Abs(a[piv, col]) < 1e-14) return null;
                if (piv != col)
                {
                    for (int k = 0; k < n; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
                    (b[col], b[piv]) = (b[piv], b[col]);
                }
                for (int r = col + 1; r < n; r++)
                {
                    double f = a[r, col] / a[col, col];
                    if (f == 0) continue;
                    for (int k = col; k < n; k++) a[r, k] -= f * a[col, k];
                    b[r] -= f * b[col];
                }
            }
            var x = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double s = b[r];
                for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k];
                x[r] = s / a[r, r];
            }
            return x;
        }

        /// <summary>Inverse of a symmetric positive-definite (or general) matrix. Returns null if singular.</summary>
        public static double[,]? Inverse(double[,] a)
        {
            int n = a.GetLength(0);
            var inv = new double[n, n];
            for (int c = 0; c < n; c++)
            {
                var e = new double[n];
                e[c] = 1;
                var col = Solve(a, e);
                if (col == null) return null;
                for (int r = 0; r < n; r++) inv[r, c] = col[r];
            }
            return inv;
        }
    }
}
