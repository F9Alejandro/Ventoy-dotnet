using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Ventoy2DiskDotNet
{
    public class PhysicalDisk
    {
        public int Number { get; set; }
        public string Path { get; set; } = "";
        public string Model { get; set; } = "";
        public string Vendor { get; set; } = "";
        public ulong SizeInBytes { get; set; }
        public string SystemName { get; set; } = ""; // e.g. "sda" or "PhysicalDrive0"

        public override string ToString()
        {
            double gb = SizeInBytes / (1024.0 * 1024.0 * 1024.0);
            string vendorProd = string.IsNullOrEmpty(Vendor) ? Model : $"{Vendor} {Model}";
            return $"Disk #{Number}: {vendorProd} ({gb:F2} GB) [{SystemName}]";
        }
    }

    public static class DiskService
    {
        // Win32 Constants
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        private const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
        private const uint FSCTL_LOCK_VOLUME = 0x00090018;
        private const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
        private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // Win32 Structs
        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_DESCRIPTOR_HEADER
        {
            public uint Version;
            public uint Size;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr CreateFileA(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref STORAGE_PROPERTY_QUERY lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static List<PhysicalDisk> GetPhysicalDisks()
        {
            var list = new List<PhysicalDisk>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                list.AddRange(GetWindowsDisks());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                list.AddRange(GetLinuxDisks());
            }

            return list;
        }

        private static List<PhysicalDisk> GetWindowsDisks()
        {
            var list = new List<PhysicalDisk>();
            for (int i = 0; i < 32; i++)
            {
                string path = $@"\\.\PhysicalDrive{i}";
                IntPtr hDisk = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (hDisk == INVALID_HANDLE_VALUE)
                {
                    continue;
                }

                try
                {
                    // 1. Get length
                    ulong size = 0;
                    byte[] outBuf = new byte[8];
                    IntPtr outPtr = Marshal.AllocHGlobal(8);
                    try
                    {
                        if (DeviceIoControl(hDisk, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, outPtr, 8, out uint bytesReturned, IntPtr.Zero))
                        {
                            size = (ulong)Marshal.ReadInt64(outPtr);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(outPtr);
                    }

                    if (size == 0)
                        continue;

                    // 2. Query storage descriptor for vendor/model
                    string vendor = "";
                    string model = "";
                    var query = new STORAGE_PROPERTY_QUERY
                    {
                        PropertyId = 0, // StorageDeviceProperty
                        QueryType = 0   // PropertyStandardQuery
                    };

                    uint headerSize = (uint)Marshal.SizeOf<STORAGE_DESCRIPTOR_HEADER>();
                    IntPtr headerPtr = Marshal.AllocHGlobal((int)headerSize);
                    try
                    {
                        if (DeviceIoControl(hDisk, IOCTL_STORAGE_QUERY_PROPERTY, ref query, (uint)Marshal.SizeOf(query), headerPtr, headerSize, out uint bytesReturned, IntPtr.Zero))
                        {
                            var header = Marshal.PtrToStructure<STORAGE_DESCRIPTOR_HEADER>(headerPtr);
                            if (header.Size > 0)
                            {
                                IntPtr descPtr = Marshal.AllocHGlobal((int)header.Size);
                                try
                                {
                                    if (DeviceIoControl(hDisk, IOCTL_STORAGE_QUERY_PROPERTY, ref query, (uint)Marshal.SizeOf(query), descPtr, header.Size, out bytesReturned, IntPtr.Zero))
                                    {
                                        int vendorOffset = Marshal.ReadInt32(descPtr, 16); // VendorIdOffset is at byte 16
                                        int productOffset = Marshal.ReadInt32(descPtr, 20); // ProductIdOffset is at byte 20

                                        if (vendorOffset > 0)
                                        {
                                            vendor = Marshal.PtrToStringAnsi(descPtr + vendorOffset) ?? "";
                                            vendor = vendor.Trim();
                                        }
                                        if (productOffset > 0)
                                        {
                                            model = Marshal.PtrToStringAnsi(descPtr + productOffset) ?? "";
                                            model = model.Trim();
                                        }
                                    }
                                }
                                finally
                                {
                                    Marshal.FreeHGlobal(descPtr);
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(headerPtr);
                    }

                    list.Add(new PhysicalDisk
                    {
                        Number = i,
                        Path = path,
                        Model = string.IsNullOrEmpty(model) ? $"Physical Drive {i}" : model,
                        Vendor = vendor,
                        SizeInBytes = size,
                        SystemName = $"PhysicalDrive{i}"
                    });
                }
                catch
                {
                    // Ignore errors for individual disks
                }
                finally
                {
                    CloseHandle(hDisk);
                }
            }
            return list;
        }

        private static List<PhysicalDisk> GetLinuxDisks()
        {
            var list = new List<PhysicalDisk>();
            if (!Directory.Exists("/sys/block"))
                return list;

            int index = 0;
            foreach (var dir in Directory.GetDirectories("/sys/block"))
            {
                string name = Path.GetFileName(dir);
                // Exclude virtual/loop/ram devices
                if (name.StartsWith("loop") || name.StartsWith("dm-") || name.StartsWith("ram") || name.StartsWith("sr") || name.StartsWith("md"))
                    continue;

                string sizePath = Path.Combine(dir, "size");
                if (!File.Exists(sizePath))
                    continue;

                try
                {
                    string sizeStr = File.ReadAllText(sizePath).Trim();
                    if (!ulong.TryParse(sizeStr, out ulong sectors))
                        continue;

                    ulong sizeInBytes = sectors * 512;

                    string model = "";
                    string modelPath = Path.Combine(dir, "device/model");
                    if (File.Exists(modelPath))
                    {
                        model = File.ReadAllText(modelPath).Trim();
                    }

                    string vendor = "";
                    string vendorPath = Path.Combine(dir, "device/vendor");
                    if (File.Exists(vendorPath))
                    {
                        vendor = File.ReadAllText(vendorPath).Trim();
                    }

                    if (string.IsNullOrEmpty(model))
                    {
                        model = $"Linux Disk {name}";
                    }

                    list.Add(new PhysicalDisk
                    {
                        Number = index++,
                        Path = $"/dev/{name}",
                        Model = model,
                        Vendor = vendor,
                        SizeInBytes = sizeInBytes,
                        SystemName = name
                    });
                }
                catch
                {
                    // Ignore disk read error
                }
            }
            return list;
        }

        public static Stream OpenReadHandle(PhysicalDisk disk)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                IntPtr hDisk = CreateFileA(disk.Path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (hDisk == INVALID_HANDLE_VALUE)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to open physical drive {disk.Path} for read. Win32 Error: {err}");
                }
                var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(hDisk, true);
                return new FileStream(safeHandle, FileAccess.Read);
            }
            else
            {
                return File.Open(disk.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        }

        public static (string version, bool secureBoot) DetectVentoyVersion(PhysicalDisk disk)
        {
            try
            {
                using (var stream = OpenReadHandle(disk))
                {
                    byte[] sector0 = new byte[512];
                    if (stream.Read(sector0, 0, 512) != 512)
                        return ("", false);

                    if (sector0[510] != 0x55 || sector0[511] != 0xAA)
                        return ("", false);

                    bool isGpt = (sector0[446 + 4] == 0xEE);
                    ulong part2StartSector = 0;
                    if (!isGpt)
                    {
                        MbrHead mbr = MbrHead.Deserialize(sector0);
                        if (mbr.PartTbl[1].FsFlag == 0xEF)
                        {
                            part2StartSector = mbr.PartTbl[1].StartSectorId;
                        }
                    }
                    else
                    {
                        byte[] gptBytes = new byte[17408];
                        stream.Position = 0;
                        if (stream.Read(gptBytes, 0, 17408) != 17408)
                            return ("", false);

                        GptInfo gpt = new GptInfo();
                        gpt.Mbr = MbrHead.Deserialize(gptBytes);
                        gpt.Head = GptHeader.Deserialize(gptBytes.AsSpan(512, 512).ToArray());
                        for (int i = 0; i < 128; i++)
                        {
                            gpt.PartTbl[i] = GptPartEntry.Deserialize(gptBytes, 1024 + (i * 128));
                        }
                        part2StartSector = gpt.PartTbl[1].StartLBA;
                    }

                    if (part2StartSector == 0)
                        return ("", false);

                    // Read the VTOYEFI FAT image (32MB)
                    byte[] fatBytes = new byte[32 * 1024 * 1024];
                    stream.Position = (long)(part2StartSector * 512);
                    if (stream.Read(fatBytes, 0, fatBytes.Length) != fatBytes.Length)
                        return ("", false);

                    using (var fatStream = new MemoryStream(fatBytes))
                    using (var fs = new DiscUtils.Fat.FatFileSystem(fatStream))
                    {
                        string version = "";
                        bool secureBoot = false;

                        if (fs.FileExists(@"grub\grub.cfg"))
                        {
                            using (var reader = new StreamReader(fs.OpenFile(@"grub\grub.cfg", FileMode.Open, FileAccess.Read)))
                            {
                                string content = reader.ReadToEnd();
                                int index = content.IndexOf("VENTOY_VERSION=");
                                if (index >= 0)
                                {
                                    int start = index + "VENTOY_VERSION=".Length;
                                    if (start < content.Length && content[start] == '"') start++;
                                    int end = start;
                                    while (end < content.Length && content[end] != '"' && content[end] != '\r' && content[end] != '\n')
                                    {
                                        end++;
                                    }
                                    version = content.Substring(start, end - start);
                                }
                            }
                        }

                        if (fs.FileExists(@"EFI\BOOT\grubx64_real.efi"))
                        {
                            secureBoot = true;
                        }

                        return (version, secureBoot);
                    }
                }
            }
            catch
            {
                return ("", false);
            }
        }

        public static Stream OpenWriteHandle(PhysicalDisk disk)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows raw handle acquisition
                IntPtr hDisk = CreateFileA(disk.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
                if (hDisk == INVALID_HANDLE_VALUE)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to open physical drive {disk.Path}. Win32 Error: {err}");
                }

                // Try to lock and dismount
                DeviceIoControl(hDisk, FSCTL_ALLOW_EXTENDED_DASD_IO, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                
                // Lock
                bool locked = false;
                for (int i = 0; i < 10; i++)
                {
                    if (DeviceIoControl(hDisk, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        locked = true;
                        break;
                    }
                    System.Threading.Thread.Sleep(200);
                }
                if (!locked)
                {
                    Console.WriteLine("Warning: Could not lock the volume. Formatting might fail or be blocked by OS.");
                }

                // Dismount
                DeviceIoControl(hDisk, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

                var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(hDisk, true);
                return new FileStream(safeHandle, FileAccess.ReadWrite);
            }
            else
            {
                // Linux: open path /dev/sda directly
                // Run standard unmount commands on block subdevices first if mounted
                UnmountLinuxPartitions(disk.Path);

                return File.Open(disk.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
        }

        private static void UnmountLinuxPartitions(string diskDevPath)
        {
            try
            {
                // Read /proc/mounts
                if (File.Exists("/proc/mounts"))
                {
                    var lines = File.ReadAllLines("/proc/mounts");
                    foreach (var line in lines)
                    {
                        var parts = line.Split(' ', '\t');
                        if (parts.Length > 0 && parts[0].StartsWith(diskDevPath))
                        {
                            string mountPoint = parts[1];
                            Console.WriteLine($"Unmounting {parts[0]} mounted on {mountPoint}...");
                            RunUmount(parts[0]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning during unmount: {ex.Message}");
            }
        }

        private static void RunUmount(string devicePath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("umount", devicePath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to execute umount: {ex.Message}");
            }
        }
    }
}
