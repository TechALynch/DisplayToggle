// Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DisplayToggle
{
    /// <summary>Windows 11 look: follows the system light/dark setting and accent color.</summary>
    internal sealed class Theme
    {
        public bool Dark;
        public Color Bg, Card, CardHover, CardBorder;
        public Color Text, TextSecondary, TextTertiary;
        public Color Accent, OnAccent, ToggleOff, MapOn, Error;
        public Color Button, ButtonHover, ButtonBorder;

        public static Theme Current()
        {
            bool dark = true;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) dark = (int)v == 0;
                }
            }
            catch { }

            var t = new Theme();
            t.Dark = dark;
            if (dark)
            {
                t.Bg = Rgb(0x20, 0x20, 0x20);
                t.Card = Rgb(0x2B, 0x2B, 0x2B);
                t.CardHover = Rgb(0x32, 0x32, 0x32);
                t.CardBorder = Rgb(0x1C, 0x1C, 0x1C);
                t.Text = Rgb(0xFF, 0xFF, 0xFF);
                t.TextSecondary = Rgb(0xC8, 0xC8, 0xC8);
                t.TextTertiary = Rgb(0x8A, 0x8A, 0x8A);
                t.OnAccent = Rgb(0x00, 0x00, 0x00);
                t.ToggleOff = Rgb(0xA0, 0xA0, 0xA0);
                t.MapOn = Rgb(0x48, 0x48, 0x48);
                t.Error = Rgb(0xFF, 0x99, 0xA4);
                t.Button = Rgb(0x2D, 0x2D, 0x2D);
                t.ButtonHover = Rgb(0x37, 0x37, 0x37);
                t.ButtonBorder = Rgb(0x3A, 0x3A, 0x3A);
            }
            else
            {
                t.Bg = Rgb(0xF3, 0xF3, 0xF3);
                t.Card = Rgb(0xFB, 0xFB, 0xFB);
                t.CardHover = Rgb(0xF5, 0xF5, 0xF5);
                t.CardBorder = Rgb(0xE5, 0xE5, 0xE5);
                t.Text = Rgb(0x1A, 0x1A, 0x1A);
                t.TextSecondary = Rgb(0x5F, 0x5F, 0x5F);
                t.TextTertiary = Rgb(0x8F, 0x8F, 0x8F);
                t.OnAccent = Rgb(0xFF, 0xFF, 0xFF);
                t.ToggleOff = Rgb(0x5F, 0x5F, 0x5F);
                t.MapOn = Rgb(0xD6, 0xD6, 0xD6);
                t.Error = Rgb(0xC4, 0x2B, 0x1C);
                t.Button = Rgb(0xFD, 0xFD, 0xFD);
                t.ButtonHover = Rgb(0xF6, 0xF6, 0xF6);
                t.ButtonBorder = Rgb(0xE0, 0xE0, 0xE0);
            }
            t.Accent = ReadAccent(dark);
            return t;
        }

        static Color ReadAccent(bool dark)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var b = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    if (b != null && b.Length >= 32)
                    {
                        // Palette order: Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3.
                        // Windows 11 controls use Light2 on dark backgrounds and Dark1 on light ones.
                        int i = dark ? 1 : 4;
                        return Rgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                    }
                }
            }
            catch { }
            return dark ? Rgb(0x4C, 0xC2, 0xFF) : Rgb(0x00, 0x5F, 0xB8);
        }

        static Color Rgb(int r, int g, int b)
        {
            return Color.FromArgb(r, g, b);
        }
    }

    internal static class Ui
    {
        public static Font MakeFont(float px, bool semibold)
        {
            string[] families = semibold
                ? new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }
                : new[] { "Segoe UI Variable Text", "Segoe UI" };
            foreach (var f in families)
            {
                var font = new Font(f, px, FontStyle.Regular, GraphicsUnit.Pixel);
                if (string.Equals(font.Name, f, StringComparison.OrdinalIgnoreCase)) return font;
                font.Dispose();
            }
            return new Font("Segoe UI", px, semibold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, path);
        }

        public static void StrokeRound(Graphics g, Color c, float width, RectangleF r, float radius)
        {
            float h = width / 2;
            using (var path = RoundRect(new RectangleF(r.X + h, r.Y + h, r.Width - width, r.Height - width), radius))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, path);
        }

        public static Color Fade(Color c, float alpha)
        {
            return Color.FromArgb(Clamp((int)(c.A * alpha)), c.R, c.G, c.B);
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                Clamp((int)(a.A + (b.A - a.A) * t)), Clamp((int)(a.R + (b.R - a.R) * t)),
                Clamp((int)(a.G + (b.G - a.G) * t)), Clamp((int)(a.B + (b.B - a.B) * t)));
        }

        static int Clamp(int v)
        {
            return v < 0 ? 0 : v > 255 ? 255 : v;
        }

        // ---- DPI ----

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X, Y;
        }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        /// <summary>Scale factor (1.0 = 100%) of the monitor containing a point (physical pixels).</summary>
        public static float ScaleAt(Point p)
        {
            try
            {
                var pt = new POINT { X = p.X, Y = p.Y };
                IntPtr mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0 && dx > 0) return dx / 96f;
            }
            catch { }
            return 1f;
        }

        public static void RoundCorners(IntPtr hwnd, bool dark)
        {
            try
            {
                int pref = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
                int d = dark ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, 4);
            }
            catch { }
        }
    }
}
