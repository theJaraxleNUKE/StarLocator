using System;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Runtime;
using StarLocator.Core;

namespace StarLocator.Droid
{
    /// <summary>Reads Camera2 characteristics and builds the solver's pinhole camera model.</summary>
    public sealed class CameraInfo
    {
        // Camera2 constant values (stable across API levels).
        public const int LensFacingBack = 1;                 // CameraMetadata.LENS_FACING_BACK
        public const int CapabilityManualSensor = 1;         // REQUEST_AVAILABLE_CAPABILITIES_MANUAL_SENSOR
        public const int TimestampSourceRealtime = 1;        // SENSOR_INFO_TIMESTAMP_SOURCE_REALTIME
        public const int DistortionCorrectionFast = 1;       // DISTORTION_CORRECTION_MODE_FAST
        public const int DistortionCorrectionHighQuality = 2;// DISTORTION_CORRECTION_MODE_HIGH_QUALITY

        public int SensorOrientation { get; private set; } = 90;
        public bool ManualSensor { get; private set; }
        public bool RealtimeTimestamps { get; private set; }
        public long MinExposureNs { get; private set; } = 100_000;
        public long MaxExposureNs { get; private set; } = 100_000_000;
        public int MinIso { get; private set; } = 100;
        public int MaxIso { get; private set; } = 800;
        public float MinFocusDistance { get; private set; }
        /// <summary>Best distortion-correction mode the camera offers (0 = none).</summary>
        public int DistortionCorrectionMode { get; private set; }

        private Rect? _active;
        private Rect? _preCorrection;
        private float[]? _intrinsics;
        private float[]? _distortion;
        private float[]? _focalLengths;
        private Android.Util.SizeF? _physicalSize;
        private Android.Util.Size? _pixelArray;

        public static CameraInfo Read(CameraCharacteristics c)
        {
            var info = new CameraInfo
            {
                SensorOrientation = GetInt(c, CameraCharacteristics.SensorOrientation) ?? 90,
                MinFocusDistance = GetFloat(c, CameraCharacteristics.LensInfoMinimumFocusDistance) ?? 0f,
                _active = GetObj<Rect>(c, CameraCharacteristics.SensorInfoActiveArraySize),
                _preCorrection = GetObj<Rect>(c, CameraCharacteristics.SensorInfoPreCorrectionActiveArraySize),
                _intrinsics = GetFloatArray(c, CameraCharacteristics.LensIntrinsicCalibration),
                _focalLengths = GetFloatArray(c, CameraCharacteristics.LensInfoAvailableFocalLengths),
                _physicalSize = GetObj<Android.Util.SizeF>(c, CameraCharacteristics.SensorInfoPhysicalSize),
                _pixelArray = GetObj<Android.Util.Size>(c, CameraCharacteristics.SensorInfoPixelArraySize),
                RealtimeTimestamps = GetInt(c, CameraCharacteristics.SensorInfoTimestampSource) == TimestampSourceRealtime
            };

            int[]? caps = GetIntArray(c, CameraCharacteristics.RequestAvailableCapabilities);
            if (caps != null) info.ManualSensor = Array.IndexOf(caps, CapabilityManualSensor) >= 0;

            Android.Util.Range? exp = GetObj<Android.Util.Range>(c, CameraCharacteristics.SensorInfoExposureTimeRange);
            if (exp != null)
            {
                info.MinExposureNs = AsNumber(exp.Lower)?.LongValue() ?? info.MinExposureNs;
                info.MaxExposureNs = AsNumber(exp.Upper)?.LongValue() ?? info.MaxExposureNs;
            }
            Android.Util.Range? iso = GetObj<Android.Util.Range>(c, CameraCharacteristics.SensorInfoSensitivityRange);
            if (iso != null)
            {
                info.MinIso = AsNumber(iso.Lower)?.IntValue() ?? info.MinIso;
                info.MaxIso = AsNumber(iso.Upper)?.IntValue() ?? info.MaxIso;
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                info._distortion = GetFloatArray(c, CameraCharacteristics.LensDistortion);
                int[]? modes = GetIntArray(c, CameraCharacteristics.DistortionCorrectionAvailableModes);
                if (modes != null)
                {
                    if (Array.IndexOf(modes, DistortionCorrectionHighQuality) >= 0) info.DistortionCorrectionMode = DistortionCorrectionHighQuality;
                    else if (Array.IndexOf(modes, DistortionCorrectionFast) >= 0) info.DistortionCorrectionMode = DistortionCorrectionFast;
                }
            }
            return info;
        }

