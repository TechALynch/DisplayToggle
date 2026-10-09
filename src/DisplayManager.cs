// Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using N = DisplayToggle.NativeMethods;

namespace DisplayToggle
{
    internal sealed class DisplayInfo
    {
        public N.LUID AdapterId;
        public uint TargetId;
        public string Name;
        public string DevicePath;
        public uint OutputTechnology;
        public bool Active;
        public bool Primary;
        public Rectangle Bounds;      // live desktop rect (physical pixels) when active
        public uint Rotation;
        public double RefreshHz;
        public string GdiName;
        public Rectangle LayoutRect;  // where the display sits in the full arrangement (remembered when off)
        public string Position;       // "Left", "Middle", "Right", ...

        public string Key
        {
            get { return string.IsNullOrEmpty(DevicePath) ? DisplayManager.TargetKey(AdapterId, TargetId) : DevicePath; }
        }

        public string Connector
        {
            get { return DisplayManager.ConnectorName(OutputTechnology); }
        }
    }

    internal static class DisplayManager
    {
        /// <summary>Positions computed by the last Disable call (for --diagnose).</summary>
        internal static string LastPlan;

        // ------------------------------------------------------------------ queries

        public static List<DisplayInfo> GetDisplays()
        {
            N.DISPLAYCONFIG_PATH_INFO[] paths;
            N.DISPLAYCONFIG_MODE_INFO[] modes;
            int rc = Query(N.QDC_ALL_PATHS, out paths, out modes);
            if (rc != 0) throw new Win32Exception(rc);

            var list = new List<DisplayInfo>();
            var byTarget = new Dictionary<string, DisplayInfo>();
            foreach (var p in paths)
            {
                if (p.targetInfo.targetAvailable == 0) continue; // nothing plugged into this output

                string tk = TargetKey(p.targetInfo.adapterId, p.targetInfo.id);
                DisplayInfo d;
                if (!byTarget.TryGetValue(tk, out d))
                {
                    d = new DisplayInfo();
                    d.AdapterId = p.targetInfo.adapterId;
                    d.TargetId = p.targetInfo.id;
                    d.OutputTechnology = p.targetInfo.outputTechnology;
                    var tn = GetTargetName(d.AdapterId, d.TargetId);
                    if (tn.HasValue)
                    {
                        d.Name = tn.Value.monitorFriendlyDeviceName;
                        d.DevicePath = tn.Value.monitorDevicePath;
                        d.OutputTechnology = tn.Value.outputTechnology;
                    }
                    if (string.IsNullOrWhiteSpace(d.Name))
                        d.Name = ConnectorName(d.OutputTechnology) == "Built-in" ? "Built-in display" : "Display";
                    byTarget[tk] = d;
                    list.Add(d);
                }

                if ((p.flags & N.DISPLAYCONFIG_PATH_ACTIVE) != 0 && !d.Active)
                {
                    d.Active = true;
                    d.Rotation = p.targetInfo.rotation;
                    var rr = p.targetInfo.refreshRate;
                    d.RefreshHz = rr.Denominator == 0 ? 0 : (double)rr.Numerator / rr.Denominator;
                    d.GdiName = GetSourceName(p.sourceInfo.adapterId, p.sourceInfo.id);
                    var b = DesktopRect(p, modes);
                    if (b.HasValue)
                    {
                        d.Bounds = b.Value;
                        d.Primary = b.Value.X == 0 && b.Value.Y == 0;
                    }
                }
            }

            LayoutStore.Apply(list);
            return list.OrderBy(d => d.LayoutRect.Left + d.LayoutRect.Width / 2)
                       .ThenBy(d => d.LayoutRect.Top).ToList();
        }

        // ------------------------------------------------------------------ turn off

