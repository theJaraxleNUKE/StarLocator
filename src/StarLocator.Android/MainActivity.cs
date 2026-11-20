using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
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
        private const int LocationPermissionRequest = 43;
        private const int ExportRequest = 44;
        /// <summary>A pose-specific calibration is trusted only within this many degrees of its pose.</summary>
        private const double PoseWarnDeg = 3.0;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Night-vision friendly palette.
        private static readonly Color Bg = Color.Rgb(8, 0, 0);
        private static readonly Color Fg = Color.Rgb(255, 90, 80);
        private static readonly Color Dim = Color.Rgb(170, 60, 55);

        private TextureView _preview = null!;
        private EditText _exposure = null!, _iso = null!;
        private Button _captureBtn = null!, _calHereBtn = null!, _gpsBtn = null!, _mapBtn = null!, _forgetBtn = null!, _exportBtn = null!;
        private EditText _calCoords = null!;
        private TextView _calInfo = null!;
        private Button? _activeBtn;
        private string? _gpsFilledText;
        private string _lastSummary = "";
        private CheckBox _saveFrames = null!, _bracket = null!;
        private RadioButton _delayOff = null!, _delay5 = null!, _delay10 = null!;
        private Android.Media.ToneGenerator? _tone;
        private CancellationTokenSource? _cts;
        private TextView _status = null!, _result = null!, _tilt = null!;
        private ImageView _image = null!;

        private CameraCapture? _camera;
        private GravitySampler _gravity = null!;
        private Task<CelestialLocator> _locatorTask = null!;
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

        protected override void OnDestroy()
        {
            _tone?.Release();
            _tone = null;
            base.OnDestroy();
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
            if (requestCode == CameraPermissionRequest)
            {
                if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) { if (_surface != null) OpenCamera(); }
                else SetStatus("Camera permission is required to photograph the stars.");
            }
            else if (requestCode == LocationPermissionRequest)
            {
                if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted) ReadGps();
                else SetStatus("Precise location was not allowed. You can type the coordinates instead (e.g. from a map app).");
            }
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
                    double fscale = Storage.LoadFocalScale(this, _camera.CameraId);
                    SetStatus($"Exposure up to {info.MaxExposureNs / 1e9:0.##} s, ISO {info.MinIso}–{info.MaxIso}.{manual}" +
                              (Math.Abs(fscale - 1) > 0.002 ? $"\nLearned lens correction: focal length ×{fscale:F3}." : ""));
                }
                ShowCalibrationInfo();
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

        /// <summary>Everything one CAPTURE produced.</summary>
        private sealed class CaptureOutcome
        {
            public StringBuilder Report { get; } = new StringBuilder();
            public List<PositionFix> Fixes { get; } = new List<PositionFix>();
            public List<Shot> Shots { get; } = new List<Shot>();
            public Vec3 MeanGravity { get; set; }
        }

        /// <summary>CAPTURE (calibrate = false) or CALIBRATE HERE (calibrate = true).</summary>
        private async void OnCapture(bool calibrate, Button source)
        {
            if (_busy) { if (ReferenceEquals(source, _activeBtn)) _cts?.Cancel(); return; }  // the active button doubles as CANCEL
            if (_camera == null) return;
            if (!_camera.IsReady) { SetStatus("Camera is still starting…"); return; }
            CameraInfo? info = _camera.Info;
            if (info == null) return;

            double calLat = 0, calLon = 0;
            if (calibrate && !CoordinateParser.TryParse(_calCoords.Text, out calLat, out calLon, out string parseError))
            {
                SetStatus("Calibration needs your exact position: " + parseError);
                return;
            }
            string method = calibrate && _gpsFilledText != null && _calCoords.Text == _gpsFilledText
                ? "known location (GPS)" : "known location (typed)";

            _busy = true;
            _activeBtn = source;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            UpdateButtons();
            try
            {
                // Calibration frames are solved WITHOUT the old calibration: we want the raw error.
                CaptureOutcome o = await CaptureAndSolveAsync(info, calibrate ? null : _calibration, token);

                if (calibrate)
                {
                    double temp = ReadTemperatureC();
                    SensorCalibration? cal = PositionSolver.CalibrateAtKnownLocation(o.Shots, calLat, calLon, method, temp, out string msg);
                    if (cal != null)
                    {
                        _calibration = cal;
                        Storage.SaveCalibration(this, _camera.CameraId, cal);
                        _result.Text = "✓ Calibration saved.\n" + msg;
                    }
                    else
                    {
                        _result.Text = "Calibration failed: " + msg;
                    }
                    ShowCalibrationInfo();
                }
                else
                {
                    PositionFix? combined = PositionSolver.CombineFixes(o.Fixes, out string combineMsg);
                    if (combined != null)
                    {
                        _lastFix = combined;
                        var detail = new StringBuilder(combineMsg);
                        if (_calibration != null)
                        {
                            double poseDiff = _calibration.PoseDifferenceDeg(o.MeanGravity);
                            if (_calibration.PoseSpecific && poseDiff > PoseWarnDeg)
                                detail.Append($"\n⚠ Phone pose is {poseDiff:F1}° away from the calibration pose: the calibration may not apply. Use the same mount and seating.");
                            double t = ReadTemperatureC();
                            if (!double.IsNaN(t) && !double.IsNaN(_calibration.TemperatureC) && Math.Abs(t - _calibration.TemperatureC) >= 10)
                                detail.Append($"\nNote: phone is {t:F0} °C, calibrated at {_calibration.TemperatureC:F0} °C.");
                        }
                        ShowFix(combined, detail.ToString());
                    }
                    else
                    {
                        _result.Text = "No position: " + combineMsg;
                        o.Report.Append("Try a longer exposure, higher ISO, or a darker / clearer patch of sky.");
                    }
                }
                SetStatus(o.Report.ToString());
                _lastSummary = $"{DateTime.UtcNow:o}\n{_result.Text}\n\n{o.Report}\n\nCalibration: {(_calibration?.ToString() ?? "none")}\n";
            }
            catch (System.OperationCanceledException)
            {
                _result.Text = "Capture cancelled.";
            }
            catch (Exception ex)
            {
                SetStatus("Capture failed: " + ex.Message);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _busy = false;
                _activeBtn = null;
                UpdateButtons();
            }
        }

        /// <summary>Countdown, exposure bracket, then solve every frame. Shared by CAPTURE and CALIBRATE HERE.</summary>
        private async Task<CaptureOutcome> CaptureAndSolveAsync(CameraInfo info, SensorCalibration? cal, CancellationToken token)
        {
            var o = new CaptureOutcome();
            double expS = ParseDouble(_exposure.Text, 4.0);
            int iso = (int)ParseDouble(_iso.Text, 1600);
            long[] exposures = BuildExposures((long)(expS * 1e9), _bracket.Checked, info);

            // ---- Countdown so the phone can be placed camera-up (screen facing down) ----------
            int delay = _delay10.Checked ? 10 : _delay5.Checked ? 5 : 0;
            for (int sLeft = delay; sLeft > 0; sLeft--)
            {
                _result.Text = $"Place the phone — capturing in {sLeft}…";
                Beep(Android.Media.Tone.PropBeep, 120);
                await Task.Delay(1000, token);
            }
            if (delay > 0)
            {
                Beep(Android.Media.Tone.PropBeep2, 400);           // "go" signal, then let vibrations die down
                await Task.Delay(700, token);
            }

            // ---- Capture every frame first (keeps the time the phone must stay still short) ---
            var frames = new List<(CapturedFrame Frame, GravityMeasurement? G)>();
            for (int i = 0; i < exposures.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                _result.Text = $"Exposing frame {i + 1}/{exposures.Length} ({exposures[i] / 1e9:0.##} s)… don't touch the phone";
                CapturedFrame frame = await _camera!.CaptureAsync(exposures[i], iso);
                await Task.Delay(100);           // let accelerometer samples cover the full window
                frames.Add((frame, _gravity.Average(frame.StartRealtimeNs, frame.EndRealtimeNs)));
            }
            Beep(Android.Media.Tone.PropAck, 600);
            _result.Text = "Done capturing — you can pick up the phone. Identifying stars…";

            // ---- Keep the frames: always the latest capture (for EXPORT), optionally permanently ---
            string lastDir = Storage.LastCaptureDir(this);
            string keptDir = Storage.KeptFramesDir(this);
            bool keep = _saveFrames.Checked;
            await Task.Run(() =>
            {
                Storage.ClearDir(lastDir);
                foreach (var (frame, g) in frames)
                {
                    if (g == null) continue;
                    Storage.SaveFrame(lastDir, frame, g);
                    if (keep) Storage.SaveFrame(keptDir, frame, g);
                }
            });
            if (keep) o.Report.Append("Frames saved to ").Append(keptDir).Append('\n');

            // ---- Solve each frame independently -----------------------------------------------
            CelestialLocator locator = await _locatorTask;
            string camId = _camera!.CameraId;
            double fscale = Storage.LoadFocalScale(this, camId);
            var learned = new List<double>();
            Vec3 gSum = Vec3.Zero;
            (CapturedFrame Frame, FrameResult Result)? best = null;
            for (int i = 0; i < frames.Count; i++)
            {
                (CapturedFrame frame, GravityMeasurement? g) = frames[i];
                string label = $"Frame {i + 1} ({frame.ExposureNs / 1e9:0.##} s): ";
                if (g == null) { o.Report.Append(label).Append("no accelerometer data.\n"); continue; }
                gSum += g.Mean;
                CameraModel cam = frame.Camera.Clone();
                cam.Fx *= fscale;
                cam.Fy *= fscale;
                FrameResult r = await Task.Run(() =>
                    locator.ProcessFrame(frame.Luma, frame.Width, frame.Height, frame.RowStride, cam, g, frame.UtcMidExposure, cal));
                o.Report.Append(label).Append(r.Detections.Count).Append(" detections, ");
                if (r.Fix != null && r.Shot != null && r.Identification != null)
                {
                    IdentificationResult id = r.Identification;
                    o.Report.Append($"{id.Matches.Count} identified, RMS {id.RmsDeg:F3}°");
                    if (g.StdDev > 0.06) o.Report.Append($", ⚠ moved (σ {g.StdDev:F2} m/s²)");
                    o.Report.Append('\n');
                    o.Fixes.Add(r.Fix);
                    o.Shots.Add(r.Shot);
                    if (id.Matches.Count >= 20) learned.Add(fscale * id.FocalScale);
                    if (best == null || id.Matches.Count > (best.Value.Result.Identification?.Matches.Count ?? -1))
                        best = (frame, r);
                }
                else
                {
                    o.Report.Append(r.Message).Append('\n');
                    if (best == null || (best.Value.Result.Fix == null && r.Detections.Count > best.Value.Result.Detections.Count))
                        best = (frame, r);
                }
            }
            o.MeanGravity = gSum;
            frames.Clear();

            // ---- Remember the lens focal-length correction the stars revealed ---------------
            if (learned.Count > 0)
            {
                learned.Sort();
                double median = learned[learned.Count / 2];
                if (Math.Abs(median / fscale - 1) > 0.002)
                {
                    Storage.SaveFocalScale(this, camId, median);
                    o.Report.Append($"Lens correction learned: the phone's focal length ×{median:F4} (saved for this camera).\n");
                }
            }

            if (best != null)
            {
                var b = best.Value;
                Bitmap bmp = await Task.Run(() => ResultRenderer.Render(b.Frame, b.Result, 1440));
                _image.SetImageBitmap(bmp);
            }
            return o;
        }

        /// <summary>Single exposure, or a ¼×, ½×, 1×, 2× bracket clamped to what the camera supports.</summary>
        private static long[] BuildExposures(long baseNs, bool bracket, CameraInfo info)
        {
            double[] factors = bracket ? new[] { 0.25, 0.5, 1.0, 2.0 } : new[] { 1.0 };
            var list = new List<long>();
            foreach (double f in factors)
            {
                long e = Math.Clamp((long)(baseNs * f), info.MinExposureNs, info.MaxExposureNs);
                if (!list.Contains(e)) list.Add(e);
            }
            return list.ToArray();
        }

        private void Beep(Android.Media.Tone tone, int ms)
        {
            try
            {
                _tone ??= new Android.Media.ToneGenerator(Android.Media.Stream.Notification, 90);
                _tone.StartTone(tone, ms);
            }
            catch (Exception)
            {
                // Some devices refuse a ToneGenerator (e.g. audio focus); the countdown still works silently.
            }
        }

        private void OnUseGps()
        {
            if (_busy) return;
            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Permission.Granted)
            {
                RequestPermissions(new[] { Android.Manifest.Permission.AccessFineLocation, Android.Manifest.Permission.AccessCoarseLocation },
                                   LocationPermissionRequest);
                return;
            }
            ReadGps();
        }

        private async void ReadGps()
        {
            if (_busy) return;
            _busy = true;
            _activeBtn = _gpsBtn;
            _cts = new CancellationTokenSource();
            UpdateButtons();
            try
            {
                SetStatus("Waiting for GPS (used only for this one-time calibration)…");
                GpsFix fix = await GpsReader.GetFixAsync(this, 20, TimeSpan.FromSeconds(120),
                                                         msg => RunOnUiThread(() => SetStatus(msg)), _cts.Token);
                string text = string.Format(Inv, "{0:F6}, {1:F6}", fix.Latitude, fix.Longitude);
                _calCoords.Text = text;
                _gpsFilledText = text;
                double km = Math.Abs(fix.ClockOffsetS) * 0.46;
                SetStatus($"GPS position filled in (±{fix.AccuracyM:F0} m). " +
                          $"Phone clock is {Math.Abs(fix.ClockOffsetS):F1} s {(fix.ClockOffsetS > 0 ? "behind" : "ahead of")} GPS time " +
                          $"(≈{km:F1} km of longitude at the equator).\nNow mount the phone and tap CALIBRATE HERE.");
            }
            catch (System.OperationCanceledException)
            {
                SetStatus("GPS cancelled.");
            }
            catch (Exception ex)
            {
                SetStatus("GPS failed: " + ex.Message + "\nYou can type the coordinates instead.");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _busy = false;
                _activeBtn = null;
                UpdateButtons();
            }
        }

        private void OnExport()
        {
            string dir = Storage.LastCaptureDir(this);
            if (!System.IO.Directory.Exists(dir) || System.IO.Directory.GetFiles(dir).Length == 0)
            {
                SetStatus("Nothing to export yet: take a capture first.");
                return;
            }
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("application/zip");
            intent.PutExtra(Intent.ExtraTitle, $"starlocator_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");
            try { StartActivityForResult(intent, ExportRequest); }
            catch (ActivityNotFoundException) { SetStatus("This phone has no file picker to save the zip."); }
        }

        protected override async void OnActivityResult(int requestCode, [Android.Runtime.GeneratedEnum] Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != ExportRequest || resultCode != Result.Ok || data?.Data == null) return;
            Android.Net.Uri uri = data.Data;
            string dir = Storage.LastCaptureDir(this);
            string summary = _lastSummary;
            try
            {
                SetStatus("Writing zip…");
                int n = await Task.Run(() =>
                {
                    System.IO.Stream? s = ContentResolver?.OpenOutputStream(uri);
                    if (s == null) throw new InvalidOperationException("Could not open the destination.");
                    return Storage.WriteZip(s, dir, summary);
                });
                SetStatus($"Exported {n} files (frames + camera/accelerometer data) as a zip.");
            }
            catch (Exception ex)
            {
                SetStatus("Export failed: " + ex.Message);
            }
        }

        /// <summary>Battery temperature in °C (no permission needed), or NaN.</summary>
        private double ReadTemperatureC()
        {
            Intent? battery = RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            int t = battery?.GetIntExtra(BatteryManager.ExtraTemperature, int.MinValue) ?? int.MinValue;
            return t == int.MinValue ? double.NaN : t / 10.0;
        }

        private void ShowCalibrationInfo()
        {
            if (_calibration == null)
            {
                _calInfo.Text = "Not calibrated. Uncalibrated fixes are typically tens of km off.";
                return;
            }
            SensorCalibration c = _calibration;
            var sb = new StringBuilder();
            sb.Append($"Calibrated {c.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} — {c.Method}. ");
            sb.Append(c.PoseSpecific ? "Valid for the pose it was made in (use the same mount). " : "Valid in any pose. ");
            if (!double.IsNaN(c.TemperatureC)) sb.Append($"Phone was {c.TemperatureC:F0} °C. ");
            if (!double.IsNaN(c.ErrorBeforeKm)) sb.Append($"Uncalibrated error then: {c.ErrorBeforeKm:F1} km.");
            _calInfo.Text = sb.ToString();
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
                string pose = "";
                if (_calibration != null && _calibration.PoseSpecific)
                {
                    double d = _calibration.PoseDifferenceDeg(a.Value);
                    pose = d > PoseWarnDeg ? $"   ⚠ {d:F1}° off calibration pose" : $"   pose OK ({d:F1}°)";
                }
                _tilt.Text = $"Camera elevation {elev:F0}°   UTC {DateTime.UtcNow:HH:mm:ss}{pose}";
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
            root.AddView(Label("Position from the stars: camera + accelerometer + clock (Marbel et al., Sensors 2020 star tracker). " +
                               "No network. GPS is used only if you choose it for the one-time calibration.", 12, false));

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

            var delayRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            delayRow.AddView(Label("Countdown ", 15, false));
            var delayGroup = new RadioGroup(this) { Orientation = Orientation.Horizontal };
            _delayOff = MakeRadio("Off");
            _delay5 = MakeRadio("5 s");
            _delay10 = MakeRadio("10 s");
            delayGroup.AddView(_delayOff);
            delayGroup.AddView(_delay5);
            delayGroup.AddView(_delay10);
            _delay10.Checked = true;
            delayRow.AddView(delayGroup);
            root.AddView(delayRow);

            _bracket = new CheckBox(this) { Text = "Exposure bracket: ¼×, ½×, 1×, 2× (each frame solved, fixes combined)", Checked = true };
            _bracket.SetTextColor(Dim);
            root.AddView(_bracket);

            _captureBtn = MakeButton("CAPTURE", (s, e) => OnCapture(false, _captureBtn));
            root.AddView(_captureBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

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
            _exportBtn = MakeButton("EXPORT FRAMES (ZIP)", (s, e) => OnExport());
            bottom.AddView(_mapBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            bottom.AddView(_exportBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            root.AddView(bottom);

            // ---- One-time calibration ----
            root.AddView(Label("One-time calibration", 17, true));
            root.AddView(Label("At a spot whose position you know, put the phone in your mount and calibrate once. " +
                               "Fill in the position with GPS or type it (decimal or 40°41'21.3\"N 74°02'40.2\"W).", 12, false));
            _calCoords = new EditText(this) { Hint = "latitude, longitude" };
            _calCoords.SetTextColor(Fg);
            _calCoords.SetHintTextColor(Dim);
            root.AddView(_calCoords);
            var calRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _gpsBtn = MakeButton("USE GPS", (s, e) => { if (_busy && ReferenceEquals(_activeBtn, _gpsBtn)) _cts?.Cancel(); else OnUseGps(); });
            _calHereBtn = MakeButton("CALIBRATE HERE", (s, e) => OnCapture(true, _calHereBtn));
            calRow.AddView(_gpsBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            calRow.AddView(_calHereBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            root.AddView(calRow);
            _calInfo = Label("", 12, false);
            root.AddView(_calInfo);
            _forgetBtn = MakeButton("FORGET CALIBRATION", (s, e) =>
            {
                if (_camera != null) Storage.ClearCalibration(this, _camera.CameraId);
                _calibration = null;
                ShowCalibrationInfo();
                SetStatus("Calibration cleared.");
                UpdateButtons();
            });
            root.AddView(_forgetBtn);

            root.AddView(Label(
                "How to use\n" +
                "1. Go outside on a clear night, let the screen dim adapt, avoid the Moon.\n" +
                "2. Tap CAPTURE, then during the countdown (beeps) place the phone screen-down on a rock, car roof or tripod " +
                "with the back camera aimed 45–90° up.\n" +
                "3. Don't touch it until the long 'done' tone. With the bracket on, 4 exposures are taken and combined. " +
                "Increase exposure/ISO if few stars are found.\n" +
                "4. Calibrate once (below) with the phone in your mount at a spot of known position. " +
                "Afterwards, always use the same mount and seating; the app warns if the pose differs by more than 3°.\n" +
                "5. EXPORT FRAMES saves the last capture as a zip (frames + camera/accelerometer data) for analysis.\n" +
                "Accuracy: typically tens of km uncalibrated, a few km calibrated. Phone clock error of 1 s ≈ 0.4 km.", 12, false));

            SetContentView(scroll);
            ShowCalibrationInfo();
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

        private RadioButton MakeRadio(string text)
        {
            var r = new RadioButton(this) { Text = text, Id = View.GenerateViewId() };
            r.SetTextColor(Fg);
            return r;
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
            if (_captureBtn == null || _calHereBtn == null) return;
            _captureBtn.Text = _busy && ReferenceEquals(_activeBtn, _captureBtn) ? "CANCEL" : "CAPTURE";
            _calHereBtn.Text = _busy && ReferenceEquals(_activeBtn, _calHereBtn) ? "CANCEL" : "CALIBRATE HERE";
            _gpsBtn.Text = _busy && ReferenceEquals(_activeBtn, _gpsBtn) ? "CANCEL GPS" : "USE GPS";
            _captureBtn.Enabled = !_busy || ReferenceEquals(_activeBtn, _captureBtn);
            _calHereBtn.Enabled = !_busy || ReferenceEquals(_activeBtn, _calHereBtn);
            _gpsBtn.Enabled = !_busy || ReferenceEquals(_activeBtn, _gpsBtn);
            _mapBtn.Enabled = _lastFix != null;
            _exportBtn.Enabled = !_busy;
            _forgetBtn.Enabled = !_busy && _calibration != null;
        }

        private void SetStatus(string s) { if (_status != null) _status.Text = s; }
        private void AppendStatus(string s) { if (_status != null) _status.Text = (_status.Text + "\n" + s).Trim(); }

        private static double ParseDouble(string? s, double fallback) =>
            double.TryParse(s?.Replace(',', '.'), NumberStyles.Float, Inv, out double v) && v > 0 ? v : fallback;
    }
}
