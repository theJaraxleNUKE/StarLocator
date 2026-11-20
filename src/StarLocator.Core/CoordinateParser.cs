using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StarLocator.Core
{
    /// <summary>
    /// Parses a latitude/longitude pair typed or pasted by the user. Accepts
    ///   decimal:      "40.68925, -74.0445"   "40.68925 -74.0445"   "40.68925N 74.0445W"
    ///   deg/min/sec:  "40°41'21.3\"N 74°02'40.2\"W"   "40 41 21.3 N, 74 02 40.2 W"
    ///   deg/min:      "40°41.355'N 74°02.670'W"
    /// Latitude comes first. A hemisphere letter (N/S/E/W) overrides the sign.
    /// </summary>
    public static class CoordinateParser
    {
        private static readonly Regex Part = new Regex(
            @"(?<sign>[-+])?\s*(?<d>\d+(?:\.\d+)?)\s*(?:°|º|d|deg|\s)?\s*" +
            @"(?:(?<m>\d+(?:\.\d+)?)\s*(?:'|′|’|m|min|\s)\s*)?" +
            @"(?:(?<s>\d+(?:\.\d+)?)\s*(?:""|″|”|''|s|sec)?\s*)?" +
            @"(?<h>[NnSsEeWw])?",
            RegexOptions.CultureInvariant);

        public static bool TryParse(string? text, out double latitude, out double longitude, out string error)
        {
            latitude = longitude = double.NaN;
            error = "";
            if (string.IsNullOrWhiteSpace(text)) { error = "Enter latitude and longitude."; return false; }

            string t = text.Trim().Replace(';', ',');
            var values = new List<(double Value, char Hemi)>();
            int pos = 0;
            while (pos < t.Length && values.Count < 3)
            {
                while (pos < t.Length && (char.IsWhiteSpace(t[pos]) || t[pos] == ',')) pos++;
                if (pos >= t.Length) break;
                Match m = Part.Match(t, pos);
                if (!m.Success || m.Index != pos || m.Length == 0) { error = $"Could not read the coordinates near \"{t.Substring(pos)}\"."; return false; }
                double d = double.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
                double min = m.Groups["m"].Success ? double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
                double sec = m.Groups["s"].Success ? double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
                if (min >= 60 || sec >= 60) { error = "Minutes and seconds must be below 60."; return false; }
                double v = d + min / 60.0 + sec / 3600.0;
                if (m.Groups["sign"].Value == "-") v = -v;
                char h = m.Groups["h"].Success ? char.ToUpperInvariant(m.Groups["h"].Value[0]) : '\0';
                if (h == 'S' || h == 'W') v = -Math.Abs(v);
                if (h == 'N' || h == 'E') v = Math.Abs(v);
                values.Add((v, h));
                pos = m.Index + m.Length;
            }
            if (values.Count != 2) { error = "Expected exactly two values: latitude then longitude."; return false; }
            if (values[0].Hemi == 'E' || values[0].Hemi == 'W' || values[1].Hemi == 'N' || values[1].Hemi == 'S')
            {
                error = "Latitude (N/S) must come first, then longitude (E/W).";
                return false;
            }
            latitude = values[0].Value;
            longitude = values[1].Value;
            if (Math.Abs(latitude) > 90) { error = "Latitude must be between -90 and 90."; return false; }
            if (Math.Abs(longitude) > 180) { error = "Longitude must be between -180 and 180."; return false; }
            return true;
        }
    }
}
