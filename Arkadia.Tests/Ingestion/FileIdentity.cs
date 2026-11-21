using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// NTFS file identity (volume serial + file index). A native rename keeps it; Copy+Delete yields a new one — so
/// tests can prove "same filesystem = File.Move only" without timing or content heuristics.
/// </summary>
internal static class FileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    public static (uint Volume, ulong Index) Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(fs.SafeFileHandle, out var i))
            throw new IOException("GetFileInformationByHandle failed", Marshal.GetLastWin32Error());
        return (i.VolumeSerialNumber, ((ulong)i.FileIndexHigh << 32) | i.FileIndexLow);
    }
}
