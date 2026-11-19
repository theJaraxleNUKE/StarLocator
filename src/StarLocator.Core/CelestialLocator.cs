using System;
using System.Collections.Generic;

namespace StarLocator.Core
{
    public sealed class LocatorOptions
    {
        /// <summary>Faintest catalog magnitude placed in the triangle hash table.</summary>
        public double HashMagnitudeLimit { get; set; } = 4.0;
        /// <summary>Largest triangle side kept in the hash table (degrees).</summary>
        public double HashMaxSideDeg { get; set; } = 30.0;
        /// <summary>Per-side distance tolerance for triangle matching (degrees).</summary>
        public double HashToleranceDeg { get; set; } = 0.4;
        /// <summary>
        /// Optional extra tolerance proportional to side length. Tests show that even 1% makes large
        /// triangles too ambiguous, so focal-length uncertainty is handled by FocalScaleTrials instead.
        /// </summary>
        public double HashRelativeTolerance { get; set; } = 0.0;
        public DetectorOptions Detector { get; set; } = new DetectorOptions();
        public IdentifierOptions Identifier { get; set; } = new IdentifierOptions();
    }

    /// <summary>Everything produced from one photo.</summary>
    public sealed class FrameResult
    {
        public List<DetectedStar> Detections { get; init; } = new List<DetectedStar>();
        public IdentificationResult? Identification { get; init; }
        public Shot? Shot { get; init; }
        public PositionFix? Fix { get; init; }
        public string Message { get; init; } = "";
        public bool Success => Fix != null;
    }

    /// <summary>
    /// High-level entry point: luminance image + camera model + gravity + time -> position.
    /// Thread-safe for concurrent ProcessFrame calls once constructed.
    /// </summary>
    public sealed class CelestialLocator
    {
        public StarCatalog Catalog { get; }
        public StarPatternHash Hash { get; }
        public LocatorOptions Options { get; }
        private readonly StarIdentifier _identifier;

        public CelestialLocator(LocatorOptions? options = null, StarCatalog? catalog = null)
        {
            Options = options ?? new LocatorOptions();
            Catalog = catalog ?? StarCatalog.LoadEmbedded();
            Hash = StarPatternHash.Build(Catalog, Options.HashMagnitudeLimit, Options.HashMaxSideDeg,
                                         Options.HashToleranceDeg, Options.HashRelativeTolerance);
            _identifier = new StarIdentifier(Catalog, Hash, Options.Identifier.ValidationMagLimit);
        }

        public FrameResult ProcessFrame(byte[] luma, int width, int height, int rowStride, CameraModel camera,
                                        GravityMeasurement gravity, DateTime utcMidExposure, SensorCalibration? calibration = null)
        {
            List<DetectedStar> det = StarDetector.Detect(luma, width, height, rowStride, Options.Detector);
            if (det.Count < 4)
                return new FrameResult { Detections = det, Message = $"Only {det.Count} stars detected. Use a longer exposure, higher ISO, or a darker/clearer sky." };

            Vec3 up = (gravity.Mean - (calibration?.AccelBias ?? Vec3.Zero)).Normalized();
            IdentificationResult id = _identifier.Identify(det, camera, up, Options.Identifier);
            if (!id.Success)
                return new FrameResult { Detections = det, Identification = id, Message = id.Message };

            Shot shot = PositionSolver.MakeShot(id, gravity, utcMidExposure);
            PositionFix fix = PositionSolver.SolveSingle(shot, calibration);
            return new FrameResult { Detections = det, Identification = id, Shot = shot, Fix = fix, Message = id.Message };
        }
    }
}
