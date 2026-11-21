using System;
using System.Collections.Generic;
using System.Linq;

namespace StarLocator.Core
{
    public sealed class IdentifierOptions
    {
        /// <summary>Brightest N detections used to form triangles (C(N,3) look-ups).</summary>
        public int TriangleStars { get; set; } = 24;
        /// <summary>Detections used for validation / final matching.</summary>
        public int ValidationStars { get; set; } = 80;
        /// <summary>Catalog stars down to this magnitude are used for validation matching.</summary>
        public double ValidationMagLimit { get; set; } = 5.5;
        /// <summary>Angular tolerance used for validation (RANSAC inlier test).</summary>
        public double MatchToleranceDeg { get; set; } = 0.35;
        /// <summary>Minimum weighted votes for a star label to be considered (paper: confidence threshold).</summary>
        public double MinVotes { get; set; } = 0.5;
        /// <summary>Number of best-voted labels used to build attitude hypotheses.</summary>
        public int HypothesisLabels { get; set; } = 12;
        /// <summary>Minimum number of matched stars to accept an orientation.</summary>
        public int MinMatches { get; set; } = 6;
        /// <summary>Minimum number of independently voted labels that must agree with the final orientation.</summary>
        public int MinConsistentLabels { get; set; } = 4;
        /// <summary>Refine focal length (and K1 with enough stars) from the stars themselves.</summary>
        public bool RefineCamera { get; set; } = true;
        /// <summary>Optional diagnostics: receives (detection index, voted catalog star, votes) for the top labels.</summary>
        public Action<IReadOnlyList<(int Det, CatalogStar Star, double Votes)>>? LabelObserver { get; set; }
        /// <summary>Focal-length scale factors tried if the nominal camera model fails.</summary>
        public double[] FocalScaleTrials { get; set; } = { 1.0, 0.98, 1.02, 0.96, 1.04, 0.94, 1.06, 0.92, 1.08, 0.90, 1.10 };
        /// <summary>
        /// If no trial above succeeds, also try focal scales from WideFocalMin to WideFocalMax in 2% steps,
        /// nearest to 1.0 first. Needed for phones whose reported lens calibration is far off
        /// (one real phone reported a focal length 26% too short).
        /// </summary>
        public bool WideFocalSearch { get; set; } = true;
        public double WideFocalMin { get; set; } = 0.6;
        public double WideFocalMax { get; set; } = 1.6;
        /// <summary>
        /// Largest accepted fit RMS as a fraction of MatchToleranceDeg. Matches that are merely random
        /// coincidences spread evenly over the tolerance disc (RMS ≈ 0.71 × tolerance); a true solution
        /// sits far inside it (real frames: 0.04–0.06° against 0.35°).
        /// </summary>
        public double MaxRmsFraction { get; set; } = 0.5;
        /// <summary>
        /// A solution found by the wide focal sweep has been searched over many more camera models, so
        /// chance solutions are more likely: it must match this many times the normal minimum.
        /// </summary>
        public double WideSearchMatchFactor { get; set; } = 2.0;
    }

    public sealed class StarMatch
    {
        public int DetectionIndex { get; init; }
        public CatalogStar Star { get; init; } = null!;
        /// <summary>Observed direction in the device frame (refraction removed).</summary>
        public Vec3 DeviceRay { get; init; }
        public double ResidualDeg { get; init; }
        /// <summary>Weighted triangle votes for this label (the paper's per-star confidence).</summary>
        public double Votes { get; init; }
    }

    public sealed class IdentificationResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        /// <summary>Rotation mapping J2000 unit vectors to the device frame: d = A r.</summary>
        public Mat3 J2000ToDevice { get; init; }
        public List<StarMatch> Matches { get; init; } = new List<StarMatch>();
        public double RmsDeg { get; init; }
        public CameraModel Camera { get; init; } = null!;
        public double FocalScale { get; init; } = 1.0;
        public int Hypotheses { get; init; }
        public int ConsistentLabels { get; init; }
        public double ExpectedRandomMatches { get; init; }
        /// <summary>Matches needed to accept the orientation (statistical threshold).</summary>
        public int RequiredMatches { get; init; }
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// Steps 2 and 3 of the paper's pipeline: label stars using SPHT look-ups with
    /// per-star confidence voting (Section 3.2), then validate the orientation with a
    /// RANSAC-like nearest-neighbour check and refine it by least squares (Section 3.3).
    /// The camera focal length / K1 can be self-calibrated from the matched stars.
    /// </summary>
    public sealed class StarIdentifier
    {
        private readonly StarCatalog _catalog;
        private readonly StarPatternHash _hash;
        private readonly List<CatalogStar> _validation;
        private readonly Dictionary<int, int> _hashLocalByCatalogIndex = new Dictionary<int, int>();

