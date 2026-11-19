using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace StarLocator.Core
{
    /// <summary>One star of the Yale Bright Star Catalog (J2000).</summary>
    public sealed class CatalogStar
    {
        public int Index { get; }
        public int Hr { get; }
        public string Name { get; }
        public double RaRad { get; }
        public double DecRad { get; }
        public double Mag { get; }
        /// <summary>Unit vector in J2000 equatorial coordinates.</summary>
        public Vec3 Unit { get; }

        public CatalogStar(int index, int hr, string name, double raRad, double decRad, double mag)
        {
            Index = index; Hr = hr; Name = name; RaRad = raRad; DecRad = decRad; Mag = mag;
            Unit = Vec3.FromSpherical(raRad, decRad);
        }

        public string DisplayName => string.IsNullOrEmpty(Name) ? "HR " + Hr.ToString(CultureInfo.InvariantCulture) : Name;
    }

    /// <summary>
    /// The star catalog. The paper uses the Yale Bright Star Catalog (BSC); it is embedded in this
    /// assembly as a compact CSV (HR, RA deg, Dec deg, Vmag, name).
    /// </summary>
    public sealed class StarCatalog
    {
        private readonly List<CatalogStar> _stars;

        public IReadOnlyList<CatalogStar> Stars => _stars;

        private StarCatalog(List<CatalogStar> stars) { _stars = stars; }

        public static StarCatalog LoadEmbedded()
        {
            Assembly asm = typeof(StarCatalog).Assembly;
            using Stream? s = asm.GetManifestResourceStream("StarLocator.Core.bsc5.csv");
            if (s == null) throw new InvalidOperationException("Embedded star catalog not found.");
            return Load(s);
        }

        public static StarCatalog Load(Stream csv)
        {
            var list = new List<CatalogStar>(9200);
            using var reader = new StreamReader(csv);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split(',');
                if (p.Length < 4) continue;
                int hr = int.Parse(p[0], CultureInfo.InvariantCulture);
                double ra = double.Parse(p[1], CultureInfo.InvariantCulture) * Astronomy.DegToRad;
                double dec = double.Parse(p[2], CultureInfo.InvariantCulture) * Astronomy.DegToRad;
                double mag = double.Parse(p[3], CultureInfo.InvariantCulture);
                string name = p.Length > 4 ? p[4].Trim() : string.Empty;
                list.Add(new CatalogStar(list.Count, hr, name, ra, dec, mag));
            }
            return new StarCatalog(list);
        }

        /// <summary>Catalog stars brighter than (or equal to) a magnitude limit.</summary>
        public List<CatalogStar> BrighterThan(double magLimit)
        {
            var r = new List<CatalogStar>();
            foreach (var s in _stars) if (s.Mag <= magLimit) r.Add(s);
            return r;
        }
    }
}
