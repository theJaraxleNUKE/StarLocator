using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Views;
using StarLocator.Core;

namespace StarLocator.Droid
{
    /// <summary>One captured long-exposure frame (luminance plane in raw sensor orientation).</summary>
    public sealed class CapturedFrame
    {
        public byte[] Luma { get; init; } = Array.Empty<byte>();
        public int Width { get; init; }
        public int Height { get; init; }
        public int RowStride { get; init; }
        public CameraModel Camera { get; init; } = null!;
        public string CameraModelSource { get; init; } = "";
        public long ExposureNs { get; init; }
        public int Iso { get; init; }
        /// <summary>Exposure window in SystemClock.elapsedRealtimeNanos() time base.</summary>
        public long StartRealtimeNs { get; init; }
        public long EndRealtimeNs { get; init; }
        public DateTime UtcMidExposure { get; init; }
        public bool PreciseTiming { get; init; }
    }

    /// <summary>
    /// Camera2 controller: live preview for aiming plus manual long-exposure YUV captures with
    /// focus at infinity. All camera callbacks run on a private handler thread.
    /// </summary>
    public sealed class CameraCapture : IDisposable
    {
        // Camera2 constant values.
        private const int ControlModeOff = 0, ControlModeAuto = 1;
        private const int AeModeOff = 0, AfModeOff = 0, NoiseReductionFast = 1;

        private readonly Context _context;
        private readonly CameraManager _manager;
        private HandlerThread? _thread;
        private Handler? _handler;
        private CameraDevice? _device;
        private CameraCaptureSession? _session;
        private ImageReader? _reader;
        private Surface? _previewSurface;
        private SurfaceTexture? _texture;
        private TaskCompletionSource<CapturedFrame>? _pending;
        private long _pendingExposureNs;
        private int _pendingIso;
        private long _requestRealtimeNs;
        private bool _usedFallbackSize;

        public CameraInfo? Info { get; private set; }
        public string CameraId { get; private set; } = "";
        public Android.Util.Size? CaptureSize { get; private set; }
        public bool IsReady => _session != null;

        /// <summary>Raised (on the camera thread) with status / error text.</summary>
        public event Action<string>? StatusChanged;

        public CameraCapture(Context context)
        {
            _context = context;
            _manager = context.GetSystemService(Context.CameraService)!.JavaCast<CameraManager>()!;
        }

        public void Open(SurfaceTexture previewTexture)
        {
            Close();
            _texture = previewTexture;
            _thread = new HandlerThread("StarLocatorCamera");
            _thread.Start();
            _handler = new Handler(_thread.Looper!);

            CameraId = FindBackCamera();
            CameraCharacteristics chars = _manager.GetCameraCharacteristics(CameraId);
            Info = CameraInfo.Read(chars);

            StreamConfigurationMap map = chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap)!.JavaCast<StreamConfigurationMap>()!;
            Android.Util.Size[] yuv = map.GetOutputSizes((int)ImageFormatType.Yuv420888) ?? Array.Empty<Android.Util.Size>();
            Android.Util.Size[] prev = map.GetOutputSizes(Java.Lang.Class.FromType(typeof(SurfaceTexture))) ?? Array.Empty<Android.Util.Size>();
            if (yuv.Length == 0) throw new InvalidOperationException("Camera has no YUV output.");

            CaptureSize = ChooseCaptureSize(yuv, 16_500_000);
            Android.Util.Size preview = ChoosePreviewSize(prev, CaptureSize);
            _texture.SetDefaultBufferSize(preview.Width, preview.Height);
            _previewSurface = new Surface(_texture);
            CreateReader(CaptureSize);

            _manager.OpenCamera(CameraId, new DeviceCallback(this), _handler);
        }

