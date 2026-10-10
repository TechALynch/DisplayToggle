// Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DisplayToggle.Installer
{
    /// <summary>
    /// Per-user installer: no admin rights. DisplayToggle.exe is embedded in this file as a resource.
    ///
    ///   DisplayToggle-Setup.exe               install (or update), with prompts
    ///   DisplayToggle-Setup.exe /S            install silently
    ///   Uninstall.exe /uninstall [/S]         remove (this file is copied into the install folder as Uninstall.exe)
    /// </summary>
    internal static class Setup
    {
        const string AppName = "Display Toggle";
        const string Publisher = "AJACE Holdings LLC";
        const string ExeName = "DisplayToggle.exe";
        const string UninstallerName = "Uninstall.exe";
        const string ShortcutName = "Display Toggle.lnk";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AJACEHoldings.DisplayToggle";

        static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DisplayToggle");
        static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplayToggle");
        static readonly string DesktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);
        static readonly string StartMenuShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);

        static string Target { get { return Path.Combine(InstallDir, ExeName); } }

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = HasArg(args, "/S") || HasArg(args, "/silent");
            bool uninstall = HasArg(args, "/uninstall");
            Application.EnableVisualStyles();
            try
            {
                return uninstall ? Uninstall(silent) : Install(silent);
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show("Something went wrong:\n\n" + ex.Message, AppName + " Setup",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ------------------------------------------------------------------ install

        static int Install(bool silent)
        {
            string version = EmbeddedVersion();
            bool update = File.Exists(Target);
            if (!silent)
            {
                string prompt = update
                    ? "Update " + AppName + " to version " + version + "?"
                    : "Install " + AppName + " " + version + " by " + Publisher + "?\n\n" +
                      "It installs for your Windows account only (no admin rights needed) and adds a " +
                      AppName + " icon to your desktop and Start menu.";
                if (Ask(prompt, MessageBoxIcon.Question) != DialogResult.Yes) return 2;
            }

            StopRunningCopies();
            Directory.CreateDirectory(InstallDir);

            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeName))
            using (var file = File.Create(Target))
                res.CopyTo(file);

            string self = Assembly.GetExecutingAssembly().Location;
            string uninstaller = Path.Combine(InstallDir, UninstallerName);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstaller, true);

            CreateShortcut(DesktopShortcut);
            CreateShortcut(StartMenuShortcut);
            Register(version, uninstaller);

            if (!silent &&
                Ask(AppName + " is " + (update ? "updated" : "installed") +
                    ".\n\nDouble-click the " + AppName + " icon on your desktop any time. Open it now?",
                    MessageBoxIcon.Information) == DialogResult.Yes)
            {
                Process.Start(Target);
            }
            return 0;
        }

        static string EmbeddedVersion()
        {
            // The installer and the app are built from the same AssemblyInfo.cs, so their versions match.
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }

        static void CreateShortcut(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic lnk = ((dynamic)shell).CreateShortcut(path);
                lnk.TargetPath = Target;
                lnk.WorkingDirectory = InstallDir;
                lnk.IconLocation = Target + ",0";
                lnk.Description = "Turn displays on or off";
                lnk.Save();
                Marshal.FinalReleaseComObject(lnk);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        /// <summary>Adds the entry shown in Settings › Apps › Installed apps.</summary>
        static void Register(string version, string uninstaller)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", Publisher);
                key.SetValue("DisplayIcon", Target + ",0");
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /S");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long bytes = new DirectoryInfo(InstallDir).GetFiles().Sum(f => f.Length);
                key.SetValue("EstimatedSize", (int)(bytes / 1024) + 1, RegistryValueKind.DWord);
            }
        }

        // ------------------------------------------------------------------ uninstall

        static int Uninstall(bool silent)
        {
            if (!silent && Ask("Remove " + AppName + " from this PC?", MessageBoxIcon.Question) != DialogResult.Yes)
                return 2;

            StopRunningCopies();
            TryDelete(DesktopShortcut);
            TryDelete(StartMenuShortcut);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            TryDelete(Target);
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch { }

            // This program is usually running from the folder being removed, so finish the job
            // from a short-lived hidden command once it has exited.
            var psi = new ProcessStartInfo("cmd.exe",
                "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + InstallDir + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);

            if (!silent)
                MessageBox.Show(AppName + " has been removed.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // ------------------------------------------------------------------ helpers

        static void StopRunningCopies()
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, Target, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
            }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static DialogResult Ask(string text, MessageBoxIcon icon)
        {
            return MessageBox.Show(text, AppName + " Setup", MessageBoxButtons.YesNo, icon);
        }

        static bool HasArg(string[] args, string name)
        {
            return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
