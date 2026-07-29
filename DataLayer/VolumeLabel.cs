using System.Runtime.InteropServices;

namespace Arkadia.Data;

/// <summary>
/// Thin wrapper around the Windows SetVolumeLabel API.
/// Only called during disk initialization on the user's explicit request.
/// </summary>
internal static class VolumeLabel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetVolumeLabel(string lpRootPathName, string? lpVolumeName);

    /// <summary>
    /// Sets the filesystem volume label at <paramref name="rootPath"/> (e.g. "D:\").
    /// Returns true on success. On failure the Win32 last error is available via
    /// <see cref="Marshal.GetLastWin32Error"/>.
    /// </summary>
    public static bool TrySet(string rootPath, string label)
        => SetVolumeLabel(rootPath, label);
}
