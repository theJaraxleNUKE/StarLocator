using System;
using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Views;
using Android.Views.Animations;

namespace StarLocator.Droid
{
    /// <summary>
    /// Opening animation: the app logo assembles itself. The disc fades in, the reticle ring draws
    /// itself, the tick marks and faint stars appear, and the big centre star starts tiny and off to
    /// the side, spins and grows along a curve into the centre, completing the logo. Then the view
    /// fades away to reveal the main screen. A tap skips it. Geometry follows the icon's 108-unit grid.
    /// </summary>
    internal sealed class SplashView : View
    {
        private const long DrawMs = 2000, HoldMs = 300, FadeMs = 350;
        private const float C = 54f;           // icon grid centre
        // Star path start (relative to centre) and Bézier control point: off to the upper right, curving in.
        private const float StartX = 34f, StartY = -30f, CtrlX = 40f, CtrlY = 14f;

        private static readonly (float X, float Y, float R)[] Dots =
        {
            (30, 34, 0.9f), (78, 30, 1.1f), (74, 78, 0.8f), (33, 74, 1f), (84, 56, 0.7f), (24, 56, 0.7f)
        };

        private readonly Palette _p;
        private readonly Action _onDone;
        private readonly Paint _fill = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _stroke = new Paint(PaintFlags.AntiAlias);
        private readonly Path _star = new Path();
        private ValueAnimator? _animator;
        private float _t;                       // milliseconds into the drawing phase
        private bool _finishing;

        public SplashView(Context context, Palette palette, Action onDone) : base(context)
        {
            _p = palette;
            _onDone = onDone;
            Clickable = true;                   // swallow touches meant for the screen underneath
            ContentDescription = "Star Locator";
            _stroke.SetStyle(Paint.Style.Stroke!);
            _stroke.StrokeCap = Paint.Cap.Round!;
            _fill.SetStyle(Paint.Style.Fill!);
            SetBackgroundColor(palette.Bg);
            Click += (s, e) => Finish();
        }

        private SplashView(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
            _p = Palette.Night;
            _onDone = () => { };
        }

        /// <summary>Starts the animation (call once the view is attached).</summary>
        public void Start()
        {
            ValueAnimator? anim = ValueAnimator.OfFloat(0f, DrawMs);
            if (anim == null) { Finish(); return; }
            _animator = anim;
            anim.SetDuration(DrawMs);
            anim.SetInterpolator(new LinearInterpolator());
            anim.Update += (s, e) =>
            {
                _t = (float)(e.Animation.AnimatedValue as Java.Lang.Float ?? Java.Lang.Float.ValueOf(DrawMs)).FloatValue();
                Invalidate();
            };
            anim.AnimationEnd += (s, e) => { if (!_finishing) PostDelayed(Finish, HoldMs); };
            anim.Start();
        }

        /// <summary>Fades out and removes itself.</summary>
        private void Finish()
        {
            if (_finishing) return;
            _finishing = true;
            _animator?.Cancel();
            _t = DrawMs;
            Invalidate();
            Animate()?.Alpha(0f).SetDuration(FadeMs).WithEndAction(new Java.Lang.Runnable(() =>
            {
                (Parent as ViewGroup)?.RemoveView(this);
                _onDone();
            }))?.Start();
        }

        // ---- Easing helpers -------------------------------------------------------------------------

        /// <summary>0..1 progress of the window [startMs, endMs].</summary>
        private float Phase(float startMs, float endMs) => Math.Clamp((_t - startMs) / (endMs - startMs), 0f, 1f);
        private static float EaseOut(float x) => 1 - (1 - x) * (1 - x) * (1 - x);
        private static float EaseInOut(float x) => x < 0.5f ? 4 * x * x * x : 1 - (float)Math.Pow(-2 * x + 2, 3) / 2;
        /// <summary>Ease-out with a small overshoot (back-out).</summary>
        private static float BackOut(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1;
            return 1 + c3 * (float)Math.Pow(x - 1, 3) + c1 * (float)Math.Pow(x - 1, 2);
        }

        private static Color WithAlpha(Color c, float a) => Color.Argb((int)(Math.Clamp(a, 0f, 1f) * 255), c.R, c.G, c.B);

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float side = Math.Min(Width, Height) * 0.62f;
            float k = side / 108f;
            canvas.Save();
            canvas.Translate((Width - side) / 2f, (Height - side) / 2f);
            canvas.Scale(k, k);

