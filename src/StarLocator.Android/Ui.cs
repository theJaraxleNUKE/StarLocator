using System;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Orientation = Android.Widget.Orientation;

namespace StarLocator.Droid
{
    /// <summary>Colour scheme. "Night" matches the app icon; "Red" preserves night vision.</summary>
    internal sealed class Palette
    {
        public Color Bg, Surface, Surface2, Primary, OnPrimary, Accent, Text, TextDim, Ok, Warn, Error;

        public static Palette Night => new Palette
        {
            Bg = Color.Rgb(10, 15, 28), Surface = Color.Rgb(21, 28, 46), Surface2 = Color.Rgb(31, 40, 64),
            Primary = Color.Rgb(255, 210, 122), OnPrimary = Color.Rgb(26, 20, 6), Accent = Color.Rgb(143, 180, 255),
            Text = Color.Rgb(236, 240, 250), TextDim = Color.Rgb(163, 174, 201),
            Ok = Color.Rgb(127, 217, 154), Warn = Color.Rgb(255, 184, 107), Error = Color.Rgb(255, 138, 128)
        };

        public static Palette Red => new Palette
        {
            Bg = Color.Rgb(10, 0, 0), Surface = Color.Rgb(26, 5, 5), Surface2 = Color.Rgb(40, 9, 9),
            Primary = Color.Rgb(255, 90, 80), OnPrimary = Color.Rgb(20, 0, 0), Accent = Color.Rgb(255, 122, 110),
            Text = Color.Rgb(255, 138, 128), TextDim = Color.Rgb(186, 90, 84),
            Ok = Color.Rgb(255, 122, 110), Warn = Color.Rgb(255, 160, 120), Error = Color.Rgb(255, 90, 80)
        };
    }

    /// <summary>Small factory for the app's flat, rounded, dark widgets (no AndroidX dependency).</summary>
    internal sealed class Ui
    {
        private readonly Context _ctx;
        public Palette P { get; }

        public Ui(Context ctx, Palette palette) { _ctx = ctx; P = palette; }

        public int Dp(float dp) => (int)Math.Round(dp * (_ctx.Resources?.DisplayMetrics?.Density ?? 2f));

        public static GradientDrawable Rounded(Color fill, float radiusPx, Color? stroke = null, int strokePx = 0)
        {
            var g = new GradientDrawable();
            g.SetShape(ShapeType.Rectangle);
            g.SetCornerRadius(radiusPx);
            g.SetColor(fill.ToArgb());
            if (stroke.HasValue && strokePx > 0) g.SetStroke(strokePx, stroke.Value);
            return g;
        }

        private static Drawable Ripple(Drawable content, Color tint) =>
            new RippleDrawable(ColorStateList.ValueOf(Color.Argb(60, tint.R, tint.G, tint.B)), content, null);

        public TextView Text(string text, float sp, Color color, bool bold = false)
        {
            var t = new TextView(_ctx) { Text = text, TextSize = sp };
            t.SetTextColor(color);
            t.SetTypeface(Typeface.Create(bold ? "sans-serif-medium" : "sans-serif", TypefaceStyle.Normal), TypefaceStyle.Normal);
            t.SetLineSpacing(0, 1.15f);
            return t;
        }

        /// <summary>Section heading used on the Advanced screen.</summary>
        public TextView Heading(string text)
        {
            TextView t = Text(text.ToUpperInvariant(), 12, P.Accent, bold: true);
            t.LetterSpacing = 0.08f;
            t.SetPadding(0, 0, 0, Dp(10));
            return t;
        }

        public LinearLayout Card()
        {
            var c = new LinearLayout(_ctx) { Orientation = Orientation.Vertical };
            c.Background = Rounded(P.Surface, Dp(20));
            c.SetPadding(Dp(20), Dp(18), Dp(20), Dp(18));
            return c;
        }

        public enum ButtonKind { Filled, Tonal, Outline, Text }

