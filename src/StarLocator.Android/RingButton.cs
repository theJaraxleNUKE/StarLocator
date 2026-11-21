using System;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Views;

namespace StarLocator.Droid
{
    /// <summary>
    /// The big round Locate button, drawn like the app icon without its centre star: a navy disc,
    /// a reticle ring with four tick marks, and small four-point stars scattered around the outside
    /// ("scattered sky" design). While busy, a progress arc fills the ring and each star lights up
    /// as the arc passes it. Geometry is defined on a 280 × 280 grid and scaled to the view.
    /// </summary>
    public sealed class RingButton : View
    {
        private const float Grid = 280f, Centre = 140f, RingR = 96f;

        // (clockwise angle from top in degrees, radius, star size) on the 280 grid.
        private static readonly (float Deg, float R, float Size)[] Stars =
        {
            (28, 122, 9), (62, 130, 5), (118, 124, 7), (196, 128, 4.5f), (232, 121, 10), (300, 129, 5.5f), (334, 123, 7.5f)
        };
        private static readonly (float Deg, float R, float Size)[] Dots =
        {
            (80, 133, 1.4f), (200, 134, 1.2f), (292, 133, 1.3f), (110, 136, 1.1f)
        };

        private readonly Paint _disc = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _ring = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _tick = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _arc = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _star = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _glow = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _dot = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _title = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _sub = new Paint(PaintFlags.AntiAlias);
        private readonly Path _starPath = new Path();

        private Color _discInner = Color.Rgb(27, 47, 99), _discOuter = Color.Rgb(6, 11, 31);
        private Color _starColor = Color.Rgb(255, 210, 122), _glowColor = Color.Rgb(255, 231, 168);
        private float _progress;
        private bool _active;
        private string _titleText = "", _subText = "";

