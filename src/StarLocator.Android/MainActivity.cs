using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using StarLocator.Core;

namespace StarLocator.Droid
{
    [Activity(Label = "Star Locator", MainLauncher = true, Exported = true,
              ScreenOrientation = ScreenOrientation.Portrait,
              Theme = "@android:style/Theme.Material.NoActionBar")]
    public class MainActivity : Activity, TextureView.ISurfaceTextureListener
    {
        private const int CameraPermissionRequest = 42;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Night-vision friendly palette.
        private static readonly Color Bg = Color.Rgb(8, 0, 0);
        private static readonly Color Fg = Color.Rgb(255, 90, 80);
        private static readonly Color Dim = Color.Rgb(170, 60, 55);

        private TextureView _preview = null!;
        private EditText _exposure = null!, _iso = null!;
        private Button _captureBtn = null!, _calibrateBtn = null!, _clearBtn = null!, _mapBtn = null!, _forgetBtn = null!;
        private CheckBox _saveFrames = null!;
        private TextView _status = null!, _result = null!, _tilt = null!;
        private ImageView _image = null!;

        private CameraCapture? _camera;
        private GravitySampler _gravity = null!;
        private Task<CelestialLocator> _locatorTask = null!;
        private readonly List<Shot> _shots = new List<Shot>();
        private SensorCalibration? _calibration;
        private PositionFix? _lastFix;
        private bool _busy;
        private SurfaceTexture? _surface;
        private Handler _ui = null!;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            _ui = new Handler(Looper.MainLooper!);
            BuildUi();

            _gravity = new GravitySampler(this);
            _ui.PostDelayed(UpdateTilt, 500);
            // Building the star-pattern hash table takes a moment; do it once in the background.
            _locatorTask = Task.Run(() => new CelestialLocator());
            SetStatus("Loading star catalog…");
            _locatorTask.ContinueWith(t => RunOnUiThread(() =>
                SetStatus(t.IsCompletedSuccessfully
                    ? $"Catalog ready: {t.Result.Hash.Stars.Count} stars, {t.Result.Hash.TriangleCount:N0} star triangles."
                    : "Catalog failed to load: " + t.Exception?.GetBaseException().Message)));
        }

        protected override void OnResume()
        {
            base.OnResume();
            _gravity.Start();
            if (CheckSelfPermission(Android.Manifest.Permission.Camera) != Permission.Granted)
                RequestPermissions(new[] { Android.Manifest.Permission.Camera }, CameraPermissionRequest);
            else if (_surface != null) OpenCamera();
        }

