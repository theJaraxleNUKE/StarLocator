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
using Android.Views;
using Android.Widget;
using StarLocator.Core;

namespace StarLocator.Droid
{
    /// <summary>What survives a theme switch (Activity.Recreate) while the app is running.</summary>
    internal static class Session
    {
        public static PositionFix? LastFix;
        public static DateTime LastFixLocal;
        public static string LastReport = "";
        public static string LastSummary = "";
        public static Bitmap? LastImage;
        public static string CalibrationCoords = "";
        public static string? GpsFilledText;
        public static string GpsNote = "";
        public static List<string> LastWarnings = new List<string>();
        public static int LastFrames;
    }

    [Activity(Label = "Star Locator", MainLauncher = true, Exported = true,
              ScreenOrientation = ScreenOrientation.Portrait,
              Theme = "@android:style/Theme.Material.NoActionBar")]
    public partial class MainActivity : Activity
    {
        private const int CameraPermissionRequest = 42;
        private const int LocationPermissionRequest = 43;
        private const int ExportLastRequest = 44;
        private const int ExportKeptRequest = 45;
        /// <summary>A pose-specific calibration is trusted only within this many degrees of its pose.</summary>
        private const double PoseWarnDeg = 3.0;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private Ui _ui = null!;
        private Handler _handler = null!;
        private CameraCapture? _camera;
        private string _cameraNote = "";
        private GravitySampler _gravity = null!;
        private Task<CelestialLocator> _locatorTask = null!;
        private SensorCalibration? _calibration;
        private Android.Media.ToneGenerator? _tone;
        private CancellationTokenSource? _cts;
        private bool _busy, _gpsBusy;

        // Settings (persisted)
        private int _delayS = 10;
        private double _exposureS = 4.0;
        private int _iso = 1600;
        private bool _bracket = true, _keepFrames, _redMode;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            _handler = new Handler(Looper.MainLooper!);
            LoadSettings();
            _ui = new Ui(this, _redMode ? Palette.Red : Palette.Night);
            BuildScreens();

            _gravity = new GravitySampler(this);
            _handler.PostDelayed(UpdateLive, 500);
            // Building the star-pattern hash table takes a moment; do it once in the background.
            _locatorTask = Task.Run(() => new CelestialLocator());
            RefreshAll();
            if (savedInstanceState?.GetBoolean("advanced") == true) ShowAdvanced(false);
        }

        protected override void OnSaveInstanceState(Bundle outState)
        {
            base.OnSaveInstanceState(outState);
            outState.PutBoolean("advanced", _advancedVisible);
        }

        protected override void OnResume()
        {
            base.OnResume();
            _gravity.Start();
            if (CheckSelfPermission(Android.Manifest.Permission.Camera) != Permission.Granted)
                RequestPermissions(new[] { Android.Manifest.Permission.Camera }, CameraPermissionRequest);
            else OpenCamera();
        }

