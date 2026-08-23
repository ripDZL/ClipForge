using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using LibVLCSharp.Shared;

namespace ClipForge;

public partial class App : Application
{
    // Excludes the current working directory and PATH from implicit native DLL
    // resolution while retaining the application directory, System32, and any
    // explicitly registered user DLL directories. ClipForge targets Windows 10+.
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable hardened native DLL search paths.");

        Core.Initialize();
        base.OnStartup(e);
    }
}