        /// <summary>Turns one display off. Returns null on success, otherwise a message.</summary>
        public static string Disable(DisplayInfo d, bool validateOnly)
        {
            N.DISPLAYCONFIG_PATH_INFO[] paths;
            N.DISPLAYCONFIG_MODE_INFO[] modes;
            int rc = Query(N.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
            if (rc != 0) return Describe(rc);

            var keep = new List<N.DISPLAYCONFIG_PATH_INFO>();
            var removed = new List<N.DISPLAYCONFIG_PATH_INFO>();
            foreach (var p in paths)
            {
                if (IsTarget(p, d.AdapterId, d.TargetId)) removed.Add(p);
                else keep.Add(p);
            }
            if (removed.Count == 0) return null; // already off
            if (keep.Count == 0) return "That's your only display that's on, so it can't be turned off.";

            // The rectangle the display occupied, unless another (cloned) display still shows the same desktop.
            var keptSources = new HashSet<string>(keep.Select(p => SourceKey(p.sourceInfo)));
            Rectangle? hole = null;
            foreach (var r in removed)
            {
                if (!keptSources.Contains(SourceKey(r.sourceInfo)))
                    hole = DesktopRect(r, modes) ?? hole;
            }

            var newModes = new List<N.DISPLAYCONFIG_MODE_INFO>();
            var remap = new Dictionary<uint, uint>();
            for (int i = 0; i < keep.Count; i++)
            {
                var p = keep[i];
                p.sourceInfo.modeInfoIdx = Remap(p.sourceInfo.modeInfoIdx, modes, newModes, remap);
                p.targetInfo.modeInfoIdx = Remap(p.targetInfo.modeInfoIdx, modes, newModes, remap);
                keep[i] = p;
            }
            var arr = newModes.ToArray();

            // Close the gap the display leaves behind and keep a main display at (0,0).
            var srcIdx = new List<uint>();
            var rects = new List<Rectangle>();
            foreach (var p in keep)
            {
                var rect = DesktopRect(p, arr);
                if (!rect.HasValue || srcIdx.Contains(p.sourceInfo.modeInfoIdx)) continue;
                srcIdx.Add(p.sourceInfo.modeInfoIdx);
                rects.Add(rect.Value);
            }
            int oldPrimary = rects.FindIndex(r => r.X == 0 && r.Y == 0);
            if (hole.HasValue) CloseGap(rects, hole.Value);
            NormalizeOrigin(rects, oldPrimary);
            LastPlan = string.Join("  ", rects.Select(r => r.X + "," + r.Y + " " + r.Width + "x" + r.Height));
            for (int k = 0; k < srcIdx.Count; k++)
            {
                arr[srcIdx[k]].sourceMode.position.x = rects[k].X;
                arr[srcIdx[k]].sourceMode.position.y = rects[k].Y;
            }

            uint flags = N.SDC_USE_SUPPLIED_DISPLAY_CONFIG | N.SDC_ALLOW_CHANGES |
                         (validateOnly ? N.SDC_VALIDATE : N.SDC_APPLY | N.SDC_SAVE_TO_DATABASE);
            rc = N.SetDisplayConfig((uint)keep.Count, keep.ToArray(), (uint)arr.Length, arr, flags);
            Log.Write("disable " + d.Name + " [" + d.Key + "] validate=" + validateOnly + " rc=" + rc);
            return rc == 0 ? null : Describe(rc);
        }

        // ------------------------------------------------------------------ turn on

        /// <summary>Turns one display back on. Returns null on success, otherwise a message.</summary>
        public static string Enable(DisplayInfo d, bool validateOnly)
        {
            return EnableTargets(new List<DisplayInfo> { d }, validateOnly);
        }

        /// <summary>Turns every connected display on.</summary>
        public static string EnableAll(bool validateOnly)
        {
            var off = GetDisplays().Where(x => !x.Active).ToList();
            if (off.Count == 0) return null;
            string err = EnableTargets(off, validateOnly);
            if (err == null) return null;

            // Same thing Win+P > Extend does.
            int rc = N.SetDisplayConfig(0, null, 0, null,
                N.SDC_TOPOLOGY_EXTEND | (validateOnly ? N.SDC_VALIDATE : N.SDC_APPLY));
            Log.Write("enable-all extend validate=" + validateOnly + " rc=" + rc);
            return rc == 0 ? null : err;
        }

        static string EnableTargets(List<DisplayInfo> targets, bool validateOnly)
        {
            N.DISPLAYCONFIG_PATH_INFO[] all;
            N.DISPLAYCONFIG_MODE_INFO[] modes;
            int rc = Query(N.QDC_ALL_PATHS, out all, out modes);
            if (rc != 0) return Describe(rc);

            var active = all.Where(p => (p.flags & N.DISPLAYCONFIG_PATH_ACTIVE) != 0).ToList();
            var used = new HashSet<string>(active.Select(p => SourceKey(p.sourceInfo)));
            var extra = new List<N.DISPLAYCONFIG_PATH_INFO>();
            var snapshot = LayoutStore.Load();

            foreach (var d in targets)
            {
                if (active.Any(p => IsTarget(p, d.AdapterId, d.TargetId))) continue; // already on

                // Every output can be driven by any free "source" on the same graphics card; take the first free one.
                bool found = false;
                foreach (var p in all)
                {
                    if (!IsTarget(p, d.AdapterId, d.TargetId) || p.targetInfo.targetAvailable == 0) continue;
                    if ((p.flags & N.DISPLAYCONFIG_PATH_ACTIVE) != 0 || used.Contains(SourceKey(p.sourceInfo))) continue;

                    var x = WithoutModes(p);
                    x.flags |= N.DISPLAYCONFIG_PATH_ACTIVE;
                    LayoutStore.Entry e;
                    if (snapshot.TryGetValue(d.Key, out e) && e.Rotation >= 1 && e.Rotation <= 4)
                        x.targetInfo.rotation = e.Rotation;
                    extra.Add(x);
                    used.Add(SourceKey(p.sourceInfo));
                    found = true;
                    break;
                }
                if (!found)
                    return "Windows has no free video output for " + d.Name +
                           ". Your graphics card may be at its limit for displays that are on at the same time.";
            }
            if (extra.Count == 0) return null;

            // 1) Ask Windows to restore this exact combination of displays from its own saved layouts.
            //    This brings back resolution, rotation, refresh rate and position exactly as you had them.
            var topo = active.Select(p => WithoutModes(p)).Concat(extra).ToArray();
            rc = N.SetDisplayConfig((uint)topo.Length, topo, 0, null,
                N.SDC_TOPOLOGY_SUPPLIED | N.SDC_ALLOW_PATH_ORDER_CHANGES |
                (validateOnly ? N.SDC_VALIDATE : N.SDC_APPLY));
            Log.Write("enable(topology) " + string.Join(", ", targets.Select(t => t.Name)) +
                      " validate=" + validateOnly + " rc=" + rc);
            if (rc == 0)
            {
                if (!validateOnly && LayoutLooksBroken()) ApplyRememberedLayout();
                return null;
            }

            // 2) No saved layout for this combination: keep the current displays as they are and
            //    let Windows pick a mode for the new one, then move it to where it used to be.
            var newModes = new List<N.DISPLAYCONFIG_MODE_INFO>();
            var remap = new Dictionary<uint, uint>();
            var sup = new List<N.DISPLAYCONFIG_PATH_INFO>();
            foreach (var p in active)
            {
                var q = p;
                q.sourceInfo.modeInfoIdx = Remap(q.sourceInfo.modeInfoIdx, modes, newModes, remap);
                q.targetInfo.modeInfoIdx = Remap(q.targetInfo.modeInfoIdx, modes, newModes, remap);
                sup.Add(q);
            }
            sup.AddRange(extra);
            rc = N.SetDisplayConfig((uint)sup.Count, sup.ToArray(), (uint)newModes.Count, newModes.ToArray(),
                N.SDC_USE_SUPPLIED_DISPLAY_CONFIG | N.SDC_ALLOW_CHANGES |
                (validateOnly ? N.SDC_VALIDATE : N.SDC_APPLY | N.SDC_SAVE_TO_DATABASE));
            Log.Write("enable(supplied) validate=" + validateOnly + " rc=" + rc);
            if (rc != 0) return Describe(rc);

            if (!validateOnly) ApplyRememberedLayout();
            return null;
        }

        // ------------------------------------------------------------------ layout fix-ups

        static bool LayoutLooksBroken()
        {
            N.DISPLAYCONFIG_PATH_INFO[] paths;
            N.DISPLAYCONFIG_MODE_INFO[] modes;
            if (Query(N.QDC_ONLY_ACTIVE_PATHS, out paths, out modes) != 0) return false;
            var rects = paths.GroupBy(p => p.sourceInfo.modeInfoIdx)
                .Select(g => DesktopRect(g.First(), modes)).Where(r => r.HasValue).Select(r => r.Value).ToList();
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                    if (rects[i].IntersectsWith(rects[j])) return true;
            return false;
        }

        /// <summary>
        /// Puts the displays that are on back where they sat in the remembered full arrangement,
        /// sliding neighbours over to fill the space of any that are off.
        /// </summary>
        static void ApplyRememberedLayout()
        {
            var snapshot = LayoutStore.Load();
            if (snapshot.Count == 0) return;

            N.DISPLAYCONFIG_PATH_INFO[] paths;
            N.DISPLAYCONFIG_MODE_INFO[] modes;
            if (Query(N.QDC_ONLY_ACTIVE_PATHS, out paths, out modes) != 0) return;

            var srcIdx = new List<uint>();
            var keys = new List<string>();
            var rects = new List<Rectangle>();
            foreach (var p in paths)
            {
                var live = DesktopRect(p, modes);
                if (!live.HasValue || srcIdx.Contains(p.sourceInfo.modeInfoIdx)) continue;
                var tn = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);
                string key = tn.HasValue && !string.IsNullOrEmpty(tn.Value.monitorDevicePath)
                    ? tn.Value.monitorDevicePath : TargetKey(p.targetInfo.adapterId, p.targetInfo.id);
                if (!snapshot.ContainsKey(key)) return; // a display we have no memory of, so leave Windows' choice alone
                var s = snapshot[key].Rect;
                srcIdx.Add(p.sourceInfo.modeInfoIdx);
                keys.Add(key);
                rects.Add(new Rectangle(s.X, s.Y, live.Value.Width, live.Value.Height));
            }
            foreach (var gone in snapshot.Where(kv => !keys.Contains(kv.Key)).OrderByDescending(kv => kv.Value.Rect.X))
                CloseGap(rects, gone.Value.Rect);
            NormalizeOrigin(rects, keys.FindIndex(k => snapshot[k].Primary));

            for (int k = 0; k < srcIdx.Count; k++)
            {
                modes[srcIdx[k]].sourceMode.position.x = rects[k].X;
                modes[srcIdx[k]].sourceMode.position.y = rects[k].Y;
            }
            int rc = N.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                N.SDC_USE_SUPPLIED_DISPLAY_CONFIG | N.SDC_ALLOW_CHANGES | N.SDC_APPLY | N.SDC_SAVE_TO_DATABASE);
            Log.Write("apply remembered layout rc=" + rc);
        }