        protected override void OnPause()
        {
            _cts?.Cancel();
            _gravity.Stop();
            _camera?.Close();
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            _tone?.Release();
            _tone = null;
            base.OnDestroy();
        }

#pragma warning disable CS0618, CS0672, CA1422 // OnBackPressed is deprecated on API 33+ but still delivered.
        public override void OnBackPressed()
        {
            if (_advancedVisible) ShowMain();
            else base.OnBackPressed();
        }
#pragma warning restore CS0618, CS0672, CA1422

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode == CameraPermissionRequest)
            {
                if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) OpenCamera();
                else ShowMessage("Camera access needed", "Star Locator photographs the stars. Allow camera access in the phone's settings.", _ui.P.Error);
            }
            else if (requestCode == LocationPermissionRequest)
            {
                if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted) ReadGps();
                else SetGpsNote("Precise location was not allowed. Type the coordinates instead (e.g. copied from a map app).");
            }
        }

        // ---- Settings ---------------------------------------------------------------------------

        private void LoadSettings()
        {
            _delayS = int.TryParse(Storage.GetSetting(this, "delay", "10"), out int d) ? d : 10;
            _exposureS = ParseDouble(Storage.GetSetting(this, "exposure", "4"), 4.0);
            _iso = (int)ParseDouble(Storage.GetSetting(this, "iso", "1600"), 1600);
            _bracket = Storage.GetSetting(this, "bracket", "1") == "1";
            _keepFrames = Storage.GetSetting(this, "keep", "0") == "1";
            _redMode = Storage.GetSetting(this, "red", "0") == "1";
        }

        private void SaveSetting(string key, string value) => Storage.SetSetting(this, key, value);

        // ---- Camera -----------------------------------------------------------------------------

        private void OpenCamera()
        {
            try
            {
                if (_camera == null)
                {
                    _camera = new CameraCapture(this);
                    _camera.StatusChanged += msg => RunOnUiThread(() => { _cameraNote = msg; RefreshDeviceInfo(); });
                }
                _camera.Open(Storage.GetSetting(this, "camera", ""));
                _calibration = Storage.LoadCalibration(this, _camera.CameraId);
                RefreshAll();
                if (_calibration == null && Storage.HasObsoleteCalibration(this, _camera.CameraId) && Session.LastFix == null)
                    ShowMessage("Please calibrate again",
                        "This version turns the camera's image stabilisation off, which moves the image centre. " +
                        "The old calibration no longer applies.", _ui.P.Warn);
                if (_camera.Info != null && !_camera.Info.ManualSensor && Session.LastFix == null)
                    ShowMessage("Limited camera", "This camera has no manual exposure, so long star exposures may not work.", _ui.P.Warn);
            }
            catch (Exception ex)
            {
                ShowMessage("Camera unavailable", ex.Message, _ui.P.Error);
            }
        }

        /// <summary>Everything one capture produced.</summary>
        private sealed class CaptureOutcome
        {
            public StringBuilder Report { get; } = new StringBuilder();
            public List<PositionFix> Fixes { get; } = new List<PositionFix>();
            public List<Shot> Shots { get; } = new List<Shot>();
            public Vec3 MeanGravity { get; set; }
            public int Frames { get; set; }
            /// <summary>Largest disagreement (degrees) between the frames' zenith directions; see PositionSolver.ZenithSpread.</summary>
            public double StabilityMaxDeg { get; set; }
        }

        private void OnRingTapped()
        {
            if (_busy) { _cts?.Cancel(); return; }
            StartCapture(calibrate: false);
        }

        /// <summary>A normal fix (calibrate = false) or the one-time calibration (calibrate = true).</summary>
        private async void StartCapture(bool calibrate)
        {
            if (_busy || _gpsBusy) return;
            if (_camera == null || !_camera.IsReady || _camera.Info == null)
            {
                ShowMessage("Camera not ready", "Give it a second and try again.", _ui.P.Warn);
                return;
            }
            CameraInfo info = _camera.Info;

            double calLat = 0, calLon = 0;
            if (calibrate)
            {
                Session.CalibrationCoords = _calCoords.Text ?? "";
                if (!CoordinateParser.TryParse(Session.CalibrationCoords, out calLat, out calLon, out string parseError))
                {
                    SetGpsNote(parseError);
                    return;
                }
            }
            string method = calibrate && Session.GpsFilledText != null && Session.CalibrationCoords == Session.GpsFilledText
                ? "known location (GPS)" : "known location (typed)";

            ShowMain();
            _busy = true;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            RefreshButtons();
            try
            {
                // Calibration frames are solved WITHOUT the old calibration: we want the raw error.
                CaptureOutcome o = await CaptureAndSolveAsync(info, calibrate ? null : _calibration, calibrate, token);
                string title;
                if (calibrate)
                {
                    double temp = ReadTemperatureC();
                    SensorCalibration? cal = PositionSolver.CalibrateAtKnownLocation(o.Shots, calLat, calLon, method, temp, out string msg);
                    if (cal != null)
                    {
                        _calibration = cal;
                        Storage.SaveCalibration(this, _camera.CameraId, cal);
                        title = "Calibration saved";
                        ShowMessage(title, double.IsNaN(cal.ErrorBeforeKm)
                            ? "Future fixes will use it."
                            : $"Before calibration this capture was {cal.ErrorBeforeKm:F1} km off. Future fixes will use it.", _ui.P.Ok);
                    }
                    else
                    {
                        title = "Calibration failed";
                        ShowMessage(title, msg, _ui.P.Error);
                    }
                    o.Report.Append('\n').Append(msg).Append('\n');
                }
                else
                {
                    PositionFix? combined = PositionSolver.CombineFixes(o.Fixes, out string combineMsg);
                    if (combined != null)
                    {
                        var warnings = new List<string>();
                        if (_calibration != null)
                        {
                            double poseDiff = _calibration.PoseDifferenceDeg(o.MeanGravity);
                            if (_calibration.PoseSpecific && poseDiff > PoseWarnDeg)
                                warnings.Add($"Phone was {poseDiff:F1}° off the calibration pose");
                            double t = ReadTemperatureC();
                            if (!double.IsNaN(t) && !double.IsNaN(_calibration.TemperatureC) && Math.Abs(t - _calibration.TemperatureC) >= 10)
                                warnings.Add($"Phone is {t:F0} °C, calibrated at {_calibration.TemperatureC:F0} °C");
                        }
                        else
                        {
                            warnings.Add("Not calibrated: expect tens of km of error");
                        }
                        if (o.StabilityMaxDeg > PositionSolver.StabilityWarnDeg)
                            warnings.Add($"Camera image shifted {o.StabilityMaxDeg:F2}° inside the phone between frames: " +
                                         "this fix is unreliable (try another camera in Advanced)");
                        Session.LastFix = combined;
                        Session.LastFixLocal = DateTime.Now;
                        title = "Fix";
                        ShowFix(combined, o.Frames, warnings);
                        o.Report.Append('\n').Append(combineMsg).Append('\n');
                        o.Report.Append(string.Format(Inv, "Fix {0:F5}, {1:F5}  ±{2:F1} km (random {3:F1}, systematic {4:F1})\n",
                            combined.LatitudeDeg, combined.LongitudeDeg, combined.UncertaintyKm,
                            combined.RandomUncertaintyKm, combined.SystematicUncertaintyKm));
                        foreach (string w in warnings) o.Report.Append("⚠ ").Append(w).Append('\n');
                    }
                    else
                    {
                        title = "No fix";
                        ShowMessage("No fix", "Not enough stars were recognised. Try a darker or clearer patch of sky, or a longer exposure in Advanced.", _ui.P.Warn);
                        o.Report.Append('\n').Append(combineMsg).Append('\n');
                    }
                }
                string header = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} — {(calibrate ? "calibration" : "capture")}: {title}\n";
                Session.LastReport = header + o.Report;
                Session.LastSummary = $"Star Locator {AppVersion()} — {DateTime.UtcNow:o} UTC\n" +
                                      $"Camera {_camera.CameraId}: {info.Describe()}\n{Session.LastReport}\n" +
                                      $"Calibration: {(_calibration?.ToString() ?? "none")}\n";
                // Keep the report with the frames so a zip of the folder is self-explanatory.
                string summary = Session.LastSummary, stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", Inv);
                bool keepReport = _keepFrames;
                await Task.Run(() =>
                {
                    try
                    {
                        Storage.SaveReport(Storage.LastCaptureDir(this), "report.txt", summary);
                        if (keepReport) Storage.SaveReport(Storage.KeptFramesDir(this), $"report_{stamp}.txt", summary);
                    }
                    catch (System.IO.IOException) { }
                });
            }
            catch (System.OperationCanceledException)
            {
                ShowMessage("Cancelled", calibrate ? "The calibration was not changed." : "", _ui.P.TextDim);
            }
            catch (Exception ex)
            {
                ShowMessage("Capture failed", ex.Message, _ui.P.Error);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _busy = false;
                RefreshAll();
            }
        }

        /// <summary>Countdown, exposure bracket, then solve every frame.</summary>
        private async Task<CaptureOutcome> CaptureAndSolveAsync(CameraInfo info, SensorCalibration? cal, bool calibrating, CancellationToken token)
        {
            var o = new CaptureOutcome();
            long[] exposures = BuildExposures((long)(_exposureS * 1e9), _bracket, info);
            string mode = calibrating ? "Calibrating" : "Locating";

            // ---- Countdown so the phone can be placed camera-up (screen facing down) ----------
            for (int sLeft = _delayS; sLeft > 0; sLeft--)
            {
                SetRing(sLeft.ToString(Inv), "place the phone", (_delayS - sLeft) / (float)_delayS);
                SetRunning(mode, "Place the phone screen-down in the mount. Tap the circle to cancel.");
                Beep(Android.Media.Tone.PropBeep, 120);
                await Task.Delay(1000, token);
            }
            if (_delayS > 0) Beep(Android.Media.Tone.PropBeep2, 400);   // "go" signal

            // Warm-up frame: lets focus and exposure settle; its image is discarded.
            SetRing("•", "hold still", 0);
            SetRunning(mode, "Don't touch the phone.");
            await Task.Delay(400, token);
            await _camera!.CaptureAsync(Math.Clamp(100_000_000L, info.MinExposureNs, info.MaxExposureNs), _iso);

            // ---- Capture every frame first (keeps the time the phone must stay still short) ---
            var frames = new List<(CapturedFrame Frame, GravityMeasurement? G)>();
            for (int i = 0; i < exposures.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                SetRing($"{i + 1}/{exposures.Length}", $"{exposures[i] / 1e9:0.##} s exposure", i / (float)exposures.Length);
                CapturedFrame frame = await _camera.CaptureAsync(exposures[i], _iso);
                await Task.Delay(100);           // let accelerometer samples cover the full window
                frames.Add((frame, _gravity.Average(frame.StartRealtimeNs, frame.EndRealtimeNs)));
            }
            Beep(Android.Media.Tone.PropAck, 600);
            SetRunning(mode, "Done capturing — you can pick up the phone.");

            // ---- Keep the frames: always the latest capture (for export), optionally permanently ---
            string lastDir = Storage.LastCaptureDir(this);
            string keptDir = Storage.KeptFramesDir(this);
            bool keep = _keepFrames;
            SetRing("…", "saving frames", 0);
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

            // ---- Solve each frame independently -----------------------------------------------
            CelestialLocator locator = await _locatorTask;
            string camId = _camera.CameraId;
            double fscale = Storage.LoadFocalScale(this, camId);
            var learned = new List<double>();
            Vec3 gSum = Vec3.Zero;
            (CapturedFrame Frame, FrameResult Result)? best = null;
            o.Frames = frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                SetRing("…", "identifying stars", i / (float)frames.Count);
                (CapturedFrame frame, GravityMeasurement? g) = frames[i];
                string label = $"Frame {i + 1} ({frame.ExposureNs / 1e9:0.##} s, ISO {frame.Iso}): ";
                if (g == null) { o.Report.Append(label).Append("no accelerometer data\n"); continue; }
                gSum += g.Mean;
                CameraModel cam = frame.Camera.Clone();
                cam.Fx *= fscale;
                cam.Fy *= fscale;
                FrameResult r = await Task.Run(() =>
                    locator.ProcessFrame(frame.Luma, frame.Width, frame.Height, frame.RowStride, cam, g, frame.UtcMidExposure, cal));
                o.Report.Append(label).Append(r.Detections.Count).Append(" stars detected, ");
                if (r.Fix != null && r.Shot != null && r.Identification != null)
                {
                    IdentificationResult id = r.Identification;
                    o.Report.Append($"{id.Matches.Count} identified, fit {id.RmsDeg:F3}°");
                    if (g.StdDev > 0.06) o.Report.Append($", ⚠ moved (σ {g.StdDev:F2} m/s²)");
                    if (frame.Diagnostics.TryGetValue("oisMode", out string? ois)) o.Report.Append(ois == "0" ? ", OIS off" : ", ⚠ OIS ON");
                    if (frame.Diagnostics.TryGetValue("oisShiftPx", out string? shift)) o.Report.Append($", OIS shift {shift} px");
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

            // ---- Was the image steady relative to the phone? ---------------------------------
            if (o.Shots.Count >= 2)
            {
                var (zRms, zMax) = PositionSolver.ZenithSpread(o.Shots);
                o.StabilityMaxDeg = zMax;
                o.Report.Append(string.Format(Inv, "Image stability: frames agree within {0:F3}° (RMS {1:F3}°){2}\n", zMax, zRms,
                    zMax > PositionSolver.StabilityWarnDeg ? " ⚠ the image moved inside the phone" : ""));
            }

            // ---- Remember the lens focal-length correction the stars revealed ---------------
            if (learned.Count > 0)
            {
                learned.Sort();
                double median = learned[learned.Count / 2];
                if (Math.Abs(median / fscale - 1) > 0.002)
                {
                    Storage.SaveFocalScale(this, camId, median);
                    o.Report.Append($"Lens correction learned: focal length ×{median:F4} (saved for this camera)\n");
                }
            }
            if (keep) o.Report.Append("Frames kept on the phone (Advanced › Files)\n");

            if (best != null)
            {
                var b = best.Value;
                Bitmap bmp = await Task.Run(() => ResultRenderer.Render(b.Frame, b.Result, 1440));
                Session.LastImage = bmp;   // the previous bitmap may still be on screen: let the GC free it
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
                // Some devices refuse a ToneGenerator; the countdown still works silently.
            }
        }

        // ---- GPS for the one-time calibration ---------------------------------------------------

        private void OnUseGps()
        {
            if (_gpsBusy) { _cts?.Cancel(); return; }
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
            if (_busy || _gpsBusy) return;
            _gpsBusy = true;
            _cts = new CancellationTokenSource();
            RefreshButtons();
            try
            {
                SetGpsNote("Waiting for GPS…");
                GpsFix fix = await GpsReader.GetFixAsync(this, 20, TimeSpan.FromSeconds(120),
                                                         msg => RunOnUiThread(() => SetGpsNote(msg)), _cts.Token);
                string text = string.Format(Inv, "{0:F6}, {1:F6}", fix.Latitude, fix.Longitude);
                _calCoords.Text = text;
                Session.CalibrationCoords = text;
                Session.GpsFilledText = text;
                SetGpsNote($"GPS ±{fix.AccuracyM:F0} m. Phone clock is {Math.Abs(fix.ClockOffsetS):F1} s " +
                           $"{(fix.ClockOffsetS > 0 ? "behind" : "ahead of")} GPS time (≈{Math.Abs(fix.ClockOffsetS) * 0.46:F1} km).");
            }
            catch (System.OperationCanceledException)
            {
                SetGpsNote("GPS cancelled.");
            }
            catch (Exception ex)
            {
                SetGpsNote(ex.Message);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _gpsBusy = false;
                RefreshButtons();
            }
        }

        private void SetGpsNote(string text)
        {
            Session.GpsNote = text;
            if (_gpsNote != null) { _gpsNote.Text = text; _gpsNote.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible; }
        }

        // ---- Files ------------------------------------------------------------------------------

        private void StartExport(int requestCode)
        {
            string dir = requestCode == ExportLastRequest ? Storage.LastCaptureDir(this) : Storage.KeptFramesDir(this);
            if (Storage.FolderSize(dir).Files == 0)
            {
                Toast.MakeText(this, "Nothing to export yet.", ToastLength.Short)?.Show();
                return;
            }
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("application/zip");
            string what = requestCode == ExportLastRequest ? "last" : "kept";
            intent.PutExtra(Intent.ExtraTitle, $"starlocator_{what}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");
            try { StartActivityForResult(intent, requestCode); }
            catch (ActivityNotFoundException) { Toast.MakeText(this, "No file picker available.", ToastLength.Long)?.Show(); }
        }

        protected override async void OnActivityResult(int requestCode, [Android.Runtime.GeneratedEnum] Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if ((requestCode != ExportLastRequest && requestCode != ExportKeptRequest) || resultCode != Result.Ok || data?.Data == null) return;
            Android.Net.Uri uri = data.Data;
            string dir = requestCode == ExportLastRequest ? Storage.LastCaptureDir(this) : Storage.KeptFramesDir(this);
            string summary = Session.LastSummary;
            try
            {
                int n = await Task.Run(() =>
                {
                    System.IO.Stream? s = ContentResolver?.OpenOutputStream(uri);
                    if (s == null) throw new InvalidOperationException("Could not open the destination.");
                    return Storage.WriteZip(s, dir, summary);
                });
                Toast.MakeText(this, $"Saved {n} files as a zip.", ToastLength.Short)?.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, "Export failed: " + ex.Message, ToastLength.Long)?.Show();
            }
        }

        private void DeleteKeptFrames()
        {
            Storage.ClearDir(Storage.KeptFramesDir(this));
            RefreshFiles();
            Toast.MakeText(this, "Kept frames deleted.", ToastLength.Short)?.Show();
        }

        // ---- Misc -------------------------------------------------------------------------------

        /// <summary>Battery temperature in °C (no permission needed), or NaN.</summary>
        private double ReadTemperatureC()
        {
            Intent? battery = RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            int t = battery?.GetIntExtra(BatteryManager.ExtraTemperature, int.MinValue) ?? int.MinValue;
            return t == int.MinValue ? double.NaN : t / 10.0;
        }

        private void OpenMap()
        {
            PositionFix? f = Session.LastFix;
            if (f == null) return;
            string lat = f.LatitudeDeg.ToString("F5", Inv), lon = f.LongitudeDeg.ToString("F5", Inv);
            try { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse($"geo:{lat},{lon}?q={lat},{lon}(Star fix)"))); }
            catch (ActivityNotFoundException) { Toast.MakeText(this, "No map app installed.", ToastLength.Short)?.Show(); }
        }

        private void CopyFix()
        {
            PositionFix? f = Session.LastFix;
            if (f == null) return;
            string text = string.Format(Inv, "{0:F5}, {1:F5}", f.LatitudeDeg, f.LongitudeDeg);
            var cm = GetSystemService(ClipboardService) as Android.Content.ClipboardManager;
            if (cm != null) cm.PrimaryClip = ClipData.NewPlainText("Star fix", text);
            Toast.MakeText(this, "Coordinates copied", ToastLength.Short)?.Show();
        }

        private string AppVersion()
        {
            try
            {
#pragma warning disable CS0618, CA1422
                return PackageManager?.GetPackageInfo(PackageName ?? "", 0)?.VersionName ?? "?";
#pragma warning restore CS0618, CA1422
            }
            catch (Exception) { return "?"; }
        }

        private void SelectCamera(string id)
        {
            if (_busy || _camera?.CameraId == id) return;
            SaveSetting("camera", id);
            _camera?.Close();
            OpenCamera();
        }

        private static double ParseDouble(string? s, double fallback) =>
            double.TryParse(s?.Replace(',', '.'), NumberStyles.Float, Inv, out double v) && v > 0 ? v : fallback;
    }
}
