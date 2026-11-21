using System;
using System.Collections.Generic;
using System.Text;
using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using StarLocator.Core;

namespace StarLocator.Droid
{
    /// <summary>Screen layout: a minimal main screen and an "Advanced" screen, swapped in place.</summary>
    public partial class MainActivity
    {
        private bool _advancedVisible;
        private View _mainScreen = null!;
        private ScrollView _advancedScreen = null!;

        // Main screen
        private TextView _resultTitle = null!, _resultCoords = null!, _resultMeta = null!, _resultWarn = null!;
        private TextView _accuracyChip = null!;
        private LinearLayout _resultActions = null!;
        private LinearLayout _banner = null!;
        private TextView _bannerTitle = null!, _bannerBody = null!;
        private RingButton _ring = null!;
        private TextView _hint = null!, _calChip = null!;

        // Advanced screen
        private TextView _lastEmpty = null!, _lastReport = null!;
        private ImageView _lastImage = null!;
        private TextView _calStatus = null!, _gpsNote = null!, _filesInfo = null!, _deviceInfo = null!, _liveInfo = null!;
        private EditText _calCoords = null!, _exposureField = null!, _isoField = null!;
        private Button _gpsBtn = null!, _calibrateBtn = null!, _forgetBtn = null!;
        private Button _exportLastBtn = null!, _exportKeptBtn = null!, _deleteKeptBtn = null!, _resetLensBtn = null!;
        private Switch _bracketSwitch = null!, _keepSwitch = null!, _redSwitch = null!;
        private TextView _cameraDesc = null!;
        private View _calibrationSection = null!;
        private Java.Lang.Object? _backCallback;

        /// <summary>Back gesture closes the Advanced screen (Android 13+ API; OnBackPressed covers older).</summary>
        private sealed class BackCallback : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
        {
            private readonly Action _onBack;
            public BackCallback(Action onBack) { _onBack = onBack; }
            public void OnBackInvoked() => _onBack();
        }

        private void SetBackHandler(bool enabled)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
            Android.Window.IOnBackInvokedDispatcher dispatcher = OnBackInvokedDispatcher;
            if (enabled && _backCallback == null)
            {
                var cb = new BackCallback(ShowMain);
                dispatcher.RegisterOnBackInvokedCallback(0, cb);   // 0 = PRIORITY_DEFAULT
                _backCallback = cb;
            }
            else if (!enabled && _backCallback is BackCallback old)
            {
                dispatcher.UnregisterOnBackInvokedCallback(old);
                _backCallback = null;
            }
        }