        public Button Button(string text, ButtonKind kind, EventHandler onClick)
        {
            var b = new Button(_ctx) { Text = text, TextSize = 15 };
            b.SetAllCaps(false);
            b.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
            b.StateListAnimator = null;
            b.SetMinHeight(Dp(48));
            b.SetMinimumHeight(Dp(48));
            b.SetPadding(Dp(20), 0, Dp(20), 0);
            switch (kind)
            {
                case ButtonKind.Filled:
                    b.SetTextColor(P.OnPrimary);
                    b.Background = Ripple(Rounded(P.Primary, Dp(24)), P.OnPrimary);
                    break;
                case ButtonKind.Tonal:
                    b.SetTextColor(P.Text);
                    b.Background = Ripple(Rounded(P.Surface2, Dp(24)), P.Text);
                    break;
                case ButtonKind.Outline:
                    b.SetTextColor(P.Text);
                    b.Background = Ripple(Rounded(Color.Transparent, Dp(24), P.TextDim, Dp(1)), P.Text);
                    break;
                default:
                    b.SetTextColor(P.Accent);
                    b.Background = Ripple(Rounded(Color.Transparent, Dp(24)), P.Accent);
                    break;
            }
            b.Click += onClick;
            return b;
        }

        /// <summary>Small rounded status pill.</summary>
        public TextView Chip(string text, Color color)
        {
            TextView t = Text(text, 13, color, bold: true);
            t.Background = Rounded(P.Surface2, Dp(14));
            t.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
            return t;
        }

        public EditText Field(string text, string hint, bool numeric)
        {
            var e = new EditText(_ctx) { Text = text, Hint = hint, TextSize = 16 };
            e.SetTextColor(P.Text);
            e.SetHintTextColor(P.TextDim);
            e.SetSingleLine(true);
            if (numeric) e.InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal;
            e.Background = Rounded(P.Surface2, Dp(12));
            e.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
            return e;
        }

        /// <summary>A label above a field.</summary>
        public LinearLayout Labeled(string label, View field)
        {
            var col = new LinearLayout(_ctx) { Orientation = Orientation.Vertical };
            TextView l = Text(label, 13, P.TextDim);
            l.SetPadding(Dp(2), 0, 0, Dp(6));
            col.AddView(l);
            col.AddView(field);
            return col;
        }

        public Switch Switch(string text, bool on)
        {
            var s = new Switch(_ctx) { Text = text, Checked = on, TextSize = 15 };
            s.SetTextColor(P.Text);
            var states = new[] { new[] { Android.Resource.Attribute.StateChecked }, Array.Empty<int>() };
            s.ThumbTintList = new ColorStateList(states, new[] { P.Primary.ToArgb(), P.TextDim.ToArgb() });
            s.TrackTintList = new ColorStateList(states, new[] { Color.Argb(110, P.Primary.R, P.Primary.G, P.Primary.B).ToArgb(), P.Surface2.ToArgb() });
            s.SetMinHeight(Dp(48));
            return s;
        }

        /// <summary>Segmented control: one option selected at a time.</summary>
        public LinearLayout Segmented(string[] options, int selected, Action<int> onSelect)
        {
            var row = new LinearLayout(_ctx) { Orientation = Orientation.Horizontal };
            row.Background = Rounded(P.Surface2, Dp(24));
            row.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));
            var buttons = new TextView[options.Length];
            void Paint(int sel)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    bool on = i == sel;
                    buttons[i].SetTextColor(on ? P.OnPrimary : P.Text);
                    buttons[i].Background = on ? Rounded(P.Primary, Dp(20)) : Rounded(Color.Transparent, Dp(20));
                }
            }
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                TextView t = Text(options[i], 15, P.Text, bold: true);
                t.Gravity = GravityFlags.Center;
                t.SetMinHeight(Dp(40));
                t.Clickable = true;
                t.Click += (s, e) => { Paint(idx); onSelect(idx); };
                buttons[i] = t;
                row.AddView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            }
            Paint(selected);
            return row;
        }

        public static LinearLayout.LayoutParams Gap(int topPx, int width = ViewGroup.LayoutParams.MatchParent, int height = ViewGroup.LayoutParams.WrapContent)
        {
            var lp = new LinearLayout.LayoutParams(width, height);
            lp.TopMargin = topPx;
            return lp;
        }
    }
}
