using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ventoy2Disk.NET
{
    public class DiskInfo
    {
        public string Path { get; set; } = "";
        public string Model { get; set; } = "";
        public ulong Size { get; set; }
        public bool IsRemovable { get; set; }

        public override string ToString()
        {
            double gb = Size / (1024.0 * 1024.0 * 1024.0);
            return $"{Path} - {Model} ({gb:F2} GB) {(IsRemovable ? "[Removable]" : "")}";
        }
    }

    public static class DiskService
    {
        #region Windows API Imports
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        private const uint FSCTL_LOCK_VOLUME = 0x00090018;
        private const uint FSCTL_UNLOCK_VOLUME = 0x0009001c;
        private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        private const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x0007c01c;
        private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700a0;

        [StructLayout(LayoutKind.Sequential)]
        private struct DISK_GEOMETRY
        {
            public long Cylinders;
            public int MediaType;
            public uint TracksPerCylinder;
            public uint SectorsPerTrack;
            public uint BytesPerSector;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISK_GEOMETRY_EX
        {
            public DISK_GEOMETRY Geometry;
            public long DiskSize;
            public byte Data;
        }
        #endregion

        public static List<DiskInfo> ListDisks()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return ListDisksLinux();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ListDisksWindows();
            }
            return new List<DiskInfo>();
        }

        private static List<DiskInfo> ListDisksLinux()
        {
            var list = new List<DiskInfo>();
            if (!Directory.Exists("/sys/block")) return list;

            foreach (var dir in Directory.GetDirectories("/sys/block"))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("loop") || name.StartsWith("ram") || name.StartsWith("dm-"))
                    continue;

                string sizePath = Path.Combine(dir, "size");
                if (!File.Exists(sizePath)) continue;

                if (ulong.TryParse(File.ReadAllText(sizePath).Trim(), out var sectors))
                {
                    ulong sizeInBytes = sectors * 512;

                    string model = "";
                    string vendor = "";
                    string modelPath = Path.Combine(dir, "device", "model");
                    string vendorPath = Path.Combine(dir, "device", "vendor");

                    if (File.Exists(modelPath)) model = File.ReadAllText(modelPath).Trim();
                    if (File.Exists(vendorPath)) vendor = File.ReadAllText(vendorPath).Trim();

                    string nameStr = $"{vendor} {model}".Trim();
                    if (string.IsNullOrEmpty(nameStr)) nameStr = name;

                    bool isRemovable = false;
                    string remPath = Path.Combine(dir, "removable");
                    if (File.Exists(remPath))
                    {
                        isRemovable = File.ReadAllText(remPath).Trim() == "1";
                    }

                    list.Add(new DiskInfo
                    {
                        Path = "/dev/" + name,
                        Model = nameStr,
                        Size = sizeInBytes,
                        IsRemovable = isRemovable
                    });
                }
            }
            return list;
        }

        private static List<DiskInfo> ListDisksWindows()
        {
            var list = new List<DiskInfo>();
            for (int i = 0; i < 16; i++)
            {
                string path = $@"\\.\PhysicalDrive{i}";
                using (var handle = CreateFile(
                    path,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero))
                {
                    if (handle.IsInvalid) continue;

                    int size = Marshal.SizeOf<DISK_GEOMETRY_EX>() + 128;
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (DeviceIoControl(
                            handle,
                            IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                            IntPtr.Zero,
                            0,
                            buffer,
                            (uint)size,
                            out _,
                            IntPtr.Zero))
                        {
                            var geomEx = Marshal.PtrToStructure<DISK_GEOMETRY_EX>(buffer);
                            bool isRemovable = geomEx.Geometry.MediaType == 11 || geomEx.Geometry.MediaType == 12;

                            list.Add(new DiskInfo
                            {
                                Path = path,
                                Model = $"Physical Drive {i}",
                                Size = (ulong)geomEx.DiskSize,
                                IsRemovable = isRemovable
                            });
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            return list;
        }

        public static Stream OpenDriveStream(string path, bool writeAccess)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new FileStream(
                    path,
                    FileMode.Open,
                    writeAccess ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                uint access = GENERIC_READ;
                if (writeAccess) access |= GENERIC_WRITE;

                var handle = CreateFile(
                    path,
                    access,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to open physical drive {path}. Win32 Error: {err}");
                }

                if (writeAccess)
                {
                    DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                }

                return new FileStream(handle, writeAccess ? FileAccess.ReadWrite : FileAccess.Read);
            }
            throw new PlatformNotSupportedException();
        }

        public static void UpdateProperties(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using (var handle = CreateFile(
                    path,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero))
                {
                    if (!handle.IsInvalid)
                    {
                        DeviceIoControl(handle, FSCTL_UNLOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                        DeviceIoControl(handle, IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    }
                }
            }
        }
    }
}