        protected override void OnPause()
        {
            _gravity.Stop();
            _camera?.Close();
            base.OnPause();
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode != CameraPermissionRequest) return;
            if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) { if (_surface != null) OpenCamera(); }
            else SetStatus("Camera permission is required to photograph the stars.");
        }

        // ---- TextureView.ISurfaceTextureListener ------------------------------------------------

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            _surface = surface;
            if (CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted) OpenCamera();
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            _camera?.Close();
            _surface = null;
            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

        // ---- Camera ----------------------------------------------------------------------------

        private void OpenCamera()
        {
            if (_surface == null) return;
            try
            {
                _camera ??= CreateCamera();
                _camera.Open(_surface);
                _calibration = Storage.LoadCalibration(this, _camera.CameraId);
                CameraInfo? info = _camera.Info;
                if (info != null)
                {
                    string manual = info.ManualSensor ? "" : "  ⚠ no manual exposure on this camera";
                    SetStatus($"Exposure up to {info.MaxExposureNs / 1e9:0.##} s, ISO {info.MinIso}–{info.MaxIso}.{manual}" +
                              (_calibration != null ? $"\nCalibration loaded: {_calibration}" : "\nNot calibrated yet."));
                }
                UpdateButtons();
            }
            catch (Exception ex)
            {
                SetStatus("Could not open camera: " + ex.Message);
            }
        }

        private CameraCapture CreateCamera()
        {
            var cam = new CameraCapture(this);
            cam.StatusChanged += msg => RunOnUiThread(() => AppendStatus(msg));
            return cam;
        }

        private async void OnCapture()
        {
            if (_busy || _camera == null) return;
            if (!_camera.IsReady) { SetStatus("Camera is still starting…"); return; }
            _busy = true;
            UpdateButtons();
            try
            {
                double expS = ParseDouble(_exposure.Text, 4.0);
                int iso = (int)ParseDouble(_iso.Text, 1600);
                SetStatus($"Exposing {expS:0.#} s at ISO {iso}… keep the phone perfectly still.");

                CapturedFrame frame = await _camera.CaptureAsync((long)(expS * 1e9), iso);
                // Let the accelerometer cover the full exposure window.
                await Task.Delay(100);
                GravityMeasurement? g = _gravity.Average(frame.StartRealtimeNs, frame.EndRealtimeNs);
                if (g == null) { SetStatus("No accelerometer data during the exposure."); return; }

                var warn = new StringBuilder();
                if (g.StdDev > 0.06) warn.Append($"⚠ Phone moved during exposure (σ {g.StdDev:F2} m/s²): result may be degraded.\n");
                if (!frame.PreciseTiming) warn.Append("Exposure time estimated (camera timestamps not in realtime base).\n");

                SetStatus($"Got {frame.Width}x{frame.Height} frame ({frame.CameraModelSource}). Identifying stars…");
                CelestialLocator locator = await _locatorTask;
                SensorCalibration? cal = _calibration;
                FrameResult result = await Task.Run(() =>
                    locator.ProcessFrame(frame.Luma, frame.Width, frame.Height, frame.RowStride, frame.Camera, g, frame.UtcMidExposure, cal));

                Bitmap bmp = await Task.Run(() => ResultRenderer.Render(frame, result, 1440));
                _image.SetImageBitmap(bmp);

                if (_saveFrames.Checked)
                {
                    string path = await Task.Run(() => Storage.SaveFrame(this, frame, g));
                    warn.Append("Saved " + path + "\n");
                }

                if (result.Fix != null && result.Shot != null && result.Identification != null)
                {
                    _shots.Add(result.Shot);
                    _lastFix = result.Fix;
                    ShowFix(result.Fix, $"{result.Identification.Matches.Count} stars identified (RMS {result.Identification.RmsDeg:F3}°, " +
                                        $"focal ×{result.Identification.FocalScale:F3}, {result.Identification.ElapsedMs} ms).");
                    SetStatus(warn + $"Shot {_shots.Count} stored. " +
                              (_shots.Count >= 4 ? "Tap CALIBRATE to solve all shots together." :
                               $"Take {4 - _shots.Count} more at different tilts to calibrate."));
                }
                else
                {
                    SetStatus(warn + $"{result.Detections.Count} stars detected. " + result.Message);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Capture failed: " + ex.Message);
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        private void OnCalibrate()
        {
            PositionFix? fix = PositionSolver.SolveMulti(_shots, out string msg);
            if (fix == null) { SetStatus(msg); return; }
            _lastFix = fix;
            if (fix.Calibration != null && _camera != null)
            {
                _calibration = fix.Calibration;
                Storage.SaveCalibration(this, _camera.CameraId, fix.Calibration);
            }
            ShowFix(fix, msg + "\nCalibration saved; future single shots will use it.");
            UpdateButtons();
        }

        private void ShowFix(PositionFix fix, string detail)
        {
            _result.Text =
                $"{Astronomy.ToDms(fix.LatitudeDeg, true)}   {Astronomy.ToDms(fix.LongitudeDeg, false)}\n" +
                string.Format(Inv, "{0:F4}, {1:F4}   ±{2:F0} km (1σ est.)\n", fix.LatitudeDeg, fix.LongitudeDeg, fix.UncertaintyKm) +
                detail + "\n" + fix.Notes;
            UpdateButtons();
        }

        private void OpenMap()
        {
            if (_lastFix == null) return;
            string lat = _lastFix.LatitudeDeg.ToString("F5", Inv), lon = _lastFix.LongitudeDeg.ToString("F5", Inv);
            try
            {
                var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse($"geo:{lat},{lon}?q={lat},{lon}(Star fix)"));
                StartActivity(intent);
            }
            catch (ActivityNotFoundException)
            {
                SetStatus("No map app installed.");
            }
        }

        private void UpdateTilt()
        {
            Vec3? a = _gravity.Latest;
            if (a.HasValue)
            {
                // Camera elevation above the horizon: the back camera looks along device -Z.
                Vec3 up = a.Value.Normalized();
                double elev = Math.Asin(Math.Clamp(-up.Z, -1, 1)) * Astronomy.RadToDeg;
                _tilt.Text = $"Camera elevation {elev:F0}°  (aim 45–90° at clear sky)   UTC {DateTime.UtcNow:HH:mm:ss}";
            }
            if (!IsFinishing) _ui.PostDelayed(UpdateTilt, 500);
        }

        // ---- UI construction --------------------------------------------------------------------

        private void BuildUi()
        {
            var scroll = new ScrollView(this);
            scroll.SetBackgroundColor(Bg);
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetPadding(24, 24, 24, 24);
            scroll.AddView(root);

            root.AddView(Label("Star Locator — position from the night sky", 20, true));
            root.AddView(Label("No GPS · no Wi-Fi · no network. Uses camera + accelerometer + clock (Marbel et al., Sensors 2020 star tracker).", 12, false));

            int width = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
            _preview = new TextureView(this) { SurfaceTextureListener = this };
            root.AddView(_preview, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, width * 4 / 3));

            _tilt = Label("", 13, false);
            root.AddView(_tilt);

            var settings = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            settings.AddView(Label("Exposure s ", 15, false));
            _exposure = NumberBox("4");
            settings.AddView(_exposure, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            settings.AddView(Label("  ISO ", 15, false));
            _iso = NumberBox("1600");
            settings.AddView(_iso, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            root.AddView(settings);

            var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _captureBtn = MakeButton("CAPTURE", (s, e) => OnCapture());
            _calibrateBtn = MakeButton("CALIBRATE", (s, e) => OnCalibrate());
            _clearBtn = MakeButton("CLEAR SHOTS", (s, e) => { _shots.Clear(); SetStatus("Shots cleared."); UpdateButtons(); });
            foreach (var b in new[] { _captureBtn, _calibrateBtn, _clearBtn })
                buttons.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            root.AddView(buttons);

            _saveFrames = new CheckBox(this) { Text = "Save frames for PC replay", Checked = false };
            _saveFrames.SetTextColor(Dim);
            root.AddView(_saveFrames);

            _result = Label("", 18, true);
            root.AddView(_result);
            _status = Label("", 13, false);
            _status.Typeface = Typeface.Monospace;
            root.AddView(_status);

            _image = new ImageView(this);
            _image.SetAdjustViewBounds(true);
            root.AddView(_image, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var bottom = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _mapBtn = MakeButton("OPEN IN MAP", (s, e) => OpenMap());
            _forgetBtn = MakeButton("FORGET CALIBRATION", (s, e) =>
            {
                if (_camera != null) Storage.ClearCalibration(this, _camera.CameraId);
                _calibration = null;
                SetStatus("Calibration cleared.");
                UpdateButtons();
            });
            bottom.AddView(_mapBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            bottom.AddView(_forgetBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            root.AddView(bottom);

            root.AddView(Label(
                "How to use\n" +
                "1. Go outside on a clear night, let the screen dim adapt, avoid the Moon.\n" +
                "2. Brace the phone (on a rock, car roof or tripod) with the back camera aimed 45–90° up.\n" +
                "3. CAPTURE and don't touch it during the exposure. Increase exposure/ISO if few stars are found.\n" +
                "4. For best accuracy: take 4–6 shots, tilting the phone differently each time " +
                "(portrait, landscape, leaning left/right), then CALIBRATE. This measures your phone's accelerometer " +
                "bias and camera mounting error and is remembered for later single shots.\n" +
                "Accuracy: typically tens of km uncalibrated, a few km calibrated. Phone clock error of 1 s ≈ 0.4 km.", 12, false));

            SetContentView(scroll);
            UpdateButtons();
        }

        private TextView Label(string text, float sp, bool bold)
        {
            var t = new TextView(this) { Text = text, TextSize = sp };
            t.SetTextColor(bold ? Fg : Dim);
            if (bold) t.SetTypeface(null, TypefaceStyle.Bold);
            t.SetPadding(0, 8, 0, 8);
            return t;
        }

        private EditText NumberBox(string value)
        {
            var e = new EditText(this) { Text = value, InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal };
            e.SetTextColor(Fg);
            return e;
        }

        private Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button(this) { Text = text, TextSize = 12 };
            b.SetTextColor(Fg);
            b.SetBackgroundColor(Color.Rgb(40, 0, 0));
            b.Click += onClick;
            return b;
        }

        private void UpdateButtons()
        {
            if (_captureBtn == null) return;
            _captureBtn.Enabled = !_busy;
            _calibrateBtn.Enabled = !_busy && _shots.Count >= 3;
            _clearBtn.Enabled = !_busy && _shots.Count > 0;
            _mapBtn.Enabled = _lastFix != null;
            _forgetBtn.Enabled = _calibration != null;
            _calibrateBtn.Text = $"CALIBRATE ({_shots.Count})";
        }

        private void SetStatus(string s) { if (_status != null) _status.Text = s; }
        private void AppendStatus(string s) { if (_status != null) _status.Text = (_status.Text + "\n" + s).Trim(); }

        private static double ParseDouble(string? s, double fallback) =>
            double.TryParse(s?.Replace(',', '.'), NumberStyles.Float, Inv, out double v) && v > 0 ? v : fallback;
    }
}
