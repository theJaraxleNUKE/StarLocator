using System;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Views;

namespace StarLocator.Droid
{
    /// <summary>The big round start button. Its ring shows countdown / exposure / solving progress.</summary>
    public sealed class RingButton : View
    {
        private readonly Paint _track = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _arc = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _fill = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _title = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _sub = new Paint(PaintFlags.AntiAlias);
        private float _progress;
        private string _titleText = "", _subText = "";

        public RingButton(Context context) : base(context) { Init(); }
        private RingButton(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { Init(); }

        private void Init()
        {
            Clickable = true;
            Focusable = true;
            _track.SetStyle(Paint.Style.Stroke!);
            _arc.SetStyle(Paint.Style.Stroke!);
            _arc.StrokeCap = Paint.Cap.Round!;
            _fill.SetStyle(Paint.Style.Fill!);
            _title.TextAlign = Paint.Align.Center!;
            _title.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
            _sub.TextAlign = Paint.Align.Center!;
            _sub.SetTypeface(Typeface.Create("sans-serif", TypefaceStyle.Normal));
        }

        public void SetColors(Color face, Color track, Color arc, Color title, Color sub)
        {
            _fill.Color = face; _track.Color = track; _arc.Color = arc; _title.Color = title; _sub.Color = sub;
            var mask = new ShapeDrawable(new Android.Graphics.Drawables.Shapes.OvalShape());
            Foreground = new RippleDrawable(ColorStateList.ValueOf(Color.Argb(50, title.R, title.G, title.B)), null, mask);
            Invalidate();
        }

        /// <summary>Updates the label and ring (progress 0..1).</summary>
        public void Show(string title, string subtitle, float progress)
        {
            _titleText = title;
            _subText = subtitle;
            _progress = Math.Clamp(progress, 0f, 1f);
            ContentDescription = string.IsNullOrEmpty(subtitle) ? title : title + ", " + subtitle;
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float w = Width, h = Height;
            float cx = w / 2f, cy = h / 2f;
            float stroke = Math.Min(w, h) * 0.045f;
            float r = Math.Min(w, h) / 2f - stroke;
            _track.StrokeWidth = stroke;
            _arc.StrokeWidth = stroke;

            canvas.DrawCircle(cx, cy, r - stroke * 0.9f, _fill);
            canvas.DrawCircle(cx, cy, r, _track);
            if (_progress > 0)
                canvas.DrawArc(new RectF(cx - r, cy - r, cx + r, cy + r), -90f, 360f * _progress, false, _arc);

            _title.TextSize = Math.Min(w, h) * (_titleText.Length > 4 ? 0.15f : 0.22f);
            _sub.TextSize = Math.Min(w, h) * 0.065f;
            bool hasSub = !string.IsNullOrEmpty(_subText);
            float titleY = cy + _title.TextSize * 0.35f - (hasSub ? _sub.TextSize * 0.8f : 0f);
            canvas.DrawText(_titleText, cx, titleY, _title);
            if (hasSub) canvas.DrawText(_subText, cx, titleY + _sub.TextSize * 2.0f, _sub);
        }
    }
}
