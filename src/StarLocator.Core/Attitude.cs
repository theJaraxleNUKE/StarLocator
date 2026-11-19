using System;
using System.Collections.Generic;

namespace StarLocator.Core
{
    /// <summary>Attitude determination from vector pairs.</summary>
    public static class Attitude
    {
        /// <summary>
        /// Solves Wahba's problem with Davenport's q-method: returns the rotation A that best
        /// satisfies body_i = A * reference_i in the weighted least-squares sense.
        /// </summary>
        public static Mat3 SolveWahba(IReadOnlyList<Vec3> body, IReadOnlyList<Vec3> reference, IReadOnlyList<double>? weights = null)
        {
            if (body.Count != reference.Count || body.Count < 2)
                throw new ArgumentException("Need at least two vector pairs.");

            double b00 = 0, b01 = 0, b02 = 0, b10 = 0, b11 = 0, b12 = 0, b20 = 0, b21 = 0, b22 = 0;
            for (int i = 0; i < body.Count; i++)
            {
                double w = weights?[i] ?? 1.0;
                Vec3 b = body[i], r = reference[i];
                b00 += w * b.X * r.X; b01 += w * b.X * r.Y; b02 += w * b.X * r.Z;
                b10 += w * b.Y * r.X; b11 += w * b.Y * r.Y; b12 += w * b.Y * r.Z;
                b20 += w * b.Z * r.X; b21 += w * b.Z * r.Y; b22 += w * b.Z * r.Z;
            }

            double sigma = b00 + b11 + b22;
            double zx = b12 - b21, zy = b20 - b02, zz = b01 - b10;
            var k = new double[4, 4]
            {
                { 2 * b00 - sigma, b01 + b10, b02 + b20, zx },
                { b10 + b01, 2 * b11 - sigma, b12 + b21, zy },
                { b20 + b02, b21 + b12, 2 * b22 - sigma, zz },
                { zx, zy, zz, sigma }
            };

            (double[] vals, double[][] vecs) = LinAlg.SymmetricEigen(k);
            int best = 0;
            for (int i = 1; i < 4; i++) if (vals[i] > vals[best]) best = i;
            double[] q = vecs[best];
            return FromQuaternion(q[0], q[1], q[2], q[3]);
        }

        /// <summary>Attitude matrix from a (vector, scalar) quaternion, Shuster/Markley convention.</summary>
        public static Mat3 FromQuaternion(double e1, double e2, double e3, double q4)
        {
            double n = Math.Sqrt(e1 * e1 + e2 * e2 + e3 * e3 + q4 * q4);
            e1 /= n; e2 /= n; e3 /= n; q4 /= n;
            double ee = e1 * e1 + e2 * e2 + e3 * e3;
            double d = q4 * q4 - ee;
            return new Mat3(
                d + 2 * e1 * e1, 2 * e1 * e2 + 2 * q4 * e3, 2 * e1 * e3 - 2 * q4 * e2,
                2 * e2 * e1 - 2 * q4 * e3, d + 2 * e2 * e2, 2 * e2 * e3 + 2 * q4 * e1,
                2 * e3 * e1 + 2 * q4 * e2, 2 * e3 * e2 - 2 * q4 * e1, d + 2 * e3 * e3);
        }

        /// <summary>TRIAD: exact attitude from two vector pairs (the first pair is trusted more).</summary>
        public static Mat3 Triad(Vec3 b1, Vec3 b2, Vec3 r1, Vec3 r2)
        {
            Vec3 tb1 = b1.Normalized();
            Vec3 tb2 = Vec3.Cross(b1, b2).Normalized();
            Vec3 tb3 = Vec3.Cross(tb1, tb2);
            Vec3 tr1 = r1.Normalized();
            Vec3 tr2 = Vec3.Cross(r1, r2).Normalized();
            Vec3 tr3 = Vec3.Cross(tr1, tr2);
            Mat3 mb = Mat3.FromColumns(tb1, tb2, tb3);
            Mat3 mr = Mat3.FromColumns(tr1, tr2, tr3);
            return mb * mr.Transpose();
        }
    }
}