        /// <summary>Slides displays that sat right of (or below) a removed display over to close the gap.</summary>
        internal static void CloseGap(List<Rectangle> rects, Rectangle hole)
        {
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                bool sharesRow = r.Top < hole.Bottom && r.Bottom > hole.Top;
                bool sharesColumn = r.Left < hole.Right && r.Right > hole.Left;
                if (sharesRow && r.Left >= hole.Right) r.X -= hole.Width;
                else if (sharesColumn && r.Top >= hole.Bottom) r.Y -= hole.Height;
                rects[i] = r;
            }
        }

        /// <summary>Windows needs one display at (0,0); that one is the main display.</summary>
        internal static void NormalizeOrigin(List<Rectangle> rects, int preferred)
        {
            if (rects.Count == 0 || rects.Any(r => r.X == 0 && r.Y == 0)) return;
            int pick = preferred;
            if (pick < 0 || pick >= rects.Count)
            {
                // The old main display went away: promote the biggest remaining one.
                pick = Enumerable.Range(0, rects.Count)
                    .OrderByDescending(i => (long)rects[i].Width * rects[i].Height)
                    .ThenBy(i => DistanceToOrigin(rects[i]))
                    .First();
            }
            int dx = rects[pick].X, dy = rects[pick].Y;
            for (int i = 0; i < rects.Count; i++) rects[i] = new Rectangle(rects[i].X - dx, rects[i].Y - dy, rects[i].Width, rects[i].Height);
        }

        static double DistanceToOrigin(Rectangle r)
        {
            double cx = Math.Max(r.Left, Math.Min(0, r.Right));
            double cy = Math.Max(r.Top, Math.Min(0, r.Bottom));
            return Math.Sqrt(cx * cx + cy * cy);
        }

        // ------------------------------------------------------------------ helpers

        internal static int Query(uint flags, out N.DISPLAYCONFIG_PATH_INFO[] paths, out N.DISPLAYCONFIG_MODE_INFO[] modes)
        {
            paths = new N.DISPLAYCONFIG_PATH_INFO[0];
            modes = new N.DISPLAYCONFIG_MODE_INFO[0];
            int rc = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint np, nm;
                rc = N.GetDisplayConfigBufferSizes(flags, out np, out nm);
                if (rc != 0) return rc;
                paths = new N.DISPLAYCONFIG_PATH_INFO[np];
                modes = new N.DISPLAYCONFIG_MODE_INFO[nm];
                rc = N.QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (rc == N.ERROR_INSUFFICIENT_BUFFER) continue; // config changed between calls
                if (rc != 0) return rc;
                Array.Resize(ref paths, (int)np);
                Array.Resize(ref modes, (int)nm);
                return 0;
            }
            return rc;
        }

        internal static N.DISPLAYCONFIG_TARGET_DEVICE_NAME? GetTargetName(N.LUID adapter, uint targetId)
        {
            var tn = new N.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            tn.header.type = N.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            tn.header.size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(N.DISPLAYCONFIG_TARGET_DEVICE_NAME));
            tn.header.adapterId = adapter;
            tn.header.id = targetId;
            if (N.DisplayConfigGetDeviceInfo(ref tn) != 0) return null;
            return tn;
        }

        static string GetSourceName(N.LUID adapter, uint sourceId)
        {
            var sn = new N.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            sn.header.type = N.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            sn.header.size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(N.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            sn.header.adapterId = adapter;
            sn.header.id = sourceId;
            return N.DisplayConfigGetDeviceInfo(ref sn) == 0 ? sn.viewGdiDeviceName : null;
        }

        static uint Remap(uint idx, N.DISPLAYCONFIG_MODE_INFO[] src, List<N.DISPLAYCONFIG_MODE_INFO> dst, Dictionary<uint, uint> map)
        {
            if (idx >= src.Length) return N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            uint n;
            if (!map.TryGetValue(idx, out n))
            {
                n = (uint)dst.Count;
                dst.Add(src[idx]);
                map[idx] = n;
            }
            return n;
        }

        static N.DISPLAYCONFIG_PATH_INFO WithoutModes(N.DISPLAYCONFIG_PATH_INFO p)
        {
            p.sourceInfo.modeInfoIdx = N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            p.targetInfo.modeInfoIdx = N.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            return p;
        }

        /// <summary>
        /// The area a path covers on the desktop. Windows reports the source size before rotation,
        /// so a portrait (90/270 degree) display needs its width and height swapped.
        /// </summary>
        static Rectangle? DesktopRect(N.DISPLAYCONFIG_PATH_INFO p, N.DISPLAYCONFIG_MODE_INFO[] modes)
        {
            uint mi = p.sourceInfo.modeInfoIdx;
            if (mi >= modes.Length || modes[mi].infoType != N.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) return null;
            var s = modes[mi].sourceMode;
            bool sideways = p.targetInfo.rotation == 2 || p.targetInfo.rotation == 4;
            int w = (int)(sideways ? s.height : s.width), h = (int)(sideways ? s.width : s.height);
            return new Rectangle(s.position.x, s.position.y, w, h);
        }

        static bool IsTarget(N.DISPLAYCONFIG_PATH_INFO p, N.LUID adapter, uint targetId)
        {
            return p.targetInfo.id == targetId &&
                   p.targetInfo.adapterId.LowPart == adapter.LowPart &&
                   p.targetInfo.adapterId.HighPart == adapter.HighPart;
        }

        internal static string TargetKey(N.LUID a, uint id)
        {
            return a.HighPart.ToString("X") + ":" + a.LowPart.ToString("X") + "/t" + id;
        }

        static string SourceKey(N.DISPLAYCONFIG_PATH_SOURCE_INFO s)
        {
            return s.adapterId.HighPart.ToString("X") + ":" + s.adapterId.LowPart.ToString("X") + "/s" + s.id;
        }

        static string Describe(int rc)
        {
            return "Windows rejected the change: " + new Win32Exception(rc).Message + " (error " + rc + ")";
        }

        internal static string ConnectorName(uint tech)
        {
            switch (tech)
            {
                case 0: return "VGA";
                case 4: return "DVI";
                case 5: return "HDMI";
                case 6: return "Built-in";
                case 10: return "DisplayPort";
                case 11: return "Built-in";
                case 15: return "Wireless";
                case 16: return "USB";
                case 17: return "Virtual";
                case 18: return "USB-C";
                case 0x80000000: return "Built-in";
                default: return "";
            }
        }

        /// <summary>Plain-text report used by --diagnose; validates (never applies) each possible change.</summary>
        internal static string Diagnose()
        {
            var sb = new StringBuilder();
            var displays = GetDisplays();
            foreach (var d in displays)
            {
                sb.AppendLine(string.Format("{0} | {1} | {2} | active={3} primary={4} | bounds={5} | layout={6} | rot={7} | {8:0.##}Hz | {9} | target={10}",
                    d.Name, d.Position, d.Connector, d.Active, d.Primary, d.Bounds, d.LayoutRect, d.Rotation,
                    d.RefreshHz, d.GdiName, TargetKey(d.AdapterId, d.TargetId)));
                sb.AppendLine("    " + d.DevicePath);
            }
            sb.AppendLine();
            foreach (var d in displays)
            {
                LastPlan = null;
                string r = d.Active ? Disable(d, true) : Enable(d, true);
                sb.AppendLine((d.Active ? "validate OFF " : "validate ON  ") + d.Name + " (" + d.Position + "): " + (r ?? "OK"));
                if (LastPlan != null) sb.AppendLine("    remaining layout: " + LastPlan);
            }
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------------- persistence

    /// <summary>
    /// Remembers where each display sat the last time they were all on, so the mini map stays stable
    /// and displays can be put back in the right place.
    /// </summary>
    internal static class LayoutStore
    {
        internal sealed class Entry
        {
            public Rectangle Rect;
            public bool Primary;
            public uint Rotation;
        }

        static string FilePath
        {
            get { return Path.Combine(Log.Dir, "layout.txt"); }
        }

        public static Dictionary<string, Entry> Load()
        {
            var map = new Dictionary<string, Entry>();
            try
            {
                if (!File.Exists(FilePath)) return map;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var f = line.Split('\t');
                    if (f.Length < 7) continue;
                    var e = new Entry();
                    e.Rect = new Rectangle(int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]));
                    e.Primary = f[5] == "1";
                    e.Rotation = uint.Parse(f[6]);
                    map[f[0]] = e;
                }
            }
            catch (Exception ex)
            {
                Log.Write("layout load failed: " + ex.Message);
            }
            return map;
        }

        static void Save(IEnumerable<DisplayInfo> displays)
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllLines(FilePath, displays.Select(d => string.Join("\t", new[] {
                    d.Key, d.Bounds.X.ToString(), d.Bounds.Y.ToString(), d.Bounds.Width.ToString(), d.Bounds.Height.ToString(),
                    d.Primary ? "1" : "0", d.Rotation.ToString() })).ToArray());
            }
            catch (Exception ex)
            {
                Log.Write("layout save failed: " + ex.Message);
            }
        }

        /// <summary>Fills in LayoutRect and Position for every display.</summary>
        public static void Apply(List<DisplayInfo> displays)
        {
            if (displays.Count == 0) return;

            if (displays.All(d => d.Active))
            {
                Save(displays);
                foreach (var d in displays) d.LayoutRect = d.Bounds;
            }
            else
            {
                var snap = Load();
                if (displays.All(d => snap.ContainsKey(d.Key)))
                {
                    foreach (var d in displays) d.LayoutRect = snap[d.Key].Rect;
                }
                else
                {
                    // No memory of the full arrangement yet: show the ones that are on, and park the rest to the right.
                    var on = displays.Where(d => d.Active).ToList();
                    int right = on.Count > 0 ? on.Max(d => d.Bounds.Right) : 0;
                    int top = on.Count > 0 ? on.Min(d => d.Bounds.Top) : 0;
                    foreach (var d in displays)
                    {
                        if (d.Active) { d.LayoutRect = d.Bounds; continue; }
                        Entry e;
                        Size sz = snap.TryGetValue(d.Key, out e) ? e.Rect.Size : new Size(1920, 1080);
                        d.LayoutRect = new Rectangle(right, top, sz.Width, sz.Height);
                        right += sz.Width;
                    }
                }
            }
            AssignPositions(displays);
        }

        static void AssignPositions(List<DisplayInfo> displays)
        {
            int n = displays.Count;
            if (n == 1) { displays[0].Position = ""; return; }

            int minX = displays.Min(d => d.LayoutRect.Left), maxX = displays.Max(d => d.LayoutRect.Right);
            int minY = displays.Min(d => d.LayoutRect.Top), maxY = displays.Max(d => d.LayoutRect.Bottom);
            bool horizontal = (maxX - minX) >= (maxY - minY);
            var ordered = horizontal
                ? displays.OrderBy(d => d.LayoutRect.Left + d.LayoutRect.Width / 2).ToList()
                : displays.OrderBy(d => d.LayoutRect.Top + d.LayoutRect.Height / 2).ToList();
            string first = horizontal ? "Left" : "Top", last = horizontal ? "Right" : "Bottom";
            for (int i = 0; i < n; i++)
            {
                string label;
                if (i == 0) label = first;
                else if (i == n - 1) label = last;
                else if (n == 3) label = "Middle";
                else label = Ordinal(i + 1) + " from " + first.ToLowerInvariant();
                ordered[i].Position = label;
            }
        }

        static string Ordinal(int k)
        {
            switch (k) { case 2: return "2nd"; case 3: return "3rd"; default: return k + "th"; }
        }
    }

    internal static class Log
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplayToggle");

        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, "log.txt");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch
            {
                // logging must never break the app
            }
        }
    }
}