        private void BuildScreens()
        {
            Palette p = _ui.P;
            var root = new FrameLayout(this);
            root.SetBackgroundColor(p.Bg);
            _mainScreen = BuildMain();
            _advancedScreen = BuildAdvanced();
            _advancedScreen.Visibility = ViewStates.Gone;
            root.AddView(_mainScreen, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            root.AddView(_advancedScreen, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            root.SetOnApplyWindowInsetsListener(new InsetsListener());
            SetContentView(root);
#pragma warning disable CS0618, CA1422 // Ignored (bars are transparent) on Android 15+, still used below it.
            Window?.SetStatusBarColor(p.Bg);
            Window?.SetNavigationBarColor(p.Bg);
#pragma warning restore CS0618, CA1422
        }

        /// <summary>Keeps content clear of the status and navigation bars (Android 15+ draws edge to edge).</summary>
        private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
        {
            public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
            {
                int l, t, r, b;
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    Android.Graphics.Insets i = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                    (l, t, r, b) = (i.Left, i.Top, i.Right, i.Bottom);
                }
                else
                {
#pragma warning disable CS0618, CA1422
                    (l, t, r, b) = (insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop,
                                    insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CS0618, CA1422
                }
                v.SetPadding(l, t, r, b);
                return insets;
            }
        }

        // ---- Main screen --------------------------------------------------------------------------

        private View BuildMain()
        {
            Palette p = _ui.P;
            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(_ui.Dp(20), _ui.Dp(12), _ui.Dp(20), _ui.Dp(20));

            // Top bar
            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            TextView title = _ui.Text("Star Locator", 22, p.Text, bold: true);
            top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            top.AddView(_ui.Button("Advanced", Ui.ButtonKind.Tonal, (s, e) => ShowAdvanced(false)));
            col.AddView(top);

            // Result card
            LinearLayout card = _ui.Card();
            _resultTitle = _ui.Text("Ready", 26, p.Text, bold: true);
            card.AddView(_resultTitle);
            _resultCoords = _ui.Text("", 15, p.TextDim);
            card.AddView(_resultCoords, Ui.Gap(_ui.Dp(4)));
            var chipRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            chipRow.SetGravity(GravityFlags.CenterVertical);
            _accuracyChip = _ui.Chip("", p.Primary);
            chipRow.AddView(_accuracyChip);
            _resultMeta = _ui.Text("", 13, p.TextDim);
            _resultMeta.SetPadding(_ui.Dp(10), 0, 0, 0);
            chipRow.AddView(_resultMeta);
            card.AddView(chipRow, Ui.Gap(_ui.Dp(12)));
            _resultWarn = _ui.Text("", 14, p.Warn);
            card.AddView(_resultWarn, Ui.Gap(_ui.Dp(10)));
            _resultActions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _resultActions.AddView(_ui.Button("Open map", Ui.ButtonKind.Tonal, (s, e) => OpenMap()));
            var copyLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            copyLp.LeftMargin = _ui.Dp(8);
            _resultActions.AddView(_ui.Button("Copy", Ui.ButtonKind.Text, (s, e) => CopyFix()), copyLp);
            card.AddView(_resultActions, Ui.Gap(_ui.Dp(14)));
            col.AddView(card, Ui.Gap(_ui.Dp(16)));

            // Message banner (cancelled, failed, calibration saved…)
            _banner = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _banner.Background = Ui.Rounded(p.Surface2, _ui.Dp(16));
            _banner.SetPadding(_ui.Dp(16), _ui.Dp(12), _ui.Dp(16), _ui.Dp(12));
            _bannerTitle = _ui.Text("", 15, p.Text, bold: true);
            _bannerBody = _ui.Text("", 14, p.TextDim);
            _banner.AddView(_bannerTitle);
            _banner.AddView(_bannerBody);
            _banner.Visibility = ViewStates.Gone;
            _banner.Clickable = true;
            _banner.Click += (s, e) => _banner.Visibility = ViewStates.Gone;
            col.AddView(_banner, Ui.Gap(_ui.Dp(12)));

            col.AddView(new View(this), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            // The big button
            float density = Resources?.DisplayMetrics?.Density ?? 2f;
            int widthDp = (int)((Resources?.DisplayMetrics?.WidthPixels ?? 1080) / density);
            int ringPx = _ui.Dp(Math.Clamp(widthDp - 80, 220, 320));
            _ring = new RingButton(this);
            _ring.SetColors(p.DiscInner, p.DiscOuter, p.Accent, p.Primary, p.Glow, p.Dots, p.Text, p.TextDim);
            _ring.Click += (s, e) => OnRingTapped();
            var ringLp = new LinearLayout.LayoutParams(ringPx, ringPx) { Gravity = GravityFlags.CenterHorizontal };
            col.AddView(_ring, ringLp);

            _hint = _ui.Text("", 15, p.TextDim);
            _hint.Gravity = GravityFlags.Center;
            col.AddView(_hint, Ui.Gap(_ui.Dp(18)));

            col.AddView(new View(this), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            _calChip = _ui.Chip("", p.TextDim);
            _calChip.Clickable = true;
            _calChip.Click += (s, e) => ShowAdvanced(true);
            col.AddView(_calChip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                                  { Gravity = GravityFlags.CenterHorizontal });
            return col;
        }

        // ---- Advanced screen ----------------------------------------------------------------------

        private ScrollView BuildAdvanced()
        {
            Palette p = _ui.P;
            var scroll = new ScrollView(this);
            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.SetPadding(_ui.Dp(20), _ui.Dp(12), _ui.Dp(20), _ui.Dp(32));
            scroll.AddView(col);

            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            top.AddView(_ui.Button("‹  Back", Ui.ButtonKind.Text, (s, e) => ShowMain()));
            top.AddView(_ui.Text("Advanced", 22, p.Text, bold: true));
            col.AddView(top);

            // Last capture
            LinearLayout last = Section(col, "Last capture");
            _lastEmpty = _ui.Text("No capture yet.", 15, p.TextDim);
            last.AddView(_lastEmpty);
            _lastImage = new ImageView(this);
            _lastImage.SetAdjustViewBounds(true);
            _lastImage.SetScaleType(ImageView.ScaleType.FitCenter!);
            _lastImage.ClipToOutline = true;
            _lastImage.Background = Ui.Rounded(p.Bg, _ui.Dp(12));
            last.AddView(_lastImage, Ui.Gap(0));
            _lastReport = _ui.Text("", 12, p.TextDim);
            _lastReport.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
            _lastReport.SetTextIsSelectable(true);
            last.AddView(_lastReport, Ui.Gap(_ui.Dp(12)));

            // Capture settings
            LinearLayout cap = Section(col, "Capture");
            List<BackCamera> cams;
            try { cams = CameraCapture.ListBackCameras(this); }
            catch (Exception) { cams = new List<BackCamera>(); }
            if (cams.Count > 5) cams = cams.GetRange(0, 5);
            if (cams.Count > 1)
            {
                var labels = new string[cams.Count];
                for (int i = 0; i < cams.Count; i++)
                {
                    labels[i] = cams[i].Label;
                    for (int j = 0; j < cams.Count; j++)
                        if (j != i && cams[j].Label == cams[i].Label) { labels[i] = cams[i].Label + " #" + cams[i].Id; break; }
                }
                string current = _camera?.CameraId ?? Storage.GetSetting(this, "camera", cams[0].Id);
                int sel = Math.Max(0, cams.FindIndex(c => c.Id == current));
                cap.AddView(_ui.Labeled("Camera", _ui.Segmented(labels, sel, i => SelectCamera(cams[i].Id))));
            }
            _cameraDesc = _ui.Text("", 13, p.TextDim);
            cap.AddView(_cameraDesc, Ui.Gap(_ui.Dp(8)));
            var spacer = new View(this);
            cap.AddView(spacer, new LinearLayout.LayoutParams(1, _ui.Dp(8)));
            cap.AddView(_ui.Labeled("Countdown", _ui.Segmented(new[] { "Off", "5 s", "10 s" },
                _delayS == 0 ? 0 : _delayS == 5 ? 1 : 2,
                i => { _delayS = i == 0 ? 0 : i == 1 ? 5 : 10; SaveSetting("delay", _delayS.ToString(Inv)); })));
            var fields = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _exposureField = _ui.Field(_exposureS.ToString("0.##", Inv), "4", numeric: true);
            _exposureField.FocusChange += (s, e) => { if (!e.HasFocus) CommitCaptureFields(); };
            _isoField = _ui.Field(_iso.ToString(Inv), "1600", numeric: true);
            _isoField.FocusChange += (s, e) => { if (!e.HasFocus) CommitCaptureFields(); };
            fields.AddView(_ui.Labeled("Exposure (s)", _exposureField), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var isoLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = _ui.Dp(12) };
            fields.AddView(_ui.Labeled("ISO", _isoField), isoLp);
            cap.AddView(fields, Ui.Gap(_ui.Dp(16)));
            _bracketSwitch = _ui.Switch("Exposure bracket (¼×, ½×, 1×, 2×)", _bracket);
            _bracketSwitch.CheckedChange += (s, e) => { _bracket = e.IsChecked; SaveSetting("bracket", _bracket ? "1" : "0"); };
            cap.AddView(_bracketSwitch, Ui.Gap(_ui.Dp(8)));

            // Calibration
            LinearLayout cal = Section(col, "Calibration");
            _calibrationSection = (View)cal.Parent!;
            _calStatus = _ui.Text("", 15, p.Text);
            cal.AddView(_calStatus);
            _calCoords = _ui.Field(Session.CalibrationCoords, "42.6526, -73.7562  or  42°39'09\"N 73°45'22\"W", numeric: false);
            cal.AddView(_ui.Labeled("Where the phone is now", _calCoords), Ui.Gap(_ui.Dp(14)));
            var calRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _gpsBtn = _ui.Button("Use GPS", Ui.ButtonKind.Tonal, (s, e) => OnUseGps());
            calRow.AddView(_gpsBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            _calibrateBtn = _ui.Button("Calibrate", Ui.ButtonKind.Filled, (s, e) => StartCapture(calibrate: true));
            calRow.AddView(_calibrateBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = _ui.Dp(12) });
            cal.AddView(calRow, Ui.Gap(_ui.Dp(12)));
            _gpsNote = _ui.Text(Session.GpsNote, 13, p.TextDim);
            _gpsNote.Visibility = string.IsNullOrEmpty(Session.GpsNote) ? ViewStates.Gone : ViewStates.Visible;
            cal.AddView(_gpsNote, Ui.Gap(_ui.Dp(10)));
            cal.AddView(_ui.Text("Put the phone in the mount, then tap Calibrate. It runs a normal capture and compares it with this location. Needed once.", 13, p.TextDim), Ui.Gap(_ui.Dp(10)));
            _forgetBtn = _ui.Button("Forget calibration", Ui.ButtonKind.Text, (s, e) => Confirm("Forget calibration?",
                "Fixes will be uncalibrated until you calibrate again.", "Forget", () =>
                {
                    if (_camera == null) return;
                    Storage.ClearCalibration(this, _camera.CameraId);
                    _calibration = null;
                    RefreshAll();
                }));
            cal.AddView(_forgetBtn, Ui.Gap(_ui.Dp(4), ViewGroup.LayoutParams.WrapContent));

            // Files
            LinearLayout files = Section(col, "Files");
            _filesInfo = _ui.Text("", 14, p.TextDim);
            files.AddView(_filesInfo);
            _keepSwitch = _ui.Switch("Keep every captured frame", _keepFrames);
            _keepSwitch.CheckedChange += (s, e) => { _keepFrames = e.IsChecked; SaveSetting("keep", _keepFrames ? "1" : "0"); };
            files.AddView(_keepSwitch, Ui.Gap(_ui.Dp(8)));
            _exportLastBtn = _ui.Button("Save last capture (.zip)", Ui.ButtonKind.Tonal, (s, e) => StartExport(ExportLastRequest));
            files.AddView(_exportLastBtn, Ui.Gap(_ui.Dp(10)));
            _exportKeptBtn = _ui.Button("Save kept frames (.zip)", Ui.ButtonKind.Tonal, (s, e) => StartExport(ExportKeptRequest));
            files.AddView(_exportKeptBtn, Ui.Gap(_ui.Dp(10)));
            _deleteKeptBtn = _ui.Button("Delete kept frames", Ui.ButtonKind.Text, (s, e) => Confirm("Delete kept frames?",
                "They are removed from the phone. Save them first if you need them.", "Delete", DeleteKeptFrames));
            files.AddView(_deleteKeptBtn, Ui.Gap(_ui.Dp(4), ViewGroup.LayoutParams.WrapContent));

            // Display
            LinearLayout disp = Section(col, "Display");
            _redSwitch = _ui.Switch("Red night-vision colours", _redMode);
            _redSwitch.CheckedChange += (s, e) =>
            {
                if (e.IsChecked == _redMode) return;
                _redMode = e.IsChecked;
                SaveSetting("red", _redMode ? "1" : "0");
                CommitCaptureFields();
                Session.CalibrationCoords = _calCoords.Text ?? "";
                Recreate();
            };
            disp.AddView(_redSwitch);

            // Device
            LinearLayout dev = Section(col, "Device");
            _liveInfo = _ui.Text("", 14, p.Text);
            dev.AddView(_liveInfo);
            _deviceInfo = _ui.Text("", 13, p.TextDim);
            _deviceInfo.SetTextIsSelectable(true);
            dev.AddView(_deviceInfo, Ui.Gap(_ui.Dp(10)));
            _resetLensBtn = _ui.Button("Reset lens correction", Ui.ButtonKind.Text, (s, e) => Confirm("Reset lens correction?",
                "The focal-length correction learned from the stars is discarded and relearned on the next capture.", "Reset", () =>
                {
                    if (_camera == null) return;
                    Storage.SaveFocalScale(this, _camera.CameraId, 1.0);
                    RefreshDeviceInfo();
                }));
            dev.AddView(_resetLensBtn, Ui.Gap(_ui.Dp(4), ViewGroup.LayoutParams.WrapContent));
            return scroll;
        }

        /// <summary>Adds a heading + card to <paramref name="parent"/> and returns the card.</summary>
        private LinearLayout Section(LinearLayout parent, string heading)
        {
            var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
            wrap.AddView(_ui.Heading(heading));
            LinearLayout card = _ui.Card();
            wrap.AddView(card);
            parent.AddView(wrap, Ui.Gap(_ui.Dp(24)));
            return card;
        }

        private void CommitCaptureFields()
        {
            _exposureS = Math.Clamp(ParseDouble(_exposureField.Text, _exposureS), 0.001, 60);
            _iso = (int)Math.Clamp(ParseDouble(_isoField.Text, _iso), 25, 25600);
            SaveSetting("exposure", _exposureS.ToString("R", Inv));
            SaveSetting("iso", _iso.ToString(Inv));
        }

        private void ShowMain()
        {
            if (_advancedVisible)
            {
                CommitCaptureFields();
                Session.CalibrationCoords = _calCoords.Text ?? "";
                var imm = GetSystemService(InputMethodService) as Android.Views.InputMethods.InputMethodManager;
                imm?.HideSoftInputFromWindow(_advancedScreen.WindowToken, 0);
            }
            _advancedVisible = false;
            SetBackHandler(false);
            _advancedScreen.Visibility = ViewStates.Gone;
            _mainScreen.Visibility = ViewStates.Visible;
        }

        private void ShowAdvanced(bool scrollToCalibration)
        {
            if (_busy) return;
            _advancedVisible = true;
            SetBackHandler(true);
            RefreshAll();
            _mainScreen.Visibility = ViewStates.Gone;
            _advancedScreen.Visibility = ViewStates.Visible;
            if (scrollToCalibration) _advancedScreen.Post(() => _advancedScreen.SmoothScrollTo(0, _calibrationSection.Top));
            else _advancedScreen.ScrollTo(0, 0);
        }

        private void Confirm(string title, string message, string action, Action onYes)
        {
            new AlertDialog.Builder(this)
                .SetTitle(title)!
                .SetMessage(message)!
                .SetPositiveButton(action, (s, e) => onYes())!
                .SetNegativeButton("Cancel", (s, e) => { })!
                .Show();
        }

        // ---- State → screen ------------------------------------------------------------------------

        private void SetRing(string title, string subtitle, float progress) =>
            RunOnUiThread(() => _ring.Show(title, subtitle, progress));

        private void SetRunning(string mode, string hint) =>
            RunOnUiThread(() =>
            {
                _hint.Text = hint;
                _banner.Visibility = ViewStates.Gone;
                if (Session.LastFix == null) _resultTitle.Text = mode + "…";
            });

        private void ShowMessage(string title, string body, Color color) =>
            RunOnUiThread(() =>
            {
                _bannerTitle.Text = title;
                _bannerTitle.SetTextColor(color);
                _bannerBody.Text = body;
                _bannerBody.Visibility = string.IsNullOrEmpty(body) ? ViewStates.Gone : ViewStates.Visible;
                _banner.Visibility = ViewStates.Visible;
            });

        private void ShowFix(PositionFix fix, int frames, List<string> warnings)
        {
            Session.LastWarnings = warnings;
            Session.LastFrames = frames;
            RenderResult();
        }

        private void RenderResult()
        {
            PositionFix? f = Session.LastFix;
            bool has = f != null;
            _resultCoords.Visibility = _accuracyChip.Visibility = _resultMeta.Visibility = _resultActions.Visibility =
                has ? ViewStates.Visible : ViewStates.Gone;
            if (f == null)
            {
                _resultTitle.Text = _busy ? _resultTitle.Text : "Ready";
                _resultWarn.Text = "";
                _resultWarn.Visibility = ViewStates.Gone;
                return;
            }
            _resultTitle.Text = Astronomy.ToDms(f.LatitudeDeg, true) + "\n" + Astronomy.ToDms(f.LongitudeDeg, false);
            _resultCoords.Text = string.Format(Inv, "{0:F5}, {1:F5}", f.LatitudeDeg, f.LongitudeDeg);
            _accuracyChip.Text = f.UncertaintyKm < 10 ? $"± {f.UncertaintyKm.ToString("F1", Inv)} km" : $"± {f.UncertaintyKm.ToString("F0", Inv)} km";
            string frames = Session.LastFrames > 0 ? $"{f.FramesUsed} of {Session.LastFrames} frames" : $"{f.FramesUsed} frames";
            _resultMeta.Text = $"{frames} · {Session.LastFixLocal:HH:mm}";
            _resultWarn.Text = string.Join("\n", Session.LastWarnings);
            _resultWarn.Visibility = Session.LastWarnings.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
        }

        private void RefreshAll()
        {
            RenderResult();
            RefreshCalibration();
            RefreshButtons();
            RefreshLastCapture();
            RefreshFiles();
            RefreshDeviceInfo();
        }

        private void RefreshButtons()
        {
            Palette p = _ui.P;
            if (!_busy)
            {
                _ring.Show("Locate", "tap to start", 0, active: false);
                _hint.Text = _delayS > 0
                    ? $"Tap, then place the phone screen-down in the mount within {_delayS} s."
                    : "Place the phone in the mount, then tap.";
            }
            _ring.Enabled = !_gpsBusy;
            _ring.Alpha = _gpsBusy ? 0.5f : 1f;
            _gpsBtn.Text = _gpsBusy ? "Stop GPS" : "Use GPS";
            _gpsBtn.Enabled = !_busy;
            _calibrateBtn.Enabled = !_busy && !_gpsBusy;
            _calibrateBtn.Alpha = _calibrateBtn.Enabled ? 1f : 0.5f;
            _forgetBtn.Visibility = _calibration != null ? ViewStates.Visible : ViewStates.Gone;
            _exportLastBtn.Enabled = _exportKeptBtn.Enabled = _deleteKeptBtn.Enabled = !_busy;
            _redSwitch.Enabled = !_busy && !_gpsBusy;
            _calChip.Visibility = _busy ? ViewStates.Invisible : ViewStates.Visible;
            _calChip.SetTextColor(_calibration != null ? p.Ok : p.Warn);
        }

        private void RefreshCalibration()
        {
            if (_calibration == null)
            {
                _calChip.Text = "Not calibrated  ›";
                _calStatus.Text = "Not calibrated. Fixes can be tens of km off until you calibrate once at a known location.";
                return;
            }
            SensorCalibration c = _calibration;
            _calChip.Text = "Calibrated  ✓";
            var sb = new StringBuilder();
            sb.Append($"Calibrated {c.CreatedUtc.ToLocalTime():d MMM yyyy} · {c.Method}");
            sb.Append(c.PoseSpecific ? "\nValid in the mount it was made in." : "\nValid in any pose.");
            if (!double.IsNaN(c.ErrorBeforeKm)) sb.Append($"\nError it corrected: {c.ErrorBeforeKm.ToString("F1", Inv)} km");
            if (!double.IsNaN(c.TemperatureC)) sb.Append($" · phone at {c.TemperatureC.ToString("F0", Inv)} °C");
            if (!double.IsNaN(c.SpreadDeg))
                sb.Append($"\nFrames agreed within {c.SpreadDeg.ToString("F3", Inv)}° ({c.Shots} of {Math.Max(c.ShotsOffered, c.Shots)} used)");
            _calStatus.Text = sb.ToString();
        }

        private void RefreshLastCapture()
        {
            bool has = !string.IsNullOrEmpty(Session.LastReport);
            _lastEmpty.Visibility = has ? ViewStates.Gone : ViewStates.Visible;
            _lastReport.Visibility = has ? ViewStates.Visible : ViewStates.Gone;
            _lastReport.Text = Session.LastReport.TrimEnd();
            Bitmap? img = Session.LastImage;
            _lastImage.SetImageBitmap(img);
            _lastImage.Visibility = img != null ? ViewStates.Visible : ViewStates.Gone;
        }

        private void RefreshFiles()
        {
            var (lastN, lastMb) = Storage.FolderSize(Storage.LastCaptureDir(this));
            var (keptN, keptMb) = Storage.FolderSize(Storage.KeptFramesDir(this));
            _filesInfo.Text = $"Last capture: {Files(lastN, lastMb)}\nKept frames: {Files(keptN, keptMb)}";
            _exportKeptBtn.Visibility = _deleteKeptBtn.Visibility = keptN > 0 ? ViewStates.Visible : ViewStates.Gone;
        }

        private static string Files(int n, double mb) =>
            n == 0 ? "none" : $"{n} file{(n == 1 ? "" : "s")}, {mb.ToString("F0", Inv)} MB";

        private void RefreshDeviceInfo()
        {
            var sb = new StringBuilder();
            CameraInfo? info = _camera?.Info;
            if (_camera != null && info != null)
            {
                sb.Append($"Back camera {_camera.CameraId}");
                if (_camera.CaptureSize != null) sb.Append($" · {_camera.CaptureSize.Width}×{_camera.CaptureSize.Height}");
                sb.Append($"\nExposure {info.MinExposureNs / 1e9:0.####}–{info.MaxExposureNs / 1e9:0.##} s · ISO {info.MinIso}–{info.MaxIso}");
                sb.Append($"\nManual sensor: {(info.ManualSensor ? "yes" : "no")} · timestamps: {(info.RealtimeTimestamps ? "realtime" : "unknown base")}");
                sb.Append($"\nOptical stabilisation: {(info.HasOis ? "present, switched off for captures" : "none")}" +
                          $" · OIS shift data: {(info.OisDataAvailable ? "yes" : "no")}");
                if (info.LogicalMultiCamera) sb.Append($"\nMulti-camera: lenses {string.Join(", ", info.PhysicalCameraIds)}");
                double fscale = Storage.LoadFocalScale(this, _camera.CameraId);
                sb.Append($"\nLens correction: focal length ×{fscale.ToString("F4", Inv)}");
                _resetLensBtn.Visibility = Math.Abs(fscale - 1) > 1e-9 ? ViewStates.Visible : ViewStates.Gone;
                _cameraDesc.Text = $"Camera {_camera.CameraId}: {info.Describe()}";
            }
            else
            {
                sb.Append("Camera not open.");
                _resetLensBtn.Visibility = ViewStates.Gone;
                _cameraDesc.Text = "";
            }
            sb.Append($"\nAccelerometer: {(_gravity?.Available == true ? "yes" : "missing")}");
            if (!string.IsNullOrEmpty(_cameraNote)) sb.Append('\n').Append(_cameraNote);
            _deviceInfo.Text = sb.ToString();
        }

        /// <summary>Live camera elevation / pose check on the Advanced screen.</summary>
        private void UpdateLive()
        {
            if (_advancedVisible)
            {
                Vec3? a = _gravity.Latest;
                var sb = new StringBuilder();
                if (a.HasValue)
                {
                    // Camera elevation above the horizon: the back camera looks along device -Z.
                    Vec3 up = a.Value.Normalized();
                    double elev = Math.Asin(Math.Clamp(-up.Z, -1, 1)) * Astronomy.RadToDeg;
                    sb.Append($"Camera elevation {elev.ToString("F0", Inv)}°");
                    if (_calibration != null && _calibration.PoseSpecific)
                    {
                        double d = _calibration.PoseDifferenceDeg(a.Value);
                        sb.Append(d > PoseWarnDeg ? $" · {d.ToString("F1", Inv)}° off calibration pose" : " · calibration pose ✓");
                    }
                    sb.Append('\n');
                }
                sb.Append($"UTC {DateTime.UtcNow:HH:mm:ss}");
                _liveInfo.Text = sb.ToString();
            }
            if (!IsFinishing) _handler.PostDelayed(UpdateLive, 500);
        }
    }
}
