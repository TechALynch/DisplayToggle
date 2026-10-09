// Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DisplayToggle
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // --diagnose : writes a report of every display and checks (without applying) each on/off change.
            if (HasArg(args, "--diagnose"))
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(Path.Combine(Log.Dir, "diagnose.txt"), DisplayManager.Diagnose());
                return 0;
            }

            // --all-on : turns every connected display back on without opening the window.
            if (HasArg(args, "--all-on"))
            {
                string err = DisplayManager.EnableAll(false);
                if (err != null) MessageBox.Show(err, ToggleForm.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return err == null ? 0 : 1;
            }

            bool first;
            using (new Mutex(true, @"Local\DisplayToggle.SingleInstance", out first))
            {
                if (!first)
                {
                    IntPtr h = NativeMethods.FindWindow(null, ToggleForm.WindowTitle);
                    if (h != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(h);
                    }
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ToggleForm());
            }
            return 0;
        }

        static bool HasArg(string[] args, string name)
        {
            return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