        private void CreateReader(Android.Util.Size size)
        {
            _reader?.Close();
            _reader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Yuv420888, 2);
            _reader.SetOnImageAvailableListener(new ImageListener(this), _handler);
        }

        private string FindBackCamera()
        {
            string[] ids = _manager.GetCameraIdList();
            foreach (string id in ids)
            {
                CameraCharacteristics c = _manager.GetCameraCharacteristics(id);
                Java.Lang.Object? facing = c.Get(CameraCharacteristics.LensFacing);
                if (facing != null && facing.JavaCast<Java.Lang.Integer>()!.IntValue() == CameraInfo.LensFacingBack) return id;
            }
            if (ids.Length == 0) throw new InvalidOperationException("No camera found.");
            return ids[0];
        }

        private static Android.Util.Size ChooseCaptureSize(Android.Util.Size[] sizes, long maxPixels)
        {
            Android.Util.Size? best = null;
            foreach (var s in sizes)
            {
                long px = (long)s.Width * s.Height;
                if (px > maxPixels) continue;
                if (best == null || px > (long)best.Width * best.Height) best = s;
            }
            if (best != null) return best;
            // Everything is larger than the cap: take the smallest.
            best = sizes[0];
            foreach (var s in sizes) if ((long)s.Width * s.Height < (long)best.Width * best.Height) best = s;
            return best;
        }

        private static Android.Util.Size ChoosePreviewSize(Android.Util.Size[] sizes, Android.Util.Size capture)
        {
            double aspect = (double)capture.Width / capture.Height;
            Android.Util.Size? best = null;
            foreach (var s in sizes)
            {
                if (s.Width > 1920 || s.Height > 1440) continue;
                if (Math.Abs((double)s.Width / s.Height - aspect) > 0.02) continue;
                if (best == null || s.Width * s.Height > best.Width * best.Height) best = s;
            }
            return best ?? (sizes.Length > 0 ? sizes[0] : new Android.Util.Size(1280, 720));
        }

        private void OnOpened(CameraDevice device)
        {
            _device = device;
            CreateSession();
        }

        private void CreateSession()
        {
            if (_device == null || _reader?.Surface == null || _previewSurface == null) return;
            var outputs = new List<Surface> { _previewSurface, _reader.Surface };
#pragma warning disable CS0618, CA1422 // CreateCaptureSession(List, ...) is deprecated in API 30 but works on all levels.
            _device.CreateCaptureSession(outputs, new SessionCallback(this), _handler);
#pragma warning restore CS0618, CA1422
        }

        private void OnConfigured(CameraCaptureSession session)
        {
            _session = session;
            try
            {
                CaptureRequest.Builder b = _device!.CreateCaptureRequest(CameraTemplate.Preview);
                b.AddTarget(_previewSurface!);
                Put(b, CaptureRequest.ControlMode, Java.Lang.Integer.ValueOf(ControlModeAuto));
                session.SetRepeatingRequest(b.Build(), null, _handler);
                StatusChanged?.Invoke($"Camera ready ({CaptureSize?.Width}x{CaptureSize?.Height}).");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Preview failed: " + ex.Message);
            }
        }

        private void OnConfigureFailed()
        {
            // Some LIMITED devices cannot stream preview + full-size YUV together: retry smaller.
            if (!_usedFallbackSize && _device != null)
            {
                _usedFallbackSize = true;
                CaptureSize = new Android.Util.Size(1920, 1080);
                CreateReader(CaptureSize);
                StatusChanged?.Invoke("Full-resolution capture not supported with preview; using 1920x1080.");
                CreateSession();
                return;
            }
            StatusChanged?.Invoke("Camera session configuration failed.");
        }

        /// <summary>Takes one manual long exposure. The returned task completes on a pool thread.</summary>
        public Task<CapturedFrame> CaptureAsync(long exposureNs, int iso)
        {
            if (_session == null || _device == null || _reader?.Surface == null || Info == null)
                throw new InvalidOperationException("Camera is not ready.");
            if (_pending != null) throw new InvalidOperationException("A capture is already in progress.");

            exposureNs = Math.Clamp(exposureNs, Info.MinExposureNs, Info.MaxExposureNs);
            iso = Math.Clamp(iso, Info.MinIso, Info.MaxIso);
            var tcs = new TaskCompletionSource<CapturedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            _pendingExposureNs = exposureNs;
            _pendingIso = iso;

            CaptureRequest.Builder b = _device.CreateCaptureRequest(CameraTemplate.StillCapture);
            b.AddTarget(_reader.Surface);
            if (Info.ManualSensor)
            {
                Put(b, CaptureRequest.ControlMode, Java.Lang.Integer.ValueOf(ControlModeOff));
                Put(b, CaptureRequest.ControlAeMode, Java.Lang.Integer.ValueOf(AeModeOff));
                Put(b, CaptureRequest.SensorExposureTime, Java.Lang.Long.ValueOf(exposureNs));
                Put(b, CaptureRequest.SensorFrameDuration, Java.Lang.Long.ValueOf(exposureNs));
                Put(b, CaptureRequest.SensorSensitivity, Java.Lang.Integer.ValueOf(iso));
            }
            if (Info.MinFocusDistance > 0)
            {
                // Focus at infinity (diopters = 0).
                Put(b, CaptureRequest.ControlAfMode, Java.Lang.Integer.ValueOf(AfModeOff));
                Put(b, CaptureRequest.LensFocusDistance, Java.Lang.Float.ValueOf(0f));
            }
            Put(b, CaptureRequest.NoiseReductionMode, Java.Lang.Integer.ValueOf(NoiseReductionFast));
            if (OperatingSystem.IsAndroidVersionAtLeast(28) && Info.DistortionCorrectionMode != 0)
                Put(b, CaptureRequest.DistortionCorrectionMode, Java.Lang.Integer.ValueOf(Info.DistortionCorrectionMode));

            _requestRealtimeNs = SystemClock.ElapsedRealtimeNanos();
            _session.Capture(b.Build(), new CaptureCallback(this), _handler);

            // Safety timeout: exposure + 15 s.
            long timeoutMs = exposureNs / 1_000_000 + 15_000;
            Task.Delay(TimeSpan.FromMilliseconds(timeoutMs)).ContinueWith(_ =>
            {
                if (ReferenceEquals(_pending, tcs) && tcs.TrySetException(new TimeoutException("Camera did not deliver the frame.")))
                    _pending = null;
            });
            return tcs.Task;
        }

        /// <summary>Sets a request key; the binding declares the static keys as nullable.</summary>
        private static void Put(CaptureRequest.Builder b, CaptureRequest.Key? key, Java.Lang.Object value)
        {
            if (key != null) b.Set(key, value);
        }

        private void OnImage(ImageReader reader)
        {
            Image? image = reader.AcquireNextImage();
            if (image == null) return;
            try
            {
                TaskCompletionSource<CapturedFrame>? tcs = _pending;
                if (tcs == null || Info == null) return; // preview never targets the reader; ignore strays
                long arrivalRt = SystemClock.ElapsedRealtimeNanos();
                DateTime arrivalUtc = DateTime.UtcNow;

                Image.Plane yPlane = image.GetPlanes()![0];
                Java.Nio.ByteBuffer buffer = yPlane.Buffer!;
                var data = new byte[buffer.Remaining()];
                buffer.Get(data);
                int w = image.Width, h = image.Height;

                long start, end;
                bool precise = Info.RealtimeTimestamps && image.Timestamp > 0;
                if (precise)
                {
                    start = image.Timestamp;               // SENSOR_TIMESTAMP = start of exposure
                    end = start + _pendingExposureNs;
                }
                else
                {
                    end = arrivalRt - 50_000_000;          // ~readout + delivery latency
                    start = Math.Max(_requestRealtimeNs, end - _pendingExposureNs);
                }
                long mid = (start + end) / 2;
                DateTime utcMid = arrivalUtc - TimeSpan.FromTicks((arrivalRt - mid) / 100);

                CameraModel model = Info.BuildModel(w, h, out string source);
                var frame = new CapturedFrame
                {
                    Luma = data, Width = w, Height = h, RowStride = yPlane.RowStride,
                    Camera = model, CameraModelSource = source,
                    ExposureNs = _pendingExposureNs, Iso = _pendingIso,
                    StartRealtimeNs = start, EndRealtimeNs = end, UtcMidExposure = utcMid, PreciseTiming = precise
                };
                _pending = null;
                tcs.TrySetResult(frame);
            }
            catch (Exception ex)
            {
                _pending?.TrySetException(ex);
                _pending = null;
            }
            finally
            {
                image.Close();
            }
        }

        private void OnCaptureFailed(string reason)
        {
            _pending?.TrySetException(new InvalidOperationException("Capture failed: " + reason));
            _pending = null;
        }

        public void Close()
        {
            try { _session?.Close(); } catch (Exception) { }
            _session = null;
            try { _device?.Close(); } catch (Exception) { }
            _device = null;
            try { _reader?.Close(); } catch (Exception) { }
            _reader = null;
            _previewSurface?.Release();
            _previewSurface = null;
            _pending?.TrySetCanceled();
            _pending = null;
            _usedFallbackSize = false;
            if (_thread != null)
            {
                _thread.QuitSafely();
                _thread = null;
                _handler = null;
            }
        }

        public void Dispose() => Close();

        // ---- Camera2 callback classes ---------------------------------------------------------

        private sealed class DeviceCallback : CameraDevice.StateCallback
        {
            private readonly CameraCapture _owner;
            public DeviceCallback(CameraCapture owner) { _owner = owner; }

            public override void OnOpened(CameraDevice camera) => _owner.OnOpened(camera);

            public override void OnDisconnected(CameraDevice camera)
            {
                camera.Close();
                _owner._device = null;
                _owner.StatusChanged?.Invoke("Camera disconnected.");
            }

            public override void OnError(CameraDevice camera, [GeneratedEnum] CameraError error)
            {
                camera.Close();
                _owner._device = null;
                _owner.StatusChanged?.Invoke("Camera error: " + error);
            }
        }

        private sealed class SessionCallback : CameraCaptureSession.StateCallback
        {
            private readonly CameraCapture _owner;
            public SessionCallback(CameraCapture owner) { _owner = owner; }
            public override void OnConfigured(CameraCaptureSession session) => _owner.OnConfigured(session);
            public override void OnConfigureFailed(CameraCaptureSession session) => _owner.OnConfigureFailed();
        }

        private sealed class CaptureCallback : CameraCaptureSession.CaptureCallback
        {
            private readonly CameraCapture _owner;
            public CaptureCallback(CameraCapture owner) { _owner = owner; }

            public override void OnCaptureFailed(CameraCaptureSession session, CaptureRequest request, CaptureFailure failure) =>
                _owner.OnCaptureFailed("reason " + failure.Reason);
        }

        private sealed class ImageListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
        {
            private readonly CameraCapture _owner;
            public ImageListener(CameraCapture owner) { _owner = owner; }
            public void OnImageAvailable(ImageReader? reader)
            {
                if (reader != null) _owner.OnImage(reader);
            }
        }
    }
}