        public StarPatternHash Hash => _hash;

        public StarIdentifier(StarCatalog catalog, StarPatternHash hash, double validationMagLimit)
        {
            _catalog = catalog;
            _hash = hash;
            _validation = catalog.BrighterThan(Math.Max(validationMagLimit, hash.MagnitudeLimit));
            for (int i = 0; i < hash.Stars.Count; i++) _hashLocalByCatalogIndex[hash.Stars[i].Index] = i;
        }

        public IdentificationResult Identify(IReadOnlyList<DetectedStar> detections, CameraModel camera,
                                             Vec3? upDevice, IdentifierOptions? options = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IdentifierOptions o = options ?? new IdentifierOptions();
            IdentificationResult? best = null;
            int totalHyp = 0;
            foreach (double fs in FocalTrials(o))
            {
                CameraModel cam = camera.WithAdjustments(fs, camera.K1);
                IdentificationResult r = IdentifyWithCamera(detections, cam, upDevice, o, fs);
                bool wide = Array.IndexOf(o.FocalScaleTrials, fs) < 0;
                if (wide && r.Success && r.Matches.Count < o.WideSearchMatchFactor * r.RequiredMatches)
                    r = Reject(r, $"Weak match at focal ×{r.FocalScale:F3} ({r.Matches.Count} stars, need " +
                                  $"{Math.Ceiling(o.WideSearchMatchFactor * r.RequiredMatches)} when the focal length is searched).");
                totalHyp += r.Hypotheses;
                if (best == null || Better(r, best)) best = r;
                if (best.Success && best.Matches.Count >= 2 * o.MinMatches && best.ConsistentLabels >= 2 * o.MinConsistentLabels) break;
                // The wide search is only for when the normal trials found nothing at all.
                if (best.Success && Array.IndexOf(o.FocalScaleTrials, fs) < 0) break;
            }
            best ??= new IdentificationResult { Success = false, Message = "No detections", Camera = camera };
            best.ElapsedMs = sw.ElapsedMilliseconds;
            return best;
        }

        /// <summary>The fine trials first, then (optionally) a 2%-step sweep outward from 1.0 over the wide range.</summary>
        private static IEnumerable<double> FocalTrials(IdentifierOptions o)
        {
            var tried = new List<double>();
            foreach (double fs in o.FocalScaleTrials) { tried.Add(fs); yield return fs; }
            if (!o.WideFocalSearch) yield break;
            var wide = new List<double>();
            for (double fs = 1.0; fs <= o.WideFocalMax + 1e-9; fs *= 1.02) wide.Add(fs);
            for (double fs = 1.0 / 1.02; fs >= o.WideFocalMin - 1e-9; fs /= 1.02) wide.Add(fs);
            wide.Sort((a, b) => Math.Abs(Math.Log(a)).CompareTo(Math.Abs(Math.Log(b))));
            foreach (double fs in wide)
            {
                bool near = false;
                foreach (double t in tried) if (Math.Abs(Math.Log(fs / t)) < 0.009) { near = true; break; }
                if (near) continue;
                tried.Add(fs);
                yield return fs;
            }
        }

        private static IdentificationResult Reject(IdentificationResult r, string msg) => new IdentificationResult
        {
            Success = false, Message = msg, J2000ToDevice = r.J2000ToDevice, Matches = r.Matches, RmsDeg = r.RmsDeg,
            Camera = r.Camera, FocalScale = r.FocalScale, Hypotheses = r.Hypotheses, ConsistentLabels = r.ConsistentLabels,
            ExpectedRandomMatches = r.ExpectedRandomMatches, RequiredMatches = r.RequiredMatches
        };

        private static bool Better(IdentificationResult a, IdentificationResult b)
        {
            if (a.Success != b.Success) return a.Success;
            if (a.ConsistentLabels != b.ConsistentLabels) return a.ConsistentLabels > b.ConsistentLabels;
            if (a.Matches.Count != b.Matches.Count) return a.Matches.Count > b.Matches.Count;
            return a.RmsDeg < b.RmsDeg;
        }

