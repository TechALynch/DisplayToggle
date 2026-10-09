// Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

using System;
using System.Drawing;
using System.Windows.Forms;
using TF = System.Windows.Forms.TextFormatFlags;

namespace DisplayToggle
{
    /// <summary>Label shown in the corner of a display for a few seconds so you can tell which one it is.</summary>
    internal sealed class IdentifyOverlay : Form
    {
        readonly Theme theme;
        readonly string headline, detail;
        readonly Font fontBig, fontSmall;
        readonly int pad, bar, gap, headlineHeight;
        readonly Timer timer = new Timer();

        public IdentifyOverlay(DisplayInfo d, Theme theme)
        {
            this.theme = theme;
            headline = string.IsNullOrEmpty(d.Position) ? d.Name : d.Position;
            detail = string.IsNullOrEmpty(d.Position) ? d.Connector : d.Name;
            if (d.Primary) detail += " · Main display";

            float s = Ui.ScaleAt(new Point(d.Bounds.X + d.Bounds.Width / 2, d.Bounds.Y + d.Bounds.Height / 2));
            fontBig = Ui.MakeFont(44 * s, true);
            fontSmall = Ui.MakeFont(16 * s, false);
            pad = (int)(24 * s);
            bar = (int)(6 * s);
            gap = (int)(6 * s);
            var a = TextRenderer.MeasureText(headline, fontBig, Size.Empty, TF.NoPadding);
            var b = TextRenderer.MeasureText(detail, fontSmall, Size.Empty, TF.NoPadding);
            headlineHeight = a.Height;
            int w = Math.Max(a.Width, b.Width) + bar + pad * 2;
            int h = a.Height + gap + b.Height + pad * 2;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = theme.Card;

            var area = Screen.FromRectangle(d.Bounds).WorkingArea;
            int margin = (int)(32 * s);
            Bounds = new Rectangle(area.X + margin, area.Bottom - h - margin, w, h);

            timer.Interval = 3000;
            timer.Tick += (x, y) => Close();
            timer.Start();
            Click += (x, y) => Close();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x8 /* WS_EX_TOPMOST */;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.RoundCorners(Handle, theme.Dark);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(theme.Card);
            using (var br = new SolidBrush(theme.Accent)) g.FillRectangle(br, 0, 0, bar, Height);
            int x = bar + pad;
            TextRenderer.DrawText(g, headline, fontBig, new Point(x, pad), theme.Text, TF.NoPadding | TF.NoPrefix);
            TextRenderer.DrawText(g, detail, fontSmall, new Point(x, pad + headlineHeight + gap),
                theme.TextSecondary, TF.NoPadding | TF.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                fontBig.Dispose();
                fontSmall.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
