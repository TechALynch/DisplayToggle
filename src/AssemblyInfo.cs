// Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;
using System.Runtime.InteropServices;

// Shown in the .exe's Properties > Details tab.
[assembly: AssemblyTitle("Display Toggle")]
[assembly: AssemblyDescription("One-click on/off switches for every connected display.")]
[assembly: AssemblyCompany("AJACE Holdings LLC")]
[assembly: AssemblyProduct("Display Toggle")]
[assembly: AssemblyCopyright("Copyright (c) 2026 AJACE Holdings LLC")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: ComVisible(false)]

// Load Windows DLLs (user32, dwmapi, shcore) only from System32, never from the folder the .exe sits in,
// so a planted DLL next to a downloaded copy can't be loaded instead.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