        private Vec3[] Rays(IReadOnlyList<DetectedStar> det, int count, CameraModel cam, Vec3? up)
        {
            var rays = new Vec3[count];
            for (int i = 0; i < count; i++)
            {
                Vec3 r = cam.PixelToDeviceRay(det[i].U, det[i].V);
                if (up.HasValue) r = Astronomy.RemoveRefraction(r, up.Value);
                rays[i] = r;
            }
            return rays;
        }

        private IdentificationResult IdentifyWithCamera(IReadOnlyList<DetectedStar> detections, CameraModel cam,
                                                        Vec3? up, IdentifierOptions o, double focalScale)
        {
            int nTri = Math.Min(o.TriangleStars, detections.Count);
            int nVal = Math.Min(o.ValidationStars, detections.Count);
            if (nTri < 3)
                return Fail("Too few stars detected (" + detections.Count + "). Use a longer exposure or a darker sky.", cam, focalScale);

            Vec3[] rays = Rays(detections, nVal, cam, up);

            // ---- Section 3.2: triangle look-ups and per-star confidence voting -----------------
            int h = _hash.Stars.Count;
            // Votes are weighted by 1/(number of catalog triangles matching the frame triangle): an
            // unambiguous triangle is strong evidence, one that matches dozens of starsets is weak.
            var votes = new float[nTri * h];
            var results = new List<(int A, int B, int C)>(64);
            double maxSide = _hash.MaxSideRad + _hash.ToleranceFor(_hash.MaxSideRad);
            double minSide = _hash.MinSideRad - _hash.ToleranceRad;
            for (int i = 0; i < nTri; i++)
            {
                for (int j = i + 1; j < nTri; j++)
                {
                    double dij = Vec3.Angle(rays[i], rays[j]);
                    if (dij > maxSide || dij < minSide) continue;
                    for (int k = j + 1; k < nTri; k++)
                    {
                        double djk = Vec3.Angle(rays[j], rays[k]);
                        double dik = Vec3.Angle(rays[i], rays[k]);
                        if (djk > maxSide || dik > maxSide || djk < minSide || dik < minSide) continue;
                        StarPatternHash.Order(dij, djk, dik, i, j, k, out double s0, out double s1, out double s2,
                                              out int v0, out int v1, out int v2);
                        results.Clear();
                        int nRes = _hash.Query(s0, s1, s2, results);
                        if (nRes == 0) continue;
                        float w = 1.0f / nRes;
                        foreach (var t in results)
                        {
                            votes[v0 * h + t.A] += w;
                            votes[v1 * h + t.B] += w;
                            votes[v2 * h + t.C] += w;
                        }
                    }
                }
            }

            var labels = new List<(int Det, int Local, double Votes)>();
            for (int i = 0; i < nTri; i++)
            {
                int bestL = -1;
                float bestV = 0, second = 0;
                for (int l = 0; l < h; l++)
                {
                    float v = votes[i * h + l];
                    if (v > bestV) { second = bestV; bestV = v; bestL = l; }
                    else if (v > second) second = v;
                }
                if (bestL >= 0 && bestV >= o.MinVotes && bestV > 1.2f * second) labels.Add((i, bestL, bestV));
            }
            labels.Sort((a, b) => b.Votes.CompareTo(a.Votes));
            if (labels.Count > o.HypothesisLabels) labels.RemoveRange(o.HypothesisLabels, labels.Count - o.HypothesisLabels);
            o.LabelObserver?.Invoke(labels.Select(l => (l.Det, _hash.Stars[l.Local], l.Votes)).ToList());
            if (labels.Count < 2)
                return Fail("Could not recognise a star pattern (" + nTri + " stars tried).", cam, focalScale);

            // ---- Section 3.3: RANSAC-like validation of orientation hypotheses ----------------
            // Each pair of voted labels gives an exact attitude (TRIAD). A hypothesis is scored by how
            // many OTHER independently-voted labels it explains. Random star fields almost never make
            // four voted labels agree on one rotation, which is what rejects false fixes.
            double tol = o.MatchToleranceDeg * Astronomy.DegToRad;
            Mat3 bestA = Mat3.Identity;
            List<(int Det, CatalogStar Star, double Err)>? bestSet = null;
            int bestConsistent = 0;
            double bestErr = double.MaxValue;
            int hypotheses = 0;
            for (int a = 0; a < labels.Count; a++)
            {
                for (int b = a + 1; b < labels.Count; b++)
                {
                    var la = labels[a]; var lb = labels[b];
                    if (la.Local == lb.Local || la.Det == lb.Det) continue;
                    Vec3 ra = _hash.Stars[la.Local].Unit, rb = _hash.Stars[lb.Local].Unit;
                    double dFrame = Vec3.Angle(rays[la.Det], rays[lb.Det]);
                    double dCat = Vec3.Angle(ra, rb);
                    if (dCat < 2.0 * Astronomy.DegToRad) continue;
                    if (Math.Abs(dFrame - dCat) > _hash.ToleranceFor(dCat)) continue;
                    hypotheses++;
                    Mat3 A = Attitude.Triad(rays[la.Det], rays[lb.Det], ra, rb);
                    var set = ConsistentLabels(labels, rays, A, rays[la.Det], 1.5);
                    double err = Rms(set);
                    if (set.Count > bestConsistent || (set.Count == bestConsistent && err < bestErr))
                    {
                        bestConsistent = set.Count; bestErr = err; bestA = A; bestSet = set;
                    }
                }
            }
            if (bestSet == null || bestConsistent < 3)
                return Fail("Star pattern not recognised with confidence (no consistent orientation).", cam, focalScale, hypotheses);

            // ---- Least-squares refinement (+ optional camera self-calibration) -----------------
            CameraModel curCam = cam;
            Mat3 curA = SolveFromSet(rays, bestSet);
            List<(int Det, CatalogStar Star, double Err)> set2 = bestSet;
            double curTol = Math.Max(tol, 0.8 * Astronomy.DegToRad);
            for (int iter = 0; iter < 5; iter++)
            {
                if (o.RefineCamera && set2.Count >= 5)
                {
                    curCam = RefineCamera(detections, set2, curCam, cam, up, out curA);
                    rays = Rays(detections, nVal, curCam, up);
                }
                var next = MatchAll(rays, curA, curCam, curTol);
                if (next.Count >= set2.Count || iter > 0) set2 = next;
                if (set2.Count < 3) break;
                curA = SolveFromSet(rays, set2);
                curTol = Math.Max(tol, curTol * 0.7);
            }
            if (set2.Count < 3)
                return Fail("Orientation refinement diverged.", cam, focalScale, hypotheses);
            set2 = MatchAll(rays, curA, curCam, tol);
            if (set2.Count >= 3) curA = SolveFromSet(rays, set2);
            set2 = MatchAll(rays, curA, curCam, tol);

            int consistentFinal = ConsistentLabels(labels, rays, curA, null, 0).Count;
            double expected = ExpectedRandom(nVal, curCam, tol);
            int needed = (int)Math.Ceiling(Math.Max(o.MinMatches, expected + 5.0 * Math.Sqrt(expected) + 2.0));
            var voteLookup = new Dictionary<int, double>();
            foreach (var l in labels) voteLookup[l.Det] = l.Votes;

            var matches = set2.Select(m => new StarMatch
            {
                DetectionIndex = m.Det,
                Star = m.Star,
                DeviceRay = rays[m.Det],
                ResidualDeg = m.Err * Astronomy.RadToDeg,
                Votes = voteLookup.TryGetValue(m.Det, out double v) ? v : 0
            }).OrderBy(m => m.Star.Mag).ToList();

            double rms = Rms(set2) * Astronomy.RadToDeg;
            double maxRms = o.MaxRmsFraction * o.MatchToleranceDeg;
            bool ok = matches.Count >= needed && consistentFinal >= o.MinConsistentLabels && rms <= maxRms;
            string msg;
            if (ok) msg = $"Identified {matches.Count} stars, RMS {rms:F3}°";
            else if (rms > maxRms)
                msg = $"Fit too loose (RMS {rms:F3}° > {maxRms:F3}°): looks like a chance match; orientation not trusted.";
            else if (consistentFinal < o.MinConsistentLabels)
                msg = $"Only {consistentFinal} voted star labels agree (need {o.MinConsistentLabels}); orientation not trusted.";
            else msg = $"Only {matches.Count} stars matched (need {needed}); orientation not trusted.";
            return new IdentificationResult
            {
                Success = ok,
                Message = msg,
                J2000ToDevice = curA,
                Matches = matches,
                RmsDeg = rms,
                Camera = curCam,
                FocalScale = curCam.Fx / cam.Fx * focalScale,
                Hypotheses = hypotheses,
                ConsistentLabels = consistentFinal,
                ExpectedRandomMatches = expected,
                RequiredMatches = needed
            };
        }

