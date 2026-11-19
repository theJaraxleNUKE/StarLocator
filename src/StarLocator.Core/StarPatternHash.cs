using System;
using System.Collections.Generic;

namespace StarLocator.Core
{
    /// <summary>
    /// Star Pattern Hash Table (SPHT), Section 3.1 of Marbel, Ben-Moshe &amp; Yozevitch (2020).
    ///
    /// Every catalog "starset" (triplet of stars that can appear together in the camera's field
    /// of view) is keyed by its three pairwise angular distances, quantised and sorted. The table
    /// is built once (O(N^3) offline) and gives O(1)-style look-ups at run time.
    ///
    /// Implementation notes:
    ///  * Distances are quantised with bin width = 2 x tolerance (the paper's "accuracy level").
    ///    A query visits every bin overlapped by [side - tol(side), side + tol(side)], so it is
    ///    guaranteed to return every catalog triangle whose sides lie within tolerance.
    ///  * Vertices are stored in the order of their opposite sides (shortest first), so a match
    ///    also yields the star-to-star correspondence.
    ///  * Keys are kept in a sorted long[] (binary search) instead of a dictionary to keep memory
    ///    small on phones.
    /// </summary>
    public sealed class StarPatternHash
    {
        private readonly long[] _keys;
        private readonly int[] _tri;          // 3 local star indices per entry, same order as _keys
        private readonly float[] _dist;       // local pairwise distances (radians), n x n

        /// <summary>Catalog stars used by the table (local index -> catalog star).</summary>
        public IReadOnlyList<CatalogStar> Stars { get; }
        public double ToleranceRad { get; }
        /// <summary>
        /// Additional tolerance proportional to side length. The paper (Section 4.1) observes that the
        /// frame-to-frame distance gap grows with star separation (focal-length and distortion errors scale
        /// with distance), so the allowed error is ToleranceRad + RelativeTolerance * side.
        /// </summary>
        public double RelativeTolerance { get; }
        public double MaxSideRad { get; }
        public double MinSideRad { get; }
        public double MagnitudeLimit { get; }
        public int TriangleCount => _keys.Length;

        private double BinRad => 2.0 * ToleranceRad;

        private StarPatternHash(List<CatalogStar> stars, long[] keys, int[] tri, float[] dist,
                                double tol, double relTol, double maxSide, double minSide, double magLimit)
        {
            Stars = stars; _keys = keys; _tri = tri; _dist = dist;
            ToleranceRad = tol; RelativeTolerance = relTol; MaxSideRad = maxSide; MinSideRad = minSide; MagnitudeLimit = magLimit;
        }

        public float Distance(int localA, int localB) => _dist[localA * Stars.Count + localB];

        /// <summary>Allowed absolute error for a side of the given length (radians).</summary>
        public double ToleranceFor(double sideRad) => ToleranceRad + RelativeTolerance * sideRad;

        public static StarPatternHash Build(StarCatalog catalog, double magLimit, double maxSideDeg,
                                            double toleranceDeg, double relativeTolerance = 0.03, double minSideDeg = 1.0)
        {
            List<CatalogStar> stars = catalog.BrighterThan(magLimit);
            int n = stars.Count;
            double maxSide = maxSideDeg * Astronomy.DegToRad;
            double minSide = minSideDeg * Astronomy.DegToRad;
            double tol = toleranceDeg * Astronomy.DegToRad;
            double bin = 2.0 * tol;

            var dist = new float[n * n];
            var neighbors = new List<int>[n];
            for (int i = 0; i < n; i++) neighbors[i] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double d = Vec3.Angle(stars[i].Unit, stars[j].Unit);
                    dist[i * n + j] = dist[j * n + i] = (float)d;
                    if (d <= maxSide && d >= minSide) neighbors[i].Add(j);
                }
            }

