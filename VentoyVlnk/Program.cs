using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VentoyVlnk
{
    class Program
    {
        // VENTOY_GUID bytes: "  www.ventoy.net"
        private static readonly byte[] VentoyGuidBytes = Encoding.ASCII.GetBytes("  www.ventoy.net");
        private const int VlnkFileLen = 32768;
        private const int VlnkNameMax = 384;

        // Win32 P/Invokes and Structures
        internal static class Win32
        {
            public const uint GENERIC_READ = 0x80000000;
            public const uint FILE_SHARE_READ = 0x00000001;
            public const uint FILE_SHARE_WRITE = 0x00000002;
            public const uint OPEN_EXISTING = 3;
            public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
            public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

            [StructLayout(LayoutKind.Sequential)]
            public struct DISK_EXTENT
            {
                public uint DiskNumber;
                public long StartingOffset;
                public long PartitionLength;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct VOLUME_DISK_EXTENTS
            {
                public uint NumberOfDiskExtents;
                public DISK_EXTENT Extent;
            }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern IntPtr CreateFile(
                string lpFileName,
                uint dwDesiredAccess,
                uint dwShareMode,
                IntPtr lpSecurityAttributes,
                uint dwCreationDisposition,
                uint dwFlagsAndAttributes,
                IntPtr hTemplateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr hObject);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeviceIoControl(
                IntPtr hDevice,
                uint dwIoControlCode,
                IntPtr lpInBuffer,
                uint nInBufferSize,
                ref VOLUME_DISK_EXTENTS lpOutBuffer,
                uint nOutBufferSize,
                out uint lpBytesReturned,
                IntPtr lpOverlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ReadFile(
                IntPtr hFile,
                byte[] lpBuffer,
                uint nNumberOfBytesToRead,
                out uint lpNumberOfBytesRead,
                IntPtr lpOverlapped);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern uint GetLogicalDrives();
        }

        // CRC32-C (Castagnoli) Table & Algorithm
        private static readonly uint[] Crc32cTable = new uint[256];
        private static bool _crcInitialized = false;
        private static bool _verbose = false;

        private static uint Reflect(uint refValue, int len)
        {
            uint result = 0;
            for (int i = 1; i <= len; i++)
            {
                if ((refValue & 1) != 0)
                {
                    result |= (uint)(1 << (len - i));
                }
                refValue >>= 1;
            }
            return result;
        }

        private static void InitCrc32cTable()
        {
            uint polynomial = 0x1edc6f41;
            for (int i = 0; i < 256; i++)
            {
                Crc32cTable[i] = Reflect((uint)i, 8) << 24;
                for (int j = 0; j < 8; j++)
                {
                    Crc32cTable[i] = (Crc32cTable[i] << 1) ^
                        (((Crc32cTable[i] & (1u << 31)) != 0) ? polynomial : 0);
                }
                Crc32cTable[i] = Reflect(Crc32cTable[i], 32);
            }
            _crcInitialized = true;
        }

        public static uint CalculateCrc32c(uint crc, byte[] buf, int size)
        {
            if (!_crcInitialized)
            {
                InitCrc32cTable();
            }

            crc ^= 0xffffffff;
            for (int i = 0; i < size; i++)
            {
                crc = (crc >> 8) ^ Crc32cTable[(crc & 0xFF) ^ buf[i]];
            }
            return crc ^ 0xffffffff;
        }

        // Utilities
        private static uint ReverseBytes(uint val)
        {
            return ((val & 0x000000FF) << 24) |
                   ((val & 0x0000FF00) << 8) |
                   ((val & 0x00FF0000) >> 8) |
                   ((val & 0xFF000000) >> 24);
        }

        private static ulong ReverseBytes(ulong val)
        {
            return ((val & 0x00000000000000FFUL) << 56) |
                   ((val & 0x000000000000FF00UL) << 40) |
                   ((val & 0x00000000FF000000UL) << 24) |
                   ((val & 0x000000FF00000000UL) << 8) |
                   ((val & 0x000000FF00000000UL) >> 8) |
                   ((val & 0x0000FF0000000000UL) >> 24) |
                   ((val & 0x00FF000000000000UL) >> 40) |
                   ((val & 0xFF00000000000000UL) >> 56);
        }

        private static uint ReadUInt32LE(byte[] buffer, int offset)
        {
            uint val = BitConverter.ToUInt32(buffer, offset);
            return BitConverter.IsLittleEndian ? val : ReverseBytes(val);
        }

        private static ulong ReadUInt64LE(byte[] buffer, int offset)
        {
            ulong val = BitConverter.ToUInt64(buffer, offset);
            return BitConverter.IsLittleEndian ? val : ReverseBytes(val);
        }

        private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            Array.Copy(bytes, 0, buffer, offset, 4);
        }

        private static void WriteUInt64LE(byte[] buffer, int offset, ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            Array.Copy(bytes, 0, buffer, offset, 8);
        }

        private static bool IsSupportedImgSuffix(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string[] suffixes = { ".iso", ".img", ".wim", ".efi", ".vhd", ".vhdx", ".dat", ".vtoy" };
            foreach (var suffix in suffixes)
            {
                if (ext == suffix) return true;
            }
            // Also check for composite endings like .vlnk.iso
            if (filePath.Contains(".vlnk."))
            {
                foreach (var suffix in suffixes)
                {
                    if (filePath.EndsWith(".vlnk" + suffix, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        // Serialization & Deserialization
        public static byte[] Serialize(uint diskSignature, ulong partOffset, string filepath)
        {
            byte[] buffer = new byte[VlnkFileLen];

            // 1. Write GUID (bytes 0-15)
            Array.Copy(VentoyGuidBytes, 0, buffer, 0, 16);

            // 2. Write CRC32 (bytes 16-19) -> initialized to 0 for CRC calculation
            WriteUInt32LE(buffer, 16, 0);

            // 3. Write disk_signature (bytes 20-23)
            WriteUInt32LE(buffer, 20, diskSignature);

            // 4. Write part_offset (bytes 24-31)
            WriteUInt64LE(buffer, 24, partOffset);

            // 5. Write filepath (bytes 32-415)
            byte[] pathBytes = Encoding.UTF8.GetBytes(filepath);
            int pathLen = Math.Min(pathBytes.Length, VlnkNameMax - 1);
            Array.Copy(pathBytes, 0, buffer, 32, pathLen);
            buffer[32 + pathLen] = 0; // Null terminator (rest of 384 bytes is already 0)

            // 6. Calculate CRC over the first 512 bytes
            byte[] header = new byte[512];
            Array.Copy(buffer, 0, header, 0, 512);
            uint crc = CalculateCrc32c(0, header, 512);

            // 7. Write CRC back to buffer
            WriteUInt32LE(buffer, 16, crc);

            return buffer;
        }

        public static bool Deserialize(byte[] buffer, out uint diskSignature, out ulong partOffset, out string filepath, out uint readCrc, out uint calcCrc)
        {
            diskSignature = 0;
            partOffset = 0;
            filepath = string.Empty;
            readCrc = 0;
            calcCrc = 0;

            if (buffer.Length < 512) return false;

            // Check GUID
            for (int i = 0; i < 16; i++)
            {
                if (buffer[i] != VentoyGuidBytes[i]) return false;
            }

            // Read CRC
            readCrc = ReadUInt32LE(buffer, 16);

            // Calculate expected CRC (zeroing out the CRC field)
            byte[] header = new byte[512];
            Array.Copy(buffer, 0, header, 0, 512);
            header[16] = 0;
            header[17] = 0;
            header[18] = 0;
            header[19] = 0;
            calcCrc = CalculateCrc32c(0, header, 512);

            if (readCrc != calcCrc) return false;

            // Read fields
            diskSignature = ReadUInt32LE(buffer, 20);
            partOffset = ReadUInt64LE(buffer, 24);

            int pathEnd = 32;
            while (pathEnd < 416 && buffer[pathEnd] != 0)
            {
                pathEnd++;
            }
            filepath = Encoding.UTF8.GetString(buffer, 32, pathEnd - 32);

            return true;
        }

        // Platform-specific Disk Signature and Partition Offset Resolution
        private static bool ResolveWindowsPath(string filePath, out string diskDevice, out ulong partOffsetBytes, out string relPath)
        {
            diskDevice = string.Empty;
            partOffsetBytes = 0;
            relPath = string.Empty;

            string absPath = Path.GetFullPath(filePath);
            if (!File.Exists(absPath))
            {
                Console.WriteLine($"Error: File '{absPath}' does not exist.");
                return false;
            }

            string? root = Path.GetPathRoot(absPath);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            {
                Console.WriteLine("Error: File path must include a drive letter (e.g. C:\\path\\to\\file).");
                return false;
            }

            char driveLetter = char.ToUpper(root[0]);
            relPath = absPath.Substring(2).Replace('\\', '/');
            if (!relPath.StartsWith("/"))
            {
                relPath = "/" + relPath;
            }

            string volPath = $@"\\.\{driveLetter}:";
            IntPtr hVol = Win32.CreateFile(volPath, Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (hVol == Win32.INVALID_HANDLE_VALUE)
            {
                Console.WriteLine($"Error opening volume {driveLetter}:. Error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            Win32.VOLUME_DISK_EXTENTS extents = new Win32.VOLUME_DISK_EXTENTS();
            uint bytesReturned;
            bool success = Win32.DeviceIoControl(
                hVol,
                Win32.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                IntPtr.Zero, 0,
                ref extents,
                24,
                out bytesReturned,
                IntPtr.Zero);

            Win32.CloseHandle(hVol);

            if (!success)
            {
                Console.WriteLine($"DeviceIoControl failed. Error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            diskDevice = $@"\\.\PhysicalDrive{extents.Extent.DiskNumber}";
            partOffsetBytes = (ulong)extents.Extent.StartingOffset;

            return true;
        }

        private static bool ResolveLinuxPath(string filePath, out string diskDevice, out ulong partOffsetBytes, out string relPath)
        {
            diskDevice = string.Empty;
            partOffsetBytes = 0;
            relPath = string.Empty;

            string absPath = Path.GetFullPath(filePath);
            if (!File.Exists(absPath))
            {
                Console.WriteLine($"Error: File '{absPath}' does not exist.");
                return false;
            }

            // Parse /proc/mounts to find mount point
            string mountPoint = string.Empty;
            string partitionDevice = string.Empty;
            int longestMountLen = -1;

            try
            {
                var lines = File.ReadAllLines("/proc/mounts");
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string mnt = parts[1];
                        string dev = parts[0];
                        if (absPath.StartsWith(mnt, StringComparison.Ordinal) || (mnt == "/" && absPath.StartsWith("/")))
                        {
                            int mntLen = mnt == "/" ? 1 : mnt.Length;
                            if (mntLen > longestMountLen)
                            {
                                longestMountLen = mntLen;
                                mountPoint = mnt;
                                partitionDevice = dev;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading /proc/mounts: {ex.Message}");
                return false;
            }

            if (string.IsNullOrEmpty(partitionDevice))
            {
                Console.WriteLine("Error: Could not find partition mount point for the file.");
                return false;
            }

            // Calculate relative path inside the partition
            string relativePath = absPath;
            if (longestMountLen > 1)
            {
                relativePath = absPath.Substring(longestMountLen);
            }
            relativePath = relativePath.Replace('\\', '/');
            if (!relativePath.StartsWith("/"))
            {
                relativePath = "/" + relativePath;
            }
            relPath = relativePath;

            // Resolve symlink if necessary
            string resolvedDevice = partitionDevice;
            if (File.Exists(partitionDevice))
            {
                try
                {
                    var target = File.ResolveLinkTarget(partitionDevice, true);
                    if (target != null)
                    {
                        resolvedDevice = target.FullName;
                    }
                }
                catch { /* ignore */ }
            }

            string partitionName = Path.GetFileName(resolvedDevice);

            // Read start sector from sysfs
            string startPath = $"/sys/class/block/{partitionName}/start";
            if (!File.Exists(startPath))
            {
                Console.WriteLine($"Error: Could not find partition start sector sysfs file at '{startPath}'.");
                return false;
            }

            try
            {
                string startText = File.ReadAllText(startPath).Trim();
                ulong startSector = ulong.Parse(startText);
                partOffsetBytes = startSector * 512;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading partition start sector: {ex.Message}");
                return false;
            }

            // Find parent disk name
            string parentBlockDevDir = Path.GetFullPath(Path.Combine($"/sys/class/block/{partitionName}", ".."));
            string parentBlockDevName = Path.GetFileName(parentBlockDevDir);
            diskDevice = $"/dev/{parentBlockDevName}";

            return true;
        }

        private static bool GetDiskSignature(string diskDevice, out uint signature)
        {
            signature = 0;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                IntPtr hPhys = Win32.CreateFile(diskDevice, Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
                if (hPhys == Win32.INVALID_HANDLE_VALUE)
                {
                    Console.WriteLine($"Error opening physical drive {diskDevice}. Error: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                byte[] mbrSector = new byte[512];
                uint bytesRead;
                bool success = Win32.ReadFile(hPhys, mbrSector, 512, out bytesRead, IntPtr.Zero);
                Win32.CloseHandle(hPhys);

                if (!success || bytesRead < 512)
                {
                    Console.WriteLine($"ReadFile failed for physical drive {diskDevice}. Error: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                signature = ReadUInt32LE(mbrSector, 0x1B8);
                return true;
            }
            else
            {
                try
                {
                    if (!File.Exists(diskDevice))
                    {
                        if (_verbose) Console.WriteLine($"Debug: Disk device '{diskDevice}' not found in /dev.");
                        return false;
                    }
                    using (var fs = File.OpenRead(diskDevice))
                    {
                        byte[] buffer = new byte[512];
                        int read = fs.Read(buffer, 0, 512);
                        if (read < 512) return false;

                        signature = ReadUInt32LE(buffer, 0x1B8);
                        return true;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    Console.WriteLine($"Error: Permission Denied reading '{diskDevice}'. Please run with sudo / root permissions.");
                    return false;
                }
                catch (Exception ex)
                {
                    if (_verbose) Console.WriteLine($"Error reading disk signature: {ex.Message}");
                    return false;
                }
            }
        }

        // Parsing Command - Find disk/partition by signature and offset
        private static bool FindDiskAndPartBySig(uint disksig, ulong partOffsetBytes, out string matchedDisk, out string matchedPart)
        {
            matchedDisk = string.Empty;
            matchedPart = string.Empty;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Under Windows, list logical drives and see which matches
                uint drives = Win32.GetLogicalDrives();
                for (int i = 0; i < 26; i++)
                {
                    if ((drives & (1 << i)) != 0)
                    {
                        char letter = (char)('A' + i);
                        string volPath = $@"\\.\{letter}:";
                        IntPtr hVol = Win32.CreateFile(volPath, Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (hVol != Win32.INVALID_HANDLE_VALUE)
                        {
                            Win32.VOLUME_DISK_EXTENTS extents = new Win32.VOLUME_DISK_EXTENTS();
                            uint bytesReturned;
                            bool success = Win32.DeviceIoControl(
                                hVol,
                                Win32.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                                IntPtr.Zero, 0,
                                ref extents,
                                24,
                                out bytesReturned,
                                IntPtr.Zero);
                            Win32.CloseHandle(hVol);

                            if (success && (ulong)extents.Extent.StartingOffset == partOffsetBytes)
                            {
                                string physPath = $@"\\.\PhysicalDrive{extents.Extent.DiskNumber}";
                                uint sig;
                                if (GetDiskSignature(physPath, out sig) && sig == disksig)
                                {
                                    matchedDisk = physPath;
                                    matchedPart = $"{letter}:";
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            else // Linux
            {
                if (!Directory.Exists("/sys/block")) return false;

                var blockDevices = Directory.GetDirectories("/sys/block");
                foreach (var devDir in blockDevices)
                {
                    string devName = Path.GetFileName(devDir);

                    // Skip loop, ram, dm, sr
                    if (devName.StartsWith("loop") || devName.StartsWith("ram") || devName.StartsWith("dm-") || devName.StartsWith("sr"))
                    {
                        continue;
                    }

                    string devPath = $"/dev/{devName}";
                    uint sig;
                    if (GetDiskSignature(devPath, out sig) && sig == disksig)
                    {
                        matchedDisk = devPath;

                        // Now find the partition of this disk that matches the start offset
                        // Search subdirectories of /sys/block/devName that represent partitions
                        var subDirs = Directory.GetDirectories(devDir);
                        foreach (var subDir in subDirs)
                        {
                            string subName = Path.GetFileName(subDir);
                            string startPath = Path.Combine(subDir, "start");
                            if (File.Exists(startPath))
                            {
                                try
                                {
                                    ulong startSector = ulong.Parse(File.ReadAllText(startPath).Trim());
                                    if (startSector * 512 == partOffsetBytes)
                                    {
                                        matchedPart = $"/dev/{subName}";
                                        return true;
                                    }
                                }
                                catch { /* ignore */ }
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static string GetLinuxMountPoint(string partitionDevice)
        {
            try
            {
                var lines = File.ReadAllLines("/proc/mounts");
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string dev = parts[0];
                        string mnt = parts[1];

                        // Normalize partitionDevice
                        string realDev = partitionDevice;
                        if (File.Exists(partitionDevice))
                        {
                            var target = File.ResolveLinkTarget(partitionDevice, true);
                            if (target != null) realDev = target.FullName;
                        }

                        string realMntDev = dev;
                        if (File.Exists(dev))
                        {
                            var target = File.ResolveLinkTarget(dev, true);
                            if (target != null) realMntDev = target.FullName;
                        }

                        if (realDev == realMntDev)
                        {
                            return mnt;
                        }
                    }
                }
            }
            catch { /* ignore */ }
            return string.Empty;
        }

        // Main Executable Entry
        static int Main(string[] args)
        {
            string cmd = string.Empty;
            string infile = string.Empty;
            string outfile = string.Empty;
            string diskDevice = string.Empty;
            ulong partOffsetBytes = 0;

            // Parse arguments
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-c":
                        cmd = "create";
                        if (i + 1 < args.Length) infile = args[++i];
                        break;
                    case "-l":
                        cmd = "parse";
                        if (i + 1 < args.Length) infile = args[++i];
                        break;
                    case "-t":
                        cmd = "check";
                        if (i + 1 < args.Length) infile = args[++i];
                        break;
                    case "-o":
                        if (i + 1 < args.Length) outfile = args[++i];
                        break;
                    case "-d":
                        if (i + 1 < args.Length) diskDevice = args[++i];
                        break;
                    case "-p":
                        if (i + 1 < args.Length)
                        {
                            if (ulong.TryParse(args[++i], out ulong sectors))
                            {
                                partOffsetBytes = sectors * 512;
                            }
                        }
                        break;
                    case "-v":
                        _verbose = true;
                        break;
                    case "-h":
                    case "--help":
                        PrintHelp();
                        return 0;
                }
            }

            if (string.IsNullOrEmpty(cmd))
            {
                PrintHelp();
                return 1;
            }

            if (cmd == "create")
            {
                if (string.IsNullOrEmpty(infile))
                {
                    Console.WriteLine("Error: Source image path (-c) must be specified.");
                    return 1;
                }

                if (!IsSupportedImgSuffix(infile))
                {
                    Console.WriteLine("Error: Unsupported image suffix. Supported formats: .iso, .img, .wim, .efi, .vhd, .vhdx, .dat, .vtoy");
                    return 1;
                }

                string relPath = string.Empty;
                uint disksig = 0;

                // Resolve disk name and partition offset
                if (string.IsNullOrEmpty(diskDevice) || partOffsetBytes == 0)
                {
                    // Automatic resolution
                    bool resolved = false;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        resolved = ResolveWindowsPath(infile, out diskDevice, out partOffsetBytes, out relPath);
                    }
                    else
                    {
                        resolved = ResolveLinuxPath(infile, out diskDevice, out partOffsetBytes, out relPath);
                    }

                    if (!resolved)
                    {
                        Console.WriteLine("Error: Automatic partition and disk resolution failed.");
                        Console.WriteLine("Please run as root/Administrator, or specify -d (disk path) and -p (partition offset in sectors) manually.");
                        return 1;
                    }
                }
                else
                {
                    // Manual path (relPath will be the relative file path from partition root)
                    // Since it's manual, we assume the path is relative to partition or absolute path where relPath must be figured out.
                    // If infile is already relative or we can't figure it out, we'll try to get Path.GetFileName or absolute path.
                    relPath = infile.Replace('\\', '/');
                    if (!relPath.StartsWith("/")) relPath = "/" + relPath;
                }

                if (_verbose)
                {
                    Console.WriteLine($"[Verbose] Resolved Disk: {diskDevice}");
                    Console.WriteLine($"[Verbose] Partition Offset (bytes): {partOffsetBytes} (sectors: {partOffsetBytes / 512})");
                    Console.WriteLine($"[Verbose] Relative Path in partition: {relPath}");
                }

                if (!GetDiskSignature(diskDevice, out disksig))
                {
                    Console.WriteLine("Error: Could not retrieve disk signature.");
                    return 1;
                }

                if (_verbose)
                {
                    Console.WriteLine($"[Verbose] Disk Signature: 0x{disksig:X8}");
                }

                // Generate default output filename if not specified
                if (string.IsNullOrEmpty(outfile))
                {
                    string filename = Path.GetFileName(infile);
                    string ext = Path.GetExtension(filename);
                    string baseName = filename.Substring(0, filename.Length - ext.Length);
                    outfile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(infile)) ?? string.Empty, $"{baseName}.vlnk{ext.ToLowerInvariant()}");
                }

                try
                {
                    byte[] fileData = Serialize(disksig, partOffsetBytes, relPath);
                    File.WriteAllBytes(outfile, fileData);
                    Console.WriteLine("Vlnk file successfully created!");
                    Console.WriteLine($"Output file: {outfile}");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing vlnk file: {ex.Message}");
                    return 1;
                }
            }
            else if (cmd == "parse")
            {
                if (string.IsNullOrEmpty(infile))
                {
                    Console.WriteLine("Error: Vlnk file path (-l) must be specified.");
                    return 1;
                }

                if (!File.Exists(infile))
                {
                    Console.WriteLine($"Error: File '{infile}' does not exist.");
                    return 1;
                }

                try
                {
                    byte[] fileData = File.ReadAllBytes(infile);
                    if (fileData.Length != VlnkFileLen)
                    {
                        Console.WriteLine("Error: Invalid vlnk file size.");
                        return 1;
                    }

                    if (!Deserialize(fileData, out uint disksig, out ulong partoff, out string filepath, out uint readCrc, out uint calcCrc))
                    {
                        Console.WriteLine("Error: Invalid or corrupted vlnk file (CRC check failed).");
                        return 1;
                    }

                    Console.WriteLine($"Disk Signature:       0x{disksig:X8}");
                    Console.WriteLine($"Partition Offset:     {partoff} bytes ({partoff / 512} sectors)");
                    Console.WriteLine($"File Path:            {filepath}");

                    // Search for the matching partition
                    if (FindDiskAndPartBySig(disksig, partoff, out string matchedDisk, out string matchedPart))
                    {
                        Console.WriteLine($"Matched Disk Device:  {matchedDisk}");
                        Console.WriteLine($"Matched Partition:    {matchedPart}");

                        string mountPoint = string.Empty;
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            mountPoint = matchedPart; // Drive letter (e.g. "D:")
                        }
                        else
                        {
                            mountPoint = GetLinuxMountPoint(matchedPart);
                        }

                        if (!string.IsNullOrEmpty(mountPoint))
                        {
                            string fullTarget = Path.Combine(mountPoint, filepath.TrimStart('/'));
                            Console.WriteLine($"Resolved Target Path: {fullTarget}");
                            bool exists = File.Exists(fullTarget) || Directory.Exists(fullTarget);
                            Console.WriteLine($"Target File Exists:   {(exists ? "YES" : "NO")}");
                        }
                        else
                        {
                            Console.WriteLine("Resolved Target Path: Partition is not mounted.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Matched Partition:    Partition not found (disk might not be connected or is modified).");
                    }

                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error parsing vlnk file: {ex.Message}");
                    return 1;
                }
            }
            else if (cmd == "check")
            {
                if (string.IsNullOrEmpty(infile))
                {
                    return 1;
                }

                if (!File.Exists(infile))
                {
                    return 1;
                }

                try
                {
                    byte[] fileData = File.ReadAllBytes(infile);
                    if (fileData.Length != VlnkFileLen) return 1;

                    if (Deserialize(fileData, out _, out _, out _, out _, out _))
                    {
                        if (_verbose) Console.WriteLine("Vlnk validation: SUCCESS");
                        return 0;
                    }
                }
                catch
                {
                    // Ignore
                }

                if (_verbose) Console.WriteLine("Vlnk validation: FAILED");
                return 1;
            }

            return 0;
        }

        static void PrintHelp()
        {
            Console.WriteLine("VentoyVlnk DotNet CLI Tool");
            Console.WriteLine("========================================");
            Console.WriteLine("Usage:");
            Console.WriteLine("  Create a vlnk file:");
            Console.WriteLine("    dotnet VentoyVlnk.dll -c <iso_file> [-o <vlnk_file>] [-d <disk_device> -p <offset_sectors>] [-v]");
            Console.WriteLine("  Parse a vlnk file:");
            Console.WriteLine("    dotnet VentoyVlnk.dll -l <vlnk_file> [-v]");
            Console.WriteLine("  Check if a file is a valid vlnk file (exit code 0 if valid):");
            Console.WriteLine("    dotnet VentoyVlnk.dll -t <vlnk_file> [-v]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -c <file>      Create vlnk for source file (e.g. windows.iso)");
            Console.WriteLine("  -l <file>      Parse and display vlnk file information");
            Console.WriteLine("  -t <file>      Check/validate if vlnk file is correct");
            Console.WriteLine("  -o <file>      Output file path (default: srcfile.vlnk.ext)");
            Console.WriteLine("  -d <device>    Force disk device name (e.g. /dev/sdb or \\\\.\\PhysicalDrive0)");
            Console.WriteLine("  -p <sectors>   Force partition start offset in sectors (512-byte blocks)");
            Console.WriteLine("  -v             Verbose output mode");
            Console.WriteLine("  -h, --help     Show this help message");
        }
    }
}