        /// <summary>
        /// Voted labels whose catalog star lands near its detection under attitude A. With an anchor
        /// (the TRIAD reference star) the tolerance grows with distance from it, mirroring the
        /// distance-dependent gap; without an anchor a fixed tolerance of MatchTolerance x 1.5 is used.
        /// </summary>
        private List<(int Det, CatalogStar Star, double Err)> ConsistentLabels(
            List<(int Det, int Local, double Votes)> labels, Vec3[] rays, Mat3 a, Vec3? anchor, double scale)
        {
            var res = new List<(int Det, CatalogStar Star, double Err)>();
            var usedStars = new HashSet<int>();
            foreach (var l in labels)
            {
                if (usedStars.Contains(l.Local)) continue;
                Vec3 pred = a * _hash.Stars[l.Local].Unit;
                double err = Vec3.Angle(pred, rays[l.Det]);
                double allowed = anchor.HasValue
                    ? scale * _hash.ToleranceFor(Vec3.Angle(rays[l.Det], anchor.Value))
                    : 1.5 * Math.Max(_hash.ToleranceRad, 0.35 * Astronomy.DegToRad);
                if (err <= allowed)
                {
                    res.Add((l.Det, _hash.Stars[l.Local], err));
                    usedStars.Add(l.Local);
                }
            }
            return res;
        }