        public RingButton(Context context) : base(context) { Init(); }
        private RingButton(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { Init(); }

        private void Init()
        {
            Clickable = true;
            Focusable = true;
            _disc.SetStyle(Paint.Style.Fill!);
            _ring.SetStyle(Paint.Style.Stroke!);
            _tick.SetStyle(Paint.Style.Stroke!);
            _tick.StrokeCap = Paint.Cap.Round!;
            _arc.SetStyle(Paint.Style.Stroke!);
            _arc.StrokeCap = Paint.Cap.Round!;
            _star.SetStyle(Paint.Style.Fill!);
            _glow.SetStyle(Paint.Style.Fill!);
            _dot.SetStyle(Paint.Style.Fill!);
            _title.TextAlign = Paint.Align.Center!;
            _title.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
            _sub.TextAlign = Paint.Align.Center!;
            _sub.SetTypeface(Typeface.Create("sans-serif", TypefaceStyle.Normal));
        }

        /// <summary>Colours: disc gradient, ring/ticks, progress arc and stars, star glow, faint dots, title, subtitle.</summary>
        public void SetColors(Color discInner, Color discOuter, Color ring, Color starAndArc, Color glow, Color dots, Color title, Color sub)
        {
            _discInner = discInner; _discOuter = discOuter;
            _ring.Color = ring; _tick.Color = ring;
            _starColor = starAndArc; _arc.Color = starAndArc;
            _glowColor = glow;
            _dot.Color = dots;
            _title.Color = title; _sub.Color = sub;
            var mask = new ShapeDrawable(new Android.Graphics.Drawables.Shapes.OvalShape());
            Foreground = new RippleDrawable(ColorStateList.ValueOf(Color.Argb(50, title.R, title.G, title.B)), null, mask);
            Invalidate();
        }

        /// <summary>
        /// Updates the label and ring. <paramref name="active"/> false = idle (no arc, every star lit);
        /// true = busy: the arc shows <paramref name="progress"/> (0..1) and stars light up as it passes them.
        /// </summary>
        public void Show(string title, string subtitle, float progress, bool active = true)
        {
            _titleText = title;
            _subText = subtitle;
            _progress = Math.Clamp(progress, 0f, 1f);
            _active = active;
            ContentDescription = string.IsNullOrEmpty(subtitle) ? title : title + ", " + subtitle;
            Invalidate();
        }

        private static (float X, float Y) Polar(float deg, float r)
        {
            double a = deg * Math.PI / 180.0;
            return (Centre + r * (float)Math.Sin(a), Centre - r * (float)Math.Cos(a));
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float side = Math.Min(Width, Height);
            float k = side / Grid;
            canvas.Save();
            canvas.Translate((Width - side) / 2f, (Height - side) / 2f);
            canvas.Scale(k, k);

            // Faint background dots.
            foreach (var d in Dots)
            {
                var (x, y) = Polar(d.Deg, d.R);
                _dot.Alpha = 150;
                canvas.DrawCircle(x, y, d.Size, _dot);
            }

            // Navy disc (same gradient as the icon background).
            _disc.SetShader(new RadialGradient(Centre, Centre - 0.12f * Grid, 0.62f * Grid,
                new[] { _discInner.ToArgb(), _discOuter.ToArgb() }, null, Shader.TileMode.Clamp!));
            canvas.DrawCircle(Centre, Centre, RingR - 1, _disc);

            // Reticle ring and tick marks.
            _ring.StrokeWidth = 3;
            canvas.DrawCircle(Centre, Centre, RingR, _ring);
            _tick.StrokeWidth = 4;
            for (int i = 0; i < 4; i++)
            {
                var (x1, y1) = Polar(90 * i, RingR - 10);
                var (x2, y2) = Polar(90 * i, RingR + 14);
                canvas.DrawLine(x1, y1, x2, y2, _tick);
            }

            // Progress arc over the ring.
            if (_active && _progress > 0)
            {
                _arc.StrokeWidth = 5;
                canvas.DrawArc(new RectF(Centre - RingR, Centre - RingR, Centre + RingR, Centre + RingR),
                               -90f, 360f * Math.Min(_progress, 0.9999f), false, _arc);
            }

            // Scattered stars; while busy, a star is lit once the arc has passed it.
            foreach (var s in Stars)
            {
                bool lit = !_active || s.Deg / 360f <= _progress;
                float op = lit ? 1f : 0.3f;
                var (x, y) = Polar(s.Deg, s.R);
                float glowR = 1.5f * s.Size;
                _glow.SetShader(new RadialGradient(x, y, glowR,
                    new[]
                    {
                        Color.Argb((int)(0.85f * 0.8f * op * 255), _glowColor.R, _glowColor.G, _glowColor.B).ToArgb(),
                        Color.Argb(0, _glowColor.R, _glowColor.G, _glowColor.B).ToArgb()
                    }, null, Shader.TileMode.Clamp!));
                canvas.DrawCircle(x, y, glowR, _glow);
                _star.Color = Color.Argb((int)(op * 255), _starColor.R, _starColor.G, _starColor.B);
                DrawFourPointStar(canvas, x, y, s.Size);
            }

            // Centre text.
            _title.TextSize = _titleText.Length > 3 ? 0.12f * Grid : 0.2f * Grid;
            _sub.TextSize = 0.048f * Grid;
            bool hasSub = !string.IsNullOrEmpty(_subText);
            float titleY = Centre + _title.TextSize * 0.35f - (hasSub ? _sub.TextSize * 0.9f : 0f);
            canvas.DrawText(_titleText, Centre, titleY, _title);
            if (hasSub) canvas.DrawText(_subText, Centre, titleY + _sub.TextSize * 2.0f, _sub);
            canvas.Restore();
        }

        private void DrawFourPointStar(Canvas canvas, float cx, float cy, float r)
        {
            float k = 0.213f * r;
            _starPath.Reset();
            _starPath.MoveTo(cx, cy - r);
            _starPath.LineTo(cx + k, cy - k);
            _starPath.LineTo(cx + r, cy);
            _starPath.LineTo(cx + k, cy + k);
            _starPath.LineTo(cx, cy + r);
            _starPath.LineTo(cx - k, cy + k);
            _starPath.LineTo(cx - r, cy);
            _starPath.LineTo(cx - k, cy - k);
            _starPath.Close();
            canvas.DrawPath(_starPath, _star);
        }
    }
}