        /// <summary>
        /// Builds the camera model for an output stream of size w x h. Android scales the active
        /// array (cropped to the output aspect ratio, centred) onto the stream.
        /// </summary>
        public CameraModel BuildModel(int w, int h, out string source)
        {
            double arrW = _active?.Width() ?? _pixelArray?.Width ?? w;
            double arrH = _active?.Height() ?? _pixelArray?.Height ?? h;
            double aspect = (double)w / h;
            double regW, regH;
            if (arrW / arrH > aspect) { regH = arrH; regW = arrH * aspect; }
            else { regW = arrW; regH = arrW / aspect; }
            double offX = (arrW - regW) / 2.0, offY = (arrH - regH) / 2.0;
            double s = w / regW;

            var model = new CameraModel { Width = w, Height = h, SensorOrientationDeg = SensorOrientation };
            if (_intrinsics != null && _intrinsics.Length >= 4 && _intrinsics[0] > 0)
            {
                // API 28+: intrinsics are in pre-correction active-array coordinates; earlier: active array.
                double dx = 0, dy = 0;
                if (OperatingSystem.IsAndroidVersionAtLeast(28) && _preCorrection != null && _active != null)
                {
                    dx = _preCorrection.Left - _active.Left;
                    dy = _preCorrection.Top - _active.Top;
                }
                model.Fx = _intrinsics[0] * s;
                model.Fy = _intrinsics[1] * s;
                model.Cx = (_intrinsics[2] + dx - offX) * s;
                model.Cy = (_intrinsics[3] + dy - offY) * s;
                source = "factory lens calibration";
            }
            else if (_focalLengths != null && _focalLengths.Length > 0 && _physicalSize != null && _pixelArray != null && _physicalSize.Width > 0)
            {
                double fArray = _focalLengths[0] / _physicalSize.Width * _pixelArray.Width;
                model.Fx = model.Fy = fArray * s;
                model.Cx = (w - 1) / 2.0;
                model.Cy = (h - 1) / 2.0;
                source = "focal length / sensor size";
            }
            else
            {
                model.Fx = model.Fy = w * 0.8; // ~64° horizontal FOV guess
                model.Cx = (w - 1) / 2.0;
                model.Cy = (h - 1) / 2.0;
                source = "guessed (no lens data)";
            }

            // If the camera corrects distortion itself we leave the model undistorted; otherwise use
            // the factory LENS_DISTORTION coefficients when present.
            if (DistortionCorrectionMode == 0 && _distortion != null && _distortion.Length >= 5)
            {
                model.K1 = _distortion[0]; model.K2 = _distortion[1]; model.K3 = _distortion[2];
                model.P1 = _distortion[3]; model.P2 = _distortion[4];
                source += " + lens distortion";
            }
            else if (DistortionCorrectionMode != 0)
            {
                source += " (distortion corrected by camera)";
            }
            return model;
        }

        // ---- Camera2 metadata helpers --------------------------------------------------------

        private static T? GetObj<T>(CameraCharacteristics c, CameraCharacteristics.Key? key) where T : Java.Lang.Object
        {
            Java.Lang.Object? o = c.Get(key);
            return o?.JavaCast<T>();
        }

        /// <summary>Range bounds are Java Integer/Long boxed numbers; accept whatever static type the binding exposes.</summary>
        private static Java.Lang.Number? AsNumber(object? o) => o switch
        {
            null => null,
            Java.Lang.Number n => n,
            IJavaObject j => j.JavaCast<Java.Lang.Number>(),
            _ => null
        };

        private static int? GetInt(CameraCharacteristics c, CameraCharacteristics.Key? key) =>
            GetObj<Java.Lang.Integer>(c, key)?.IntValue();

        private static float? GetFloat(CameraCharacteristics c, CameraCharacteristics.Key? key) =>
            GetObj<Java.Lang.Float>(c, key)?.FloatValue();

        private static float[]? GetFloatArray(CameraCharacteristics c, CameraCharacteristics.Key? key)
        {
            Java.Lang.Object? o = c.Get(key);
            return o == null ? null : JNIEnv.GetArray<float>(o.Handle);
        }

        private static int[]? GetIntArray(CameraCharacteristics c, CameraCharacteristics.Key? key)
        {
            Java.Lang.Object? o = c.Get(key);
            return o == null ? null : JNIEnv.GetArray<int>(o.Handle);
        }
    }
}