        private static IdentificationResult Fail(string msg, CameraModel cam, double fs, int hyp = 0) =>
            new IdentificationResult { Success = false, Message = msg, Camera = cam, FocalScale = fs, Hypotheses = hyp, J2000ToDevice = Mat3.Identity };

        private double ExpectedRandom(int detections, CameraModel cam, double tolRad)
        {
            // Number of validation stars that would fall within tolerance of a random point.
            double density = _validation.Count / (4.0 * Math.PI);
            double cap = 2.0 * Math.PI * (1.0 - Math.Cos(tolRad));
            return detections * density * cap;
        }

        private static Mat3 SolveFromSet(Vec3[] rays, List<(int Det, CatalogStar Star, double Err)> set)
        {
            var b = new List<Vec3>(set.Count);
            var r = new List<Vec3>(set.Count);
            foreach (var m in set) { b.Add(rays[m.Det]); r.Add(m.Star.Unit); }
            return Attitude.SolveWahba(b, r);
        }

        private static double Rms(List<(int Det, CatalogStar Star, double Err)> set)
        {
            if (set.Count == 0) return 0;
            double s = 0;
            foreach (var m in set) s += m.Err * m.Err;
            return Math.Sqrt(s / set.Count);
        }

        /// <summary>
        /// Projects every validation star into the device frame and pairs each detection with its
        /// nearest catalog star within tolerance (one-to-one, brightest/closest wins).
        /// </summary>
        private List<(int Det, CatalogStar Star, double Err)> MatchAll(Vec3[] rays, Mat3 j2000ToDevice, CameraModel cam, double tolRad)
        {
            Mat3 aT = j2000ToDevice.Transpose();
            // Boresight in J2000: the camera looks along device -Z.
            Vec3 bore = aT * new Vec3(0, 0, -1);
            double fovCos = Math.Cos(cam.DiagonalFovRad / 2.0 + 2.0 * Astronomy.DegToRad);
            var nearby = new List<CatalogStar>(512);
            foreach (var s in _validation) if (Vec3.Dot(s.Unit, bore) > fovCos) nearby.Add(s);

            double cosTol = Math.Cos(tolRad);
            var candidates = new List<(int Det, CatalogStar Star, double Err)>();
            for (int i = 0; i < rays.Length; i++)
            {
                Vec3 r = aT * rays[i];
                CatalogStar? bestS = null;
                double bestDot = cosTol;
                foreach (var s in nearby)
                {
                    double d = Vec3.Dot(r, s.Unit);
                    if (d > bestDot) { bestDot = d; bestS = s; }
                }
                if (bestS != null) candidates.Add((i, bestS, Vec3.Angle(r, bestS.Unit)));
            }
            // Enforce one-to-one on catalog stars, keeping the closest detection.
            var used = new Dictionary<int, (int Det, CatalogStar Star, double Err)>();
            foreach (var c in candidates)
            {
                if (!used.TryGetValue(c.Star.Index, out var prev) || c.Err < prev.Err) used[c.Star.Index] = c;
            }
            return used.Values.ToList();
        }

