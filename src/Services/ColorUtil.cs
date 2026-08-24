using System;
using System.Globalization;
using System.Windows.Media;

namespace Dockyard.Services
{
    /// <summary>Hex / RGB / HSV conversions for the colour editor.</summary>
    public static class ColorUtil
    {
        public static Color Parse(string hex, Color fallback)
        {
            try
            {
                object o = ColorConverter.ConvertFromString(hex);
                if (o is Color) return (Color)o;
            }
            catch { }
            return fallback;
        }

        /// <summary>Always emits #AARRGGBB so alpha round-trips through the config file.</summary>
        public static string ToHex(Color c)
        {
            return "#" + c.A.ToString("X2", CultureInfo.InvariantCulture)
                       + c.R.ToString("X2", CultureInfo.InvariantCulture)
                       + c.G.ToString("X2", CultureInfo.InvariantCulture)
                       + c.B.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>h in 0..360, s and v in 0..1.</summary>
        public static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            v = max;
            s = max <= 0 ? 0 : d / max;

            if (d <= 0) { h = 0; return; }

            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);

            if (h < 0) h += 360;
        }

        public static Color FromHsv(double h, double s, double v, byte alpha)
        {
            h = ((h % 360) + 360) % 360;
            s = Clamp01(s);
            v = Clamp01(v);

            double c = v * s;
            double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
            double m = v - c;

            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromArgb(
                alpha,
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static double Clamp01(double d)
        {
            return d < 0 ? 0 : (d > 1 ? 1 : d);
        }

        /// <summary>Rainbow ramp for the hue slider.</summary>
        public static LinearGradientBrush HueRamp()
        {
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 0)
            };
            for (int i = 0; i <= 6; i++)
                b.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1, 255), i / 6.0));
            b.Freeze();
            return b;
        }

        public static LinearGradientBrush Ramp(Color from, Color to)
        {
            LinearGradientBrush b = new LinearGradientBrush(from, to, 0);
            b.Freeze();
            return b;
        }

        /// <summary>Grey checkerboard so alpha is legible behind a translucent swatch.</summary>
        public static DrawingBrush Checkerboard(double cell = 7)
        {
            DrawingGroup g = new DrawingGroup();

            g.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46)), null,
                new RectangleGeometry(new System.Windows.Rect(0, 0, cell * 2, cell * 2))));

            GeometryGroup squares = new GeometryGroup();
            squares.Children.Add(new RectangleGeometry(new System.Windows.Rect(0, 0, cell, cell)));
            squares.Children.Add(new RectangleGeometry(new System.Windows.Rect(cell, cell, cell, cell)));
            g.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x34)), null, squares));

            DrawingBrush brush = new DrawingBrush(g)
            {
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(0, 0, cell * 2, cell * 2),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None
            };
            brush.Freeze();
            return brush;
        }
    }
}
