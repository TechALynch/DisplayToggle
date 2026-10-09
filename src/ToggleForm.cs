// Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using N = DisplayToggle.NativeMethods;
using TF = System.Windows.Forms.TextFormatFlags;

namespace DisplayToggle
{
    /// <summary>
    /// Small flyout listing every connected display with an on/off switch.
    /// Changes apply immediately (no "Keep changes / Revert" prompt).
    /// </summary>
    internal sealed class ToggleForm : Form
    {
        public const string WindowTitle = "Display Toggle";

        // Layout in DIPs; multiplied by the monitor's scale factor.
        const int FormWidth = 420, Pad = 16, RowHeight = 64, RowGap = 4, MapHeight = 112;

        enum Hit { None, Close, Map, Row, Identify, AllOn, Settings }

        readonly Theme theme = Theme.Current();
        float scale;
        Font fontTitle, fontBody, fontSmall, fontTiny;

        List<DisplayInfo> displays = new List<DisplayInfo>();
        string message;
        bool messageIsError;
        bool busy, closeWhenDone;
        string pendingKey;      // display being switched; null while "Turn all on" runs
        bool pendingOn;
        DateTime stayOpenUntil = DateTime.MinValue;

        Rectangle titleRect, summaryRect, closeRect, mapRect, messageRect, identifyRect, allOnRect, settingsRect;
        readonly List<Rectangle> rowRects = new List<Rectangle>();
        readonly List<Rectangle> switchRects = new List<Rectangle>();
        readonly List<RectangleF> mapRects = new List<RectangleF>();

        Hit hoverHit;
        int hoverIndex = -1;
        int focusIndex = -1;
        bool showFocus;

        readonly Dictionary<string, float> knob = new Dictionary<string, float>(); // 0 = off, 1 = on (animated)
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        readonly List<Form> overlays = new List<Form>();

        public ToggleForm()
        {
            Text = WindowTitle;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = theme.Bg;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            anim.Interval = 15;
            anim.Tick += (s, e) => StepAnimation();

            LoadDisplays();
            foreach (var d in displays) knob[d.Key] = d.Active ? 1f : 0f;
            PlaceWindow();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.RoundCorners(Handle, theme.Dark);
        }

        // ------------------------------------------------------------------ data

        void LoadDisplays()
        {
            try
            {
                displays = DisplayManager.GetDisplays();
            }
            catch (Exception ex)
            {
                displays = new List<DisplayInfo>();
                message = "Couldn't read your displays: " + ex.Message;
                messageIsError = true;
            }
            if (focusIndex >= displays.Count) focusIndex = displays.Count - 1;
        }

        bool IsPending(DisplayInfo d)
        {
            if (!busy) return false;
            return pendingKey == null ? pendingOn && !d.Active : pendingKey == d.Key;
        }

        float TargetOf(DisplayInfo d)
        {
            if (IsPending(d)) return pendingOn ? 1f : 0f;
            return d.Active ? 1f : 0f;
        }

        float KnobOf(DisplayInfo d)
        {
            float v;
            return knob.TryGetValue(d.Key, out v) ? v : TargetOf(d);
        }

        void StepAnimation()
        {
            bool moving = false;
            foreach (var d in displays)
            {
                float target = TargetOf(d), cur = KnobOf(d);
                const float step = 0.14f;
                if (Math.Abs(target - cur) <= step) cur = target;
                else
                {
                    cur += Math.Sign(target - cur) * step;
                    moving = true;
                }
                knob[d.Key] = cur;
            }
            if (!moving) anim.Stop();
            Invalidate();
        }

        // ------------------------------------------------------------------ actions

        void Toggle(int i)
        {
            if (busy || i < 0 || i >= displays.Count) return;
            var d = displays[i];
            focusIndex = i;
            if (d.Active && displays.Count(x => x.Active) <= 1)
            {
                SetMessage("That's the only display that's on. Turn another one on first.", true);
                return;
            }
            bool turnOn = !d.Active;
            RunChange(d.Key, turnOn, () => turnOn ? DisplayManager.Enable(d, false) : DisplayManager.Disable(d, false));
        }

        void TurnAllOn()
        {
            if (busy || !displays.Any(d => !d.Active)) return;
            RunChange(null, true, () => DisplayManager.EnableAll(false));
        }