        /// <summary>Nelder-Mead over (focal scale, K1) minimising the attitude-fit residual of fixed matches.</summary>
        private static CameraModel RefineCamera(IReadOnlyList<DetectedStar> det, List<(int Det, CatalogStar Star, double Err)> set,
                                                CameraModel cam, CameraModel trialCam, Vec3? up, out Mat3 attitude)
        {
            bool fitK1 = set.Count >= 10;
            double baseK1 = cam.K1;
            double Cost(double[] p)
            {
                double fs = p[0];
                double k1 = fitK1 ? p[1] : baseK1;
                // Stay close to the trial camera so random patterns cannot be "fitted" by warping the lens.
                double totalScale = cam.Fx * fs / trialCam.Fx;
                if (totalScale < 0.92 || totalScale > 1.08 || Math.Abs(k1 - trialCam.K1) > 0.25) return 1e9;
                CameraModel c = cam.WithAdjustments(fs, k1);
                var b = new List<Vec3>(set.Count);
                var r = new List<Vec3>(set.Count);
                foreach (var m in set)
                {
                    Vec3 ray = c.PixelToDeviceRay(det[m.Det].U, det[m.Det].V);
                    if (up.HasValue) ray = Astronomy.RemoveRefraction(ray, up.Value);
                    b.Add(ray); r.Add(m.Star.Unit);
                }
                Mat3 A = Attitude.SolveWahba(b, r);
                double s = 0;
                for (int i = 0; i < b.Count; i++) { double e = Vec3.Angle(b[i], A * r[i]); s += e * e; }
                return s / b.Count;
            }

            double[] start = fitK1 ? new[] { 1.0, baseK1 } : new[] { 1.0 };
            double[] steps = fitK1 ? new[] { 0.02, 0.02 } : new[] { 0.02 };
            double[] bestP = NelderMead(Cost, start, steps, 200);
            CameraModel result = cam.WithAdjustments(bestP[0], fitK1 ? bestP[1] : baseK1);

            var bb = new List<Vec3>(); var rr = new List<Vec3>();
            foreach (var m in set)
            {
                Vec3 ray = result.PixelToDeviceRay(det[m.Det].U, det[m.Det].V);
                if (up.HasValue) ray = Astronomy.RemoveRefraction(ray, up.Value);
                bb.Add(ray); rr.Add(m.Star.Unit);
            }
            attitude = Attitude.SolveWahba(bb, rr);
            return result;
        }

        private static double[] NelderMead(Func<double[], double> f, double[] x0, double[] step, int maxIter)
        {
            int n = x0.Length;
            var pts = new double[n + 1][];
            var vals = new double[n + 1];
            for (int i = 0; i <= n; i++)
            {
                pts[i] = (double[])x0.Clone();
                if (i > 0) pts[i][i - 1] += step[i - 1];
                vals[i] = f(pts[i]);
            }
            for (int it = 0; it < maxIter; it++)
            {
                Array.Sort(vals, pts);
                if (Math.Abs(vals[n] - vals[0]) < 1e-16) break;
                var centroid = new double[n];
                for (int i = 0; i < n; i++) for (int d = 0; d < n; d++) centroid[d] += pts[i][d] / n;
                double[] Along(double t) { var p = new double[n]; for (int d = 0; d < n; d++) p[d] = centroid[d] + t * (pts[n][d] - centroid[d]); return p; }
                double[] xr = Along(-1); double fr = f(xr);
                if (fr < vals[0])
                {
                    double[] xe = Along(-2); double fe = f(xe);
                    if (fe < fr) { pts[n] = xe; vals[n] = fe; } else { pts[n] = xr; vals[n] = fr; }
                }
                else if (fr < vals[n - 1]) { pts[n] = xr; vals[n] = fr; }
                else
                {
                    double[] xc = Along(fr < vals[n] ? -0.5 : 0.5); double fc = f(xc);
                    if (fc < Math.Min(fr, vals[n])) { pts[n] = xc; vals[n] = fc; }
                    else
                    {
                        for (int i = 1; i <= n; i++)
                        {
                            for (int d = 0; d < n; d++) pts[i][d] = pts[0][d] + 0.5 * (pts[i][d] - pts[0][d]);
                            vals[i] = f(pts[i]);
                        }
                    }
                }
            }
            Array.Sort(vals, pts);
            return pts[0];
        }
    }
}
