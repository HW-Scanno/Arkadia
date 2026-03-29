using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Arkadia.Startup;

/// <summary>
/// Fires a single .wav file asynchronously at startup.
/// <para>
/// Implementation: Windows-only via <c>winmm.dll PlaySound</c> (SND_ASYNC | SND_FILENAME).
/// On non-Windows platforms this is intentionally a no-op — Avalonia is cross-platform
/// but .NET does not ship a cross-platform .wav player in the BCL.
/// A cross-platform audio library can be plugged in here when needed.
/// </para>
/// Sound playback failure never blocks startup.
/// </summary>
internal static class StartupSoundPlayer
{
    public static void TryPlay(string? soundPath)
    {
        if (string.IsNullOrEmpty(soundPath) || !File.Exists(soundPath))
            return;

        try
        {
            if (OperatingSystem.IsWindows())
                PlayWindows(soundPath);
        }
        catch
        {
            // Sound failure must never block startup
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PlayWindows(string path) =>
        NativeMethods.PlaySound(path, nint.Zero, NativeMethods.SndAsync | NativeMethods.SndFilename);

    private static class NativeMethods
    {
        internal const uint SndAsync    = 0x0001;
        internal const uint SndFilename = 0x00020000;

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        [SupportedOSPlatform("windows")]
        internal static extern bool PlaySound(string lpszName, nint hModule, uint dwFlags);
    }
}
