using System;

namespace StarLocator.Core
{
    /// <summary>
    /// Pinhole camera with Android-style lens distortion, attached to a phone.
    ///
    /// Camera frame (same as Android LENS_POSE / OpenCV): +x = raw image right (+u),
    /// +y = raw image down (+v), +z = out of the lens.
    ///
    /// Device frame (Android SensorManager): +X right, +Y up (toward top of the screen in
    /// natural orientation), +Z out of the screen. The back camera looks along -Z.
    /// </summary>
    public sealed class CameraModel
    {
        /// <summary>Image width/height in pixels of the frames that will be analysed (raw sensor orientation).</summary>
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Focal lengths and principal point in pixels of the analysed image.</summary>
        public double Fx { get; set; }
        public double Fy { get; set; }
        public double Cx { get; set; }
        public double Cy { get; set; }

        /// <summary>
        /// Android LENS_DISTORTION coefficients [k1,k2,k3,p1,p2]: they map ideal (undistorted)
        /// normalised coordinates to distorted ones, the same convention as OpenCV.
        /// </summary>
        public double K1 { get; set; }
        public double K2 { get; set; }
        public double K3 { get; set; }
        public double P1 { get; set; }
        public double P2 { get; set; }

        /// <summary>Android SENSOR_ORIENTATION in degrees (0, 90, 180 or 270). Back cameras are usually 90.</summary>
        public int SensorOrientationDeg { get; set; } = 90;

        public CameraModel Clone() => (CameraModel)MemberwiseClone();

        /// <summary>Copy of this model with the focal length scaled and K1 overridden.</summary>
        public CameraModel WithAdjustments(double focalScale, double k1)
        {
            CameraModel c = Clone();
            c.Fx = Fx * focalScale;
            c.Fy = Fy * focalScale;
            c.K1 = k1;
            return c;
        }

        /// <summary>Diagonal field of view in radians.</summary>
        public double DiagonalFovRad
        {
            get
            {
                double hx = Math.Max(Cx, Width - Cx) / Fx;
                double hy = Math.Max(Cy, Height - Cy) / Fy;
                return 2.0 * Math.Atan(Math.Sqrt(hx * hx + hy * hy));
            }
        }

        /// <summary>Rotation that maps camera-frame vectors into the device frame (back camera).</summary>
        public Mat3 CameraToDevice
        {
            get
            {
                // Derived from SENSOR_ORIENTATION: the raw image has to be rotated clockwise by
                // theta to be upright in the device's natural orientation.
                double t = SensorOrientationDeg * Astronomy.DegToRad;
                double c = Math.Cos(t), s = Math.Sin(t);
                var xDev = new Vec3(c, -s, 0);
                var yDev = new Vec3(-s, -c, 0);
                var zDev = new Vec3(0, 0, -1);
                return Mat3.FromColumns(xDev, yDev, zDev);
            }
        }

        /// <summary>Converts a pixel (raw image) into a unit ray in the camera frame, removing lens distortion.</summary>
        public Vec3 PixelToCameraRay(double u, double v)
        {
            double xd = (u - Cx) / Fx;
            double yd = (v - Cy) / Fy;
            (double x, double y) = Undistort(xd, yd);
            return new Vec3(x, y, 1.0).Normalized();
        }

        /// <summary>Converts a pixel into a unit ray in the device frame.</summary>
        public Vec3 PixelToDeviceRay(double u, double v) => CameraToDevice * PixelToCameraRay(u, v);

        /// <summary>Projects a camera-frame direction to pixel coordinates. Returns false if behind the camera.</summary>
        public bool CameraRayToPixel(Vec3 ray, out double u, out double v)
        {
            u = v = 0;
            if (ray.Z <= 1e-6) return false;
            double x = ray.X / ray.Z, y = ray.Y / ray.Z;
            (double xd, double yd) = Distort(x, y);
            u = Cx + Fx * xd;
            v = Cy + Fy * yd;
            return true;
        }

        public bool DeviceRayToPixel(Vec3 deviceRay, out double u, out double v) =>
            CameraRayToPixel(CameraToDevice.Transpose() * deviceRay, out u, out v);

        public (double X, double Y) Distort(double x, double y)
        {
            double r2 = x * x + y * y;
            double radial = 1 + K1 * r2 + K2 * r2 * r2 + K3 * r2 * r2 * r2;
            double xd = x * radial + P1 * (2 * x * y) + P2 * (r2 + 2 * x * x);
            double yd = y * radial + P2 * (2 * x * y) + P1 * (r2 + 2 * y * y);
            return (xd, yd);
        }

        public (double X, double Y) Undistort(double xd, double yd)
        {
            if (K1 == 0 && K2 == 0 && K3 == 0 && P1 == 0 && P2 == 0) return (xd, yd);
            double x = xd, y = yd;
            for (int i = 0; i < 20; i++)
            {
                double r2 = x * x + y * y;
                double radial = 1 + K1 * r2 + K2 * r2 * r2 + K3 * r2 * r2 * r2;
                if (radial < 0.2) radial = 0.2;
                double dx = P1 * (2 * x * y) + P2 * (r2 + 2 * x * x);
                double dy = P2 * (2 * x * y) + P1 * (r2 + 2 * y * y);
                double nx = (xd - dx) / radial;
                double ny = (yd - dy) / radial;
                if (Math.Abs(nx - x) < 1e-12 && Math.Abs(ny - y) < 1e-12) { x = nx; y = ny; break; }
                x = nx; y = ny;
            }
            return (x, y);
        }

        /// <summary>
        /// Builds a camera model from basic Android camera characteristics when
        /// LENS_INTRINSIC_CALIBRATION is unavailable.
        /// </summary>
        public static CameraModel FromFocalLength(int width, int height, double focalMm, double sensorWidthMm, int sensorOrientationDeg)
        {
            double fx = focalMm / sensorWidthMm * width;
            return new CameraModel
            {
                Width = width,
                Height = height,
                Fx = fx,
                Fy = fx,
                Cx = (width - 1) / 2.0,
                Cy = (height - 1) / 2.0,
                SensorOrientationDeg = sensorOrientationDeg
            };
        }
    }
}