        void RunChange(string key, bool turnOn, Func<string> work)
        {
            busy = true;
            pendingKey = key;
            pendingOn = turnOn;
            message = null;
            Size = Relayout();
            anim.Start();
            Invalidate();

            // SetDisplayConfig blocks for a second or two while the screens re-sync; keep the UI responsive.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;
                try { err = work(); }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke(new Action(() => FinishChange(err))); }
                catch (InvalidOperationException) { }
            });
        }

        void FinishChange(string err)
        {
            busy = false;
            pendingKey = null;
            stayOpenUntil = DateTime.Now.AddSeconds(2);
            if (closeWhenDone)
            {
                Close();
                return;
            }
            LoadDisplays();
            if (err != null)
            {
                message = err;
                messageIsError = true;
            }
            PlaceWindow(); // the monitor this window was on may have just turned off
            anim.Start();
            Invalidate();
            Activate();
        }

        void Identify()
        {
            CloseOverlays();
            foreach (var d in displays.Where(x => x.Active))
            {
                var o = new IdentifyOverlay(d, theme);
                o.FormClosed += (s, e) => overlays.Remove(o);
                overlays.Add(o);
                o.Show();
            }
        }

        void CloseOverlays()
        {
            foreach (var o in overlays.ToList()) o.Close();
            overlays.Clear();
        }

        void OpenSettings()
        {
            try { Process.Start("ms-settings:display"); } catch { }
            Close();
        }

        void SetMessage(string text, bool isError)
        {
            message = text;
            messageIsError = isError;
            Size = Relayout();
            Invalidate();
        }

        // ------------------------------------------------------------------ window placement & layout

        int P(float dip)
        {
            return (int)Math.Round(dip * scale);
        }

        void SetScale(float s)
        {
            if (fontBody != null && Math.Abs(s - scale) < 0.001f) return;
            scale = s;
            foreach (var f in new[] { fontTitle, fontBody, fontSmall, fontTiny }) if (f != null) f.Dispose();
            fontTitle = Ui.MakeFont(16 * s, true);
            fontBody = Ui.MakeFont(14 * s, false);
            fontSmall = Ui.MakeFont(12 * s, false);
            fontTiny = Ui.MakeFont(11 * s, false);
        }

        /// <summary>Centers the window on the monitor under the mouse.</summary>
        void PlaceWindow()
        {
            var cursor = Cursor.Position;
            var area = Screen.FromPoint(cursor).WorkingArea;
            SetScale(Ui.ScaleAt(cursor));
            var size = Relayout();
            Bounds = new Rectangle(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2,
                                   size.Width, size.Height);
        }

        Size Relayout()
        {
            int w = P(FormWidth), pad = P(Pad), inner = w - 2 * pad;
            int y = P(14);
            titleRect = new Rectangle(pad, y, inner - P(36), P(24));
            closeRect = new Rectangle(w - P(8) - P(36), P(8), P(36), P(32));
            y += P(24);
            summaryRect = new Rectangle(pad, y, inner, P(18));
            y += P(18) + P(12);

            mapRect = new Rectangle(pad, y, inner, P(MapHeight));
            y += mapRect.Height + P(8);

            rowRects.Clear();
            switchRects.Clear();
            foreach (var d in displays)
            {
                var r = new Rectangle(pad, y, inner, P(RowHeight));
                rowRects.Add(r);
                switchRects.Add(new Rectangle(r.Right - P(16) - P(40), r.Y + (r.Height - P(20)) / 2, P(40), P(20)));
                y += r.Height + P(RowGap);
            }

            if (!string.IsNullOrEmpty(message))
            {
                var sz = TextRenderer.MeasureText(message, fontSmall, new Size(inner, int.MaxValue), TF.WordBreak | TF.NoPrefix);
                messageRect = new Rectangle(pad, y + P(6), inner, sz.Height);
                y = messageRect.Bottom + P(2);
            }
            else messageRect = Rectangle.Empty;

            y += P(10);
            int bh = P(32);
            identifyRect = new Rectangle(pad, y, ButtonWidth("Identify"), bh);
            allOnRect = new Rectangle(identifyRect.Right + P(8), y, ButtonWidth("Turn all on"), bh);
            int sw = ButtonWidth("Display settings");
            settingsRect = new Rectangle(w - pad - sw, y, sw, bh);
            y += bh + P(16);

            LayoutMap();
            return new Size(w, y);
        }

        int ButtonWidth(string text)
        {
            return TextRenderer.MeasureText(text, fontBody, Size.Empty, TF.NoPadding).Width + P(28);
        }

        /// <summary>Scales the real desktop arrangement down into the map box.</summary>
        void LayoutMap()
        {
            mapRects.Clear();
            if (displays.Count == 0) return;
            var u = displays[0].LayoutRect;
            foreach (var d in displays) u = Rectangle.Union(u, d.LayoutRect);
            if (u.Width <= 0 || u.Height <= 0) return;

            var avail = RectangleF.Inflate(mapRect, -P(14), -P(14));
            float k = Math.Min(avail.Width / u.Width, avail.Height / u.Height);
            float ox = avail.X + (avail.Width - u.Width * k) / 2 - u.X * k;
            float oy = avail.Y + (avail.Height - u.Height * k) / 2 - u.Y * k;
            float gap = Math.Max(1f, 1.5f * scale);
            foreach (var d in displays)
            {
                var r = d.LayoutRect;
                mapRects.Add(new RectangleF(ox + r.X * k + gap, oy + r.Y * k + gap, r.Width * k - 2 * gap, r.Height * k - 2 * gap));
            }
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(theme.Bg);

            const TF line = TF.Left | TF.VerticalCenter | TF.SingleLine | TF.EndEllipsis | TF.NoPrefix | TF.NoPadding;
            TextRenderer.DrawText(g, "Displays", fontTitle, titleRect, theme.Text, line);
            TextRenderer.DrawText(g, Summary(), fontSmall, summaryRect, theme.TextSecondary, line);

            if (hoverHit == Hit.Close) Ui.FillRound(g, theme.CardHover, closeRect, P(4));
            float cx = closeRect.X + closeRect.Width / 2f, cy = closeRect.Y + closeRect.Height / 2f, x = 5 * scale;
            using (var pen = new Pen(hoverHit == Hit.Close ? theme.Text : theme.TextSecondary, Math.Max(1f, scale)))
            {
                g.DrawLine(pen, cx - x, cy - x, cx + x, cy + x);
                g.DrawLine(pen, cx - x, cy + x, cx + x, cy - x);
            }

            DrawMap(g);
            for (int i = 0; i < displays.Count; i++) DrawRow(g, i);

            if (!string.IsNullOrEmpty(message))
                TextRenderer.DrawText(g, message, fontSmall, messageRect, messageIsError ? theme.Error : theme.TextSecondary,
                    TF.WordBreak | TF.NoPrefix | TF.Left | TF.Top);

            DrawButton(g, identifyRect, "Identify", Hit.Identify, !busy && displays.Any(d => d.Active));
            DrawButton(g, allOnRect, "Turn all on", Hit.AllOn, !busy && displays.Any(d => !d.Active));
            DrawButton(g, settingsRect, "Display settings", Hit.Settings, true);
        }

        string Summary()
        {
            if (busy) return pendingKey == null ? "Turning displays on…" : "Applying…";
            if (displays.Count == 0) return "No displays found";
            int on = displays.Count(d => d.Active);
            return displays.Count + " connected · " + (on == displays.Count ? "all on" : on + " on");
        }

        string Detail(DisplayInfo d)
        {
            if (IsPending(d)) return pendingOn ? "Turning on…" : "Turning off…";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(d.Position)) parts.Add(d.Position);
            if (d.Active)
            {
                parts.Add(d.Bounds.Width + " × " + d.Bounds.Height);
                if (d.RefreshHz > 0) parts.Add(Math.Round(d.RefreshHz) + " Hz");
                if (d.Primary) parts.Add("Main");
            }
            else if (d.Connector != "" && d.Connector != "Built-in")
            {
                parts.Add(d.Connector);
            }
            return string.Join(" · ", parts);
        }

        bool IsHot(int i)
        {
            return (hoverHit == Hit.Row || hoverHit == Hit.Map) && hoverIndex == i;
        }

        void DrawMap(Graphics g)
        {
            Ui.FillRound(g, theme.Card, mapRect, P(8));
            Ui.StrokeRound(g, theme.CardBorder, 1, mapRect, P(8));

            for (int i = 0; i < displays.Count && i < mapRects.Count; i++)
            {
                var d = displays[i];
                var r = mapRects[i];
                bool hot = IsHot(i) || (showFocus && focusIndex == i);
                bool on = KnobOf(d) > 0.5f;
                float rad = Math.Min(P(4), Math.Min(r.Width, r.Height) / 4);
                Color label;
                if (on)
                {
                    Ui.FillRound(g, hot ? theme.Accent : theme.MapOn, r, rad);
                    label = hot ? theme.OnAccent : theme.Text;
                }
                else
                {
                    using (var path = Ui.RoundRect(r, rad))
                    using (var pen = new Pen(hot ? theme.Accent : theme.TextTertiary, Math.Max(1f, scale)) { DashStyle = DashStyle.Dash })
                        g.DrawPath(pen, path);
                    label = hot ? theme.Accent : theme.TextTertiary;
                }

                string text = on ? d.Position : "Off";
                if (string.IsNullOrEmpty(text)) continue;
                var sz = TextRenderer.MeasureText(text, fontTiny, Size.Empty, TF.NoPadding);
                if (sz.Width <= r.Width - 2 && sz.Height <= r.Height - 2)
                    TextRenderer.DrawText(g, text, fontTiny, Rectangle.Round(r), label,
                        TF.HorizontalCenter | TF.VerticalCenter | TF.SingleLine | TF.NoPadding | TF.NoPrefix);
            }
        }

        void DrawRow(Graphics g, int i)
        {
            var d = displays[i];
            var r = rowRects[i];
            bool hot = !busy && IsHot(i);
            float t = KnobOf(d);
            bool on = t > 0.5f;

            Ui.FillRound(g, hot ? theme.CardHover : theme.Card, r, P(6));
            Ui.StrokeRound(g, theme.CardBorder, 1, r, P(6));
            if (showFocus && focusIndex == i) Ui.StrokeRound(g, theme.Text, Math.Max(2f, 2 * scale), r, P(6));

            DrawGlyph(g, new RectangleF(r.X + P(12), r.Y + (r.Height - P(40)) / 2f, P(40), P(40)), d, on);

            var sw = switchRects[i];
            int textX = r.X + P(64), textW = sw.X - P(48) - textX;
            const TF line = TF.Left | TF.VerticalCenter | TF.SingleLine | TF.EndEllipsis | TF.NoPrefix | TF.NoPadding;
            TextRenderer.DrawText(g, d.Name, fontBody, new Rectangle(textX, r.Y + P(12), textW, P(20)),
                on ? theme.Text : theme.TextSecondary, line);
            TextRenderer.DrawText(g, Detail(d), fontSmall, new Rectangle(textX, r.Y + P(33), textW, P(18)),
                theme.TextSecondary, line);
            TextRenderer.DrawText(g, on ? "On" : "Off", fontBody, new Rectangle(sw.X - P(44), r.Y, P(36), r.Height),
                theme.Text, TF.Right | TF.VerticalCenter | TF.SingleLine | TF.NoPadding);

            DrawSwitch(g, sw, t, hot, !busy || IsPending(d));
        }

        /// <summary>Windows 11 style toggle switch; t animates 0 (off) to 1 (on).</summary>
        void DrawSwitch(Graphics g, Rectangle r, float t, bool hot, bool enabled)
        {
            float alpha = enabled ? 1f : 0.45f;
            var pill = new RectangleF(r.X, r.Y, r.Width, r.Height);
            float rad = pill.Height / 2;
            if (t < 1f) Ui.StrokeRound(g, Ui.Fade(theme.ToggleOff, (1 - t) * alpha), Math.Max(1f, scale), pill, rad);
            if (t > 0f) Ui.FillRound(g, Ui.Fade(theme.Accent, t * alpha), pill, rad);

            float dia = (hot && enabled ? 14 : 12) * scale;
            float cx = pill.X + rad + t * (pill.Width - 2 * rad), cy = pill.Y + rad;
            using (var b = new SolidBrush(Ui.Fade(Ui.Blend(theme.ToggleOff, theme.OnAccent, t), alpha)))
                g.FillEllipse(b, cx - dia / 2, cy - dia / 2, dia, dia);
        }

        /// <summary>Little monitor (or laptop) icon drawn in the display's real orientation.</summary>
        void DrawGlyph(Graphics g, RectangleF box, DisplayInfo d, bool on)
        {
            float s = scale, cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
            Color stroke = on ? theme.Text : theme.TextTertiary;
            float sw = Math.Max(1f, 1.5f * s);
            bool builtIn = d.Connector == "Built-in";
            bool portrait = d.LayoutRect.Height > d.LayoutRect.Width;

            RectangleF screen;
            if (builtIn) screen = new RectangleF(cx - 11 * s, cy - 9 * s, 22 * s, 14 * s);
            else if (portrait) screen = new RectangleF(cx - 7.5f * s, cy - 14 * s, 15 * s, 22 * s);
            else screen = new RectangleF(cx - 13 * s, cy - 11 * s, 26 * s, 17 * s);

            if (on) Ui.FillRound(g, Ui.Fade(theme.Accent, 0.35f), screen, 2.5f * s);
            Ui.StrokeRound(g, stroke, sw, screen, 2.5f * s);
            using (var pen = new Pen(stroke, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float b = screen.Bottom;
                if (builtIn)
                {
                    g.DrawLine(pen, cx - 14 * s, b + 2.5f * s, cx + 14 * s, b + 2.5f * s);
                }
                else
                {
                    g.DrawLine(pen, cx, b, cx, b + 4 * s);
                    g.DrawLine(pen, cx - 6 * s, b + 4 * s, cx + 6 * s, b + 4 * s);
                }
            }
        }

        void DrawButton(Graphics g, Rectangle r, string text, Hit hit, bool enabled)
        {
            Ui.FillRound(g, enabled && hoverHit == hit ? theme.ButtonHover : theme.Button, r, P(4));
            Ui.StrokeRound(g, theme.ButtonBorder, 1, r, P(4));
            TextRenderer.DrawText(g, text, fontBody, r, enabled ? theme.Text : theme.TextTertiary,
                TF.HorizontalCenter | TF.VerticalCenter | TF.SingleLine | TF.NoPrefix);
        }

        // ------------------------------------------------------------------ input

        void HitTest(Point p, out Hit hit, out int index)
        {
            index = -1;
            hit = Hit.None;
            if (closeRect.Contains(p)) { hit = Hit.Close; return; }
            for (int i = 0; i < mapRects.Count; i++)
                if (mapRects[i].Contains(p)) { hit = Hit.Map; index = i; return; }
            for (int i = 0; i < rowRects.Count; i++)
                if (rowRects[i].Contains(p)) { hit = Hit.Row; index = i; return; }
            if (identifyRect.Contains(p)) hit = Hit.Identify;
            else if (allOnRect.Contains(p)) hit = Hit.AllOn;
            else if (settingsRect.Contains(p)) hit = Hit.Settings;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Hit hit;
            int index;
            HitTest(e.Location, out hit, out index);
            if (hit != hoverHit || index != hoverIndex)
            {
                hoverHit = hit;
                hoverIndex = index;
                showFocus = false;
                Cursor = hit == Hit.None ? Cursors.Default : Cursors.Hand;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverHit = Hit.None;
            hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Hit hit;
            int index;
            HitTest(e.Location, out hit, out index);
            switch (hit)
            {
                case Hit.Close: Close(); break;
                case Hit.Map:
                case Hit.Row: Toggle(index); break;
                case Hit.Identify: if (!busy) Identify(); break;
                case Hit.AllOn: TurnAllOn(); break;
                case Hit.Settings: OpenSettings(); break;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    Close();
                    return true;
                case Keys.Down:
                case Keys.Tab:
                    MoveFocus(1);
                    return true;
                case Keys.Up:
                case Keys.Shift | Keys.Tab:
                    MoveFocus(-1);
                    return true;
                case Keys.Space:
                case Keys.Enter:
                    if (focusIndex >= 0) Toggle(focusIndex);
                    return true;
                case Keys.A:
                    TurnAllOn();
                    return true;
                case Keys.I:
                    if (!busy) Identify();
                    return true;
            }
            // 1-9 toggles that display (numbered left to right).
            if (keyData >= Keys.D1 && keyData <= Keys.D9) { Toggle(keyData - Keys.D1); return true; }
            if (keyData >= Keys.NumPad1 && keyData <= Keys.NumPad9) { Toggle(keyData - Keys.NumPad1); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void MoveFocus(int delta)
        {
            if (displays.Count == 0) return;
            showFocus = true;
            focusIndex = focusIndex < 0
                ? (delta > 0 ? 0 : displays.Count - 1)
                : (focusIndex + delta + displays.Count) % displays.Count;
            Invalidate();
        }

        // ------------------------------------------------------------------ window behavior

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Behave like a flyout: clicking anywhere else closes it (but not mid-change).
            if (!busy && DateTime.Now >= stayOpenUntil) Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                // Never quit halfway through a display change.
                closeWhenDone = true;
                e.Cancel = true;
                return;
            }
            CloseOverlays();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == N.WM_DPICHANGED)
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                var suggested = (N.RECT)Marshal.PtrToStructure(m.LParam, typeof(N.RECT));
                SetScale(dpi / 96f);
                var size = Relayout();
                Bounds = new Rectangle(suggested.Left, suggested.Top, size.Width, size.Height);
                Invalidate();
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == N.WM_DISPLAYCHANGE && !busy)
            {
                // Something else changed the displays (cable plugged in, Settings, Win+P): refresh.
                BeginInvoke(new Action(() =>
                {
                    if (busy || IsDisposed) return;
                    LoadDisplays();
                    foreach (var d in displays) knob[d.Key] = d.Active ? 1f : 0f;
                    PlaceWindow();
                    Invalidate();
                }));
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                anim.Dispose();
                foreach (var f in new[] { fontTitle, fontBody, fontSmall, fontTiny }) if (f != null) f.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