            // 1) Disc fades and grows in (0–350 ms).
            float disc = EaseOut(Phase(0, 350));
            if (disc > 0)
            {
                _fill.SetShader(new RadialGradient(C, C - 0.12f * 108, 0.75f * 108,
                    new[] { WithAlpha(_p.DiscInner, disc).ToArgb(), WithAlpha(_p.DiscOuter, disc).ToArgb() }, null, Shader.TileMode.Clamp!));
                canvas.DrawCircle(C, C, 50f * (0.9f + 0.1f * disc), _fill);
                _fill.SetShader(null);
            }

            // 2) Faint background stars twinkle on, one after another (150–700 ms).
            for (int i = 0; i < Dots.Length; i++)
            {
                float a = Phase(150 + 70 * i, 400 + 70 * i);
                if (a <= 0) continue;
                _fill.Color = WithAlpha(_p.Dots, 0.75f * a);
                canvas.DrawCircle(Dots[i].X, Dots[i].Y, Dots[i].R, _fill);
            }

            // 3) Ring draws itself clockwise from the top (200–750 ms).
            float ring = EaseInOut(Phase(200, 750));
            if (ring > 0)
            {
                _stroke.Color = _p.Accent;
                _stroke.StrokeWidth = 2f;
                canvas.DrawArc(new RectF(C - 24, C - 24, C + 24, C + 24), -90f, 360f * ring, false, _stroke);
            }

            // 4) Tick marks slide in from outside (650–950 ms).
            float tick = EaseOut(Phase(650, 950));
            if (tick > 0)
            {
                _stroke.Color = WithAlpha(_p.Accent, tick);
                _stroke.StrokeWidth = 2.4f;
                float slide = (1 - tick) * 6f;
                canvas.DrawLine(C, 24 - slide, C, 32 - slide, _stroke);
                canvas.DrawLine(C, 76 + slide, C, 84 + slide, _stroke);
                canvas.DrawLine(24 - slide, C, 32 - slide, C, _stroke);
                canvas.DrawLine(76 + slide, C, 84 + slide, C, _stroke);
            }

            // 5) The centre star: tiny and off to the side, spins and curves into place (300–1650 ms).
            float m = Phase(300, 1650);
            if (m > 0)
            {
                float u = EaseInOut(m);
                // Quadratic Bézier from the start point, via the control point, to the centre.
                float bx = (1 - u) * (1 - u) * StartX + 2 * (1 - u) * u * CtrlX;
                float by = (1 - u) * (1 - u) * StartY + 2 * (1 - u) * u * CtrlY;
                float scale = 0.12f + 0.88f * BackOut(m);
                float spin = 900f * EaseOut(m);          // two and a half turns, slowing down
                float appear = Phase(300, 450);

                // Glow blooms as the star locks into the centre (1450–1900 ms).
                float glow = EaseOut(Phase(1450, 1900));
                if (glow > 0)
                {
                    float pulse = 1f + 0.25f * (float)Math.Sin(Math.PI * Phase(1600, 1950));
                    _fill.SetShader(new RadialGradient(C, C, 13f * pulse,
                        new[] { WithAlpha(_p.Glow, 0.9f * glow).ToArgb(), WithAlpha(_p.Glow, 0f).ToArgb() }, null, Shader.TileMode.Clamp!));
                    canvas.DrawCircle(C, C, 13f * pulse, _fill);
                    _fill.SetShader(null);
                }

                canvas.Save();
                canvas.Translate(C + bx, C + by);
                canvas.Rotate(spin);
                canvas.Scale(scale, scale);
                _star.Reset();
                _star.MoveTo(0, -15); _star.LineTo(3.2f, -3.2f); _star.LineTo(15, 0); _star.LineTo(3.2f, 3.2f);
                _star.LineTo(0, 15); _star.LineTo(-3.2f, 3.2f); _star.LineTo(-15, 0); _star.LineTo(-3.2f, -3.2f);
                _star.Close();
                _fill.Color = WithAlpha(_p.Primary, appear);
                canvas.DrawPath(_star, _fill);
                canvas.Restore();
            }
            canvas.Restore();
        }

        /// <summary>True if the user has turned animations off (Settings › Accessibility › Remove animations).</summary>
        public static bool AnimationsDisabled(Context ctx)
        {
            try
            {
                return Android.Provider.Settings.Global.GetFloat(ctx.ContentResolver, Android.Provider.Settings.Global.AnimatorDurationScale, 1f) == 0f;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