            var keyList = new List<long>(1 << 18);
            var triList = new List<int>(3 << 18);
            for (int i = 0; i < n; i++)
            {
                List<int> nb = neighbors[i];
                for (int a = 0; a < nb.Count; a++)
                {
                    int j = nb[a];
                    for (int b = a + 1; b < nb.Count; b++)
                    {
                        int k = nb[b];
                        double djk = dist[j * n + k];
                        if (djk > maxSide || djk < minSide) continue;
                        double dij = dist[i * n + j], dik = dist[i * n + k];
                        Order(dij, djk, dik, i, j, k, out double s0, out double s1, out double s2,
                              out int v0, out int v1, out int v2);
                        keyList.Add(MakeKey((int)(s0 / bin), (int)(s1 / bin), (int)(s2 / bin)));
                        triList.Add(v0); triList.Add(v1); triList.Add(v2);
                    }
                }
            }

            long[] keys = keyList.ToArray();
            var order = new int[keys.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(keys, order);
            var tri = new int[keys.Length * 3];
            for (int i = 0; i < order.Length; i++)
            {
                int src = order[i] * 3;
                tri[i * 3] = triList[src];
                tri[i * 3 + 1] = triList[src + 1];
                tri[i * 3 + 2] = triList[src + 2];
            }
            return new StarPatternHash(stars, keys, tri, dist, tol, relativeTolerance, maxSide, minSide, magLimit);
        }

        /// <summary>
        /// Sorts the three sides ascending and returns, for each sorted side, the vertex opposite to it.
        /// Side dij is opposite k, djk opposite i, dik opposite j.
        /// </summary>
        public static void Order(double dij, double djk, double dik, int i, int j, int k,
                                 out double s0, out double s1, out double s2,
                                 out int v0, out int v1, out int v2)
        {
            // (side, opposite vertex)
            double a = dij, b = djk, c = dik;
            int oa = k, ob = i, oc = j;
            if (a > b) { (a, b) = (b, a); (oa, ob) = (ob, oa); }
            if (b > c) { (b, c) = (c, b); (ob, oc) = (oc, ob); }
            if (a > b) { (a, b) = (b, a); (oa, ob) = (ob, oa); }
            s0 = a; s1 = b; s2 = c; v0 = oa; v1 = ob; v2 = oc;
        }

        private static long MakeKey(int q0, int q1, int q2) =>
            (long)q0 | ((long)q1 << 21) | ((long)q2 << 42);

        /// <summary>
        /// Finds all catalog triangles whose sorted sides are each within ToleranceFor(side) of (s0,s1,s2).
        /// Results hold the three local star indices in opposite-side order.
        /// </summary>
        public int Query(double s0, double s1, double s2, List<(int A, int B, int C)> results)
        {
            double bin = BinRad;
            double t0 = ToleranceFor(s0), t1 = ToleranceFor(s1), t2 = ToleranceFor(s2);
            int lo0 = Math.Max(0, (int)Math.Floor((s0 - t0) / bin)), hi0 = (int)Math.Floor((s0 + t0) / bin);
            int lo1 = Math.Max(0, (int)Math.Floor((s1 - t1) / bin)), hi1 = (int)Math.Floor((s1 + t1) / bin);
            int lo2 = Math.Max(0, (int)Math.Floor((s2 - t2) / bin)), hi2 = (int)Math.Floor((s2 + t2) / bin);
            int found = 0;
            int n = Stars.Count;
            for (int k0 = lo0; k0 <= hi0; k0++)
            {
                for (int k1 = Math.Max(k0, lo1); k1 <= hi1; k1++)
                {
                    for (int k2 = Math.Max(k1, lo2); k2 <= hi2; k2++)
                    {
                        long key = MakeKey(k0, k1, k2);
                        int lo = LowerBound(key);
                        for (int e = lo; e < _keys.Length && _keys[e] == key; e++)
                        {
                            int A = _tri[e * 3], B = _tri[e * 3 + 1], C = _tri[e * 3 + 2];
                            // Exact side check (side opposite A is between B and C, etc.)
                            if (Math.Abs(_dist[B * n + C] - s0) > t0) continue;
                            if (Math.Abs(_dist[A * n + C] - s1) > t1) continue;
                            if (Math.Abs(_dist[A * n + B] - s2) > t2) continue;
                            results.Add((A, B, C));
                            found++;
                        }
                    }
                }
            }
            return found;
        }

        private int LowerBound(long key)
        {
            int lo = 0, hi = _keys.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid] < key) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }
}
