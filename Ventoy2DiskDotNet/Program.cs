using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Ventoy2DiskDotNet
{
    class Program
    {
        private static string _curServerToken = Guid.NewGuid().ToString("n");
        private static string _curLanguage = "en-US";
        private static int _curPartStyle = 0; // 0 = MBR, 1 = GPT

        // Progress state for Ventoy2Disk
        private static int _percent = 100;
        private static string _processDisk = string.Empty;
        private static string _processType = string.Empty; // "install", "update", "clean"
        private static string _processResult = "success"; // "success", "failed"
        private static readonly object _progressLock = new object();

        // Plugson settings
        private static string _plugsonMountPoint = string.Empty;

        // Win32 declarations for device scanning on Windows
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

        public class DiskInfo
        {
            public string Name { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public string HumanSize { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public int VtoyValid { get; set; } // 0 = invalid, 1 = valid
            public string VtoyVer { get; set; } = string.Empty;
            public int VtoySecureBoot { get; set; } // 0 = disabled, 1 = enabled
            public int VtoyPartStyle { get; set; } // 0 = MBR, 1 = GPT
        }

        private static string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            double dValue = bytes;
            while (Math.Round(dValue / 1024) >= 1)
            {
                dValue /= 1024;
                counter++;
            }
            return $"{dValue:F1} {suffixes[counter]}";
        }

        // Linux device detection
        private static List<DiskInfo> GetLinuxDisks(bool showAll)
        {
            var list = new List<DiskInfo>();
            try
            {
                var psi = new ProcessStartInfo("lsblk", "-J -b -o NAME,MODEL,SIZE,TYPE,TRAN")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit();
                        using (var doc = JsonDocument.Parse(output))
                        {
                            if (doc.RootElement.TryGetProperty("blockdevices", out var devices))
                            {
                                foreach (var dev in devices.EnumerateArray())
                                {
                                    string name = dev.GetProperty("name").GetString() ?? string.Empty;
                                    string type = dev.GetProperty("type").GetString() ?? string.Empty;
                                    string tran = dev.TryGetProperty("tran", out var tranProp) ? (tranProp.GetString() ?? string.Empty) : string.Empty;
                                    string model = dev.TryGetProperty("model", out var modelProp) ? (modelProp.GetString() ?? string.Empty) : "Unknown Model";
                                    long size = dev.GetProperty("size").GetInt64();

                                    if (type != "disk") continue;
                                    if (name.StartsWith("loop") || name.StartsWith("ram") || name.StartsWith("dm-") || name.StartsWith("sr")) continue;

                                    if (!showAll && tran != "usb") continue;

                                    var info = new DiskInfo
                                    {
                                        Name = name,
                                        Model = model,
                                        SizeBytes = size,
                                        HumanSize = FormatSize(size)
                                    };
                                    DetectVentoy(info);
                                    list.Add(info);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scanning Linux disks: {ex.Message}");
            }
            return list;
        }

        private static void DetectVentoy(DiskInfo info)
        {
            string devPath = $"/dev/{info.Name}";
            if (!File.Exists(devPath)) return;

            try
            {
                using (var fs = File.OpenRead(devPath))
                {
                    byte[] mbr = new byte[512];
                    int read = fs.Read(mbr, 0, 512);
                    if (read < 512) return;

                    if (mbr[510] != 0x55 || mbr[511] != 0xAA) return;

                    uint p1Start = BitConverter.ToUInt32(mbr, 454);
                    uint p2Start = BitConverter.ToUInt32(mbr, 470);
                    uint p2Size = BitConverter.ToUInt32(mbr, 474);

                    bool isValid = false;
                    int style = 0;

                    if (p1Start == 2048 && p2Size == 65536)
                    {
                        isValid = true;
                    }
                    else
                    {
                        fs.Seek(512, SeekOrigin.Begin);
                        byte[] gptHeader = new byte[92];
                        fs.Read(gptHeader, 0, 92);
                        string gptSig = Encoding.ASCII.GetString(gptHeader, 0, 8);
                        if (gptSig == "EFI PART")
                        {
                            style = 1;
                            fs.Seek(1152 + 56, SeekOrigin.Begin);
                            byte[] nameBytes = new byte[72];
                            fs.Read(nameBytes, 0, 72);
                            string partName = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
                            if (partName == "VTOYEFI")
                            {
                                isValid = true;
                            }
                        }
                    }

                    if (isValid)
                    {
                        info.VtoyValid = 1;
                        info.VtoyPartStyle = style;

                        string partPath = $"{devPath}2";
                        if (File.Exists(partPath))
                        {
                            string mountPoint = $"/tmp/vtoy_mnt_{info.Name}";
                            try
                            {
                                Directory.CreateDirectory(mountPoint);
                                var mountPsi = new ProcessStartInfo("mount", $"-o ro {partPath} {mountPoint}")
                                {
                                    RedirectStandardError = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };
                                using (var mountProc = Process.Start(mountPsi))
                                {
                                    mountProc?.WaitForExit();
                                    if (mountProc?.ExitCode == 0)
                                    {
                                        string cfgPath = Path.Combine(mountPoint, "grub/grub.cfg");
                                        if (File.Exists(cfgPath))
                                        {
                                            string cfgText = File.ReadAllText(cfgPath);
                                            int idx = cfgText.IndexOf("VENTOY_VERSION=");
                                            if (idx >= 0)
                                            {
                                                int startIdx = idx + "VENTOY_VERSION=".Length;
                                                if (startIdx < cfgText.Length && cfgText[startIdx] == '"') startIdx++;
                                                int endIdx = startIdx;
                                                while (endIdx < cfgText.Length && cfgText[endIdx] != '"' && cfgText[endIdx] != '\r' && cfgText[endIdx] != '\n')
                                                {
                                                    endIdx++;
                                                }
                                                info.VtoyVer = cfgText.Substring(startIdx, endIdx - startIdx);
                                            }
                                        }
                                        string realEfiPath = Path.Combine(mountPoint, "EFI/BOOT/grubx64_real.efi");
                                        info.VtoySecureBoot = File.Exists(realEfiPath) ? 1 : 0;
                                    }
                                }
                            }
                            finally
                            {
                                var umountPsi = new ProcessStartInfo("umount", mountPoint)
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };
                                using (var umountProc = Process.Start(umountPsi))
                                {
                                    umountProc?.WaitForExit();
                                }
                                try { Directory.Delete(mountPoint); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Windows device detection
        private static List<DiskInfo> GetWindowsDisks(bool showAll)
        {
            var list = new List<DiskInfo>();
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return list;

            uint drives = Win32.GetLogicalDrives();
            for (int i = 0; i < 26; i++)
            {
                if ((drives & (1 << i)) != 0)
                {
                    char letter = (char)('A' + i);
                    try
                    {
                        var driveInfo = new DriveInfo(letter.ToString());
                        if (!driveInfo.IsReady) continue;

                        if (!showAll && driveInfo.DriveType != DriveType.Removable) continue;

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

                            if (success)
                            {
                                uint diskNum = extents.Extent.DiskNumber;
                                var info = new DiskInfo
                                {
                                    Name = $"PhysicalDrive{diskNum}",
                                    Model = driveInfo.VolumeLabel + $" (Drive {letter}:)",
                                    SizeBytes = driveInfo.TotalSize,
                                    HumanSize = FormatSize(driveInfo.TotalSize)
                                };
                                DetectVentoyWindows(info, letter);
                                list.Add(info);
                            }
                        }
                    }
                    catch { }
                }
            }
            return list;
        }

        private static void DetectVentoyWindows(DiskInfo info, char driveLetter)
        {
            string physPath = $@"\\.\{info.Name}";
            IntPtr hPhys = Win32.CreateFile(physPath, Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (hPhys == Win32.INVALID_HANDLE_VALUE) return;

            try
            {
                byte[] mbr = new byte[512];
                uint bytesRead;
                bool success = Win32.ReadFile(hPhys, mbr, 512, out bytesRead, IntPtr.Zero);
                if (!success || bytesRead < 512) return;

                if (mbr[510] != 0x55 || mbr[511] != 0xAA) return;

                uint p1Start = BitConverter.ToUInt32(mbr, 454);
                uint p2Start = BitConverter.ToUInt32(mbr, 470);
                uint p2Size = BitConverter.ToUInt32(mbr, 474);

                bool isValid = false;
                int style = 0;

                if (p1Start == 2048 && p2Size == 65536)
                {
                    isValid = true;
                }
                else
                {
                    byte[] gptHeader = new byte[92];
                    Win32.ReadFile(hPhys, gptHeader, 92, out bytesRead, IntPtr.Zero);
                    string gptSig = Encoding.ASCII.GetString(gptHeader, 0, 8);
                    if (gptSig == "EFI PART")
                    {
                        style = 1;
                        isValid = true; // basic heuristic
                    }
                }

                if (isValid)
                {
                    info.VtoyValid = 1;
                    info.VtoyPartStyle = style;
                    info.VtoyVer = "1.0.99";

                    // Try checking config path if accessible on logical drive
                    string cfg = $"{driveLetter}:\\grub\\grub.cfg";
                    if (File.Exists(cfg))
                    {
                        string cfgText = File.ReadAllText(cfg);
                        int idx = cfgText.IndexOf("VENTOY_VERSION=");
                        if (idx >= 0)
                        {
                            int startIdx = idx + "VENTOY_VERSION=".Length;
                            if (startIdx < cfgText.Length && cfgText[startIdx] == '"') startIdx++;
                            int endIdx = startIdx;
                            while (endIdx < cfgText.Length && cfgText[endIdx] != '"' && cfgText[endIdx] != '\r' && cfgText[endIdx] != '\n')
                            {
                                endIdx++;
                            }
                            info.VtoyVer = cfgText.Substring(startIdx, endIdx - startIdx);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                Win32.CloseHandle(hPhys);
            }
        }

        private static string GetVentoyVersion()
        {
            try
            {
                string path = "/root/ventoy-conv/Ventoy/INSTALL/ventoy/version";
                if (File.Exists(path)) return File.ReadAllText(path).Trim();
            }
            catch { }
            return "1.0.99";
        }

        // Wipe sectors natively (Clean operation)
        private static void WipeDiskSectors(string diskPath)
        {
            try
            {
                using (var fs = File.Open(diskPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    byte[] zeroes = new byte[1024 * 1024];
                    fs.Write(zeroes, 0, zeroes.Length);

                    try
                    {
                        long size = fs.Length;
                        if (size > zeroes.Length)
                        {
                            fs.Seek(size - zeroes.Length, SeekOrigin.Begin);
                            fs.Write(zeroes, 0, zeroes.Length);
                        }
                    }
                    catch { }
                    fs.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error wiping sectors on {diskPath}: {ex.Message}");
                throw;
            }
        }

        // Install / Update / Clean subprocess execution
        private static void StartBackgroundOperation(string type, string diskName, int style, int secureBoot, string reserveSpace, string fsType)
        {
            lock (_progressLock)
            {
                _percent = 0;
                _processDisk = diskName;
                _processType = type;
                _processResult = "success";
            }

            Task.Run(async () =>
            {
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        string driveIdx = string.Concat(diskName.Where(char.IsDigit));
                        if (string.IsNullOrEmpty(driveIdx)) driveIdx = "1";

                        string args = "VTOYCLI";
                        if (type == "install") args += " /I";
                        else if (type == "update") args += " /U";
                        else
                        {
                            WipeDiskSectors(diskName);
                            lock (_progressLock) { _percent = 100; _processResult = "success"; }
                            return;
                        }

                        args += $" /PhyDrive:{driveIdx}";
                        if (style == 1) args += " /GPT";
                        if (secureBoot == 0) args += " /NOSB";
                        if (fsType.Equals("ntfs", StringComparison.OrdinalIgnoreCase)) args += " /FS:NTFS";

                        if (long.TryParse(reserveSpace, out long bytes) && bytes > 0)
                        {
                            long mb = bytes / (1024 * 1024);
                            args += $" /R:{mb}";
                        }

                        var psi = new ProcessStartInfo("Ventoy2Disk.exe", args)
                        {
                            WorkingDirectory = "/root/ventoy-conv/Ventoy/INSTALL",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        string workDir = psi.WorkingDirectory;
                        string percentFile = Path.Combine(workDir, "cli_percent.txt");
                        string doneFile = Path.Combine(workDir, "cli_done.txt");
                        string logFile = Path.Combine(workDir, "cli_log.txt");
                        try { File.Delete(percentFile); File.Delete(doneFile); File.Delete(logFile); } catch { }

                        using (var proc = Process.Start(psi))
                        {
                            if (proc == null) throw new Exception("Failed to start Ventoy2Disk.exe");

                            while (!proc.HasExited)
                            {
                                await Task.Delay(500);
                                if (File.Exists(percentFile))
                                {
                                    try
                                    {
                                        string text = File.ReadAllText(percentFile).Trim();
                                        if (int.TryParse(text, out int p))
                                        {
                                            lock (_progressLock) { _percent = p; }
                                        }
                                    }
                                    catch { }
                                }
                            }
                            proc.WaitForExit();

                            for (int i = 0; i < 10 && !File.Exists(doneFile); i++)
                            {
                                await Task.Delay(500);
                            }

                            if (File.Exists(doneFile))
                            {
                                string doneText = File.ReadAllText(doneFile).Trim();
                                lock (_progressLock)
                                {
                                    _percent = 100;
                                    _processResult = doneText == "0" ? "success" : "failed";
                                }
                            }
                            else
                            {
                                lock (_progressLock)
                                {
                                    _percent = 100;
                                    _processResult = proc.ExitCode == 0 ? "success" : "failed";
                                }
                            }
                        }
                    }
                    else // Linux Native C# worker replacing Ventoy2Disk.sh
                    {
                        try
                        {
                            string diskPath = $"/dev/{diskName}";
                            if (!File.Exists(diskPath))
                            {
                                throw new Exception($"Disk device {diskPath} does not exist.");
                            }

                            if (type == "clean")
                            {
                                WipeDiskSectors(diskPath);
                                lock (_progressLock) { _percent = 100; _processResult = "success"; }
                                return;
                            }

                            string toolDir = GetToolDir();
                            string installDir = "/root/ventoy-conv/Ventoy/INSTALL";
                            string vtoycliPath = Path.Combine(installDir, "tool", toolDir, "vtoycli");
                            string xzcatPath = Path.Combine(installDir, "tool", toolDir, "xzcat");
                            if (!File.Exists(xzcatPath)) xzcatPath = "xzcat";
                            else RunCommand("chmod", $"+x {xzcatPath}", out _, out _);
                            if (File.Exists(vtoycliPath)) RunCommand("chmod", $"+x {vtoycliPath}", out _, out _);

                            // Umount all disk partitions
                            UmountDisk(diskName);

                            if (type == "install")
                            {
                                lock (_progressLock) { _percent = 5; }
                                // Zero out partition table first
                                ZeroDiskHeader(diskPath);

                                lock (_progressLock) { _percent = 10; }

                                // Read sector size and sector count
                                long sectorCount = long.Parse(File.ReadAllText($"/sys/class/block/{diskName}/size").Trim());
                                long reserveMb = 0;
                                if (long.TryParse(reserveSpace, out long rsvBytes) && rsvBytes > 0)
                                {
                                    reserveMb = rsvBytes / (1024 * 1024);
                                }

                                // Space check
                                long minSectors = 32 * 2048 + 2048; // partition 2 (32MB) + partition 1 start (1MB)
                                if (sectorCount <= minSectors)
                                {
                                    throw new Exception("No enough space in disk");
                                }

                                // Layout calculation
                                long part1_start_sector = 2048;
                                long part1_end_sector = 0;
                                long part2_start_sector = 0;
                                long part2_end_sector = 0;

                                if (style == 1) // GPT
                                {
                                    if (reserveMb > 0)
                                    {
                                        long reserve_sector_num = reserveMb * 2048 + 33;
                                        part1_end_sector = sectorCount - reserve_sector_num - 65536 - 1;
                                    }
                                    else
                                    {
                                        part1_end_sector = sectorCount - 65536 - 34;
                                    }
                                }
                                else // MBR
                                {
                                    if (reserveMb > 0)
                                    {
                                        long reserve_sector_num = reserveMb * 2048;
                                        part1_end_sector = sectorCount - reserve_sector_num - 65536 - 1;
                                    }
                                    else
                                    {
                                        part1_end_sector = sectorCount - 65536 - 1;
                                    }
                                }

                                part2_start_sector = part1_end_sector + 1;
                                long modsector = part2_start_sector % 8;
                                if (modsector > 0)
                                {
                                    part1_end_sector -= modsector;
                                    part2_start_sector = part1_end_sector + 1;
                                }
                                part2_end_sector = part2_start_sector + 65536 - 1;

                                lock (_progressLock) { _percent = 20; }

                                // Create partitions using parted
                                int partedExit = -1;
                                if (style == 1) // GPT
                                {
                                    string vt_set_efi_type = (toolDir != "aarch64") ? "set 2 msftdata on" : "";
                                    string partedArgs = $"-a none --script {diskPath} mklabel gpt unit s mkpart Ventoy ntfs {part1_start_sector} {part1_end_sector} mkpart VTOYEFI fat16 {part2_start_sector} {part2_end_sector} {vt_set_efi_type} quit";
                                    partedExit = RunCommand("parted", partedArgs, out _, out _);
                                }
                                else // MBR
                                {
                                    string partedArgs = $"-a none --script {diskPath} mklabel msdos unit s mkpart primary ntfs {part1_start_sector} {part1_end_sector} mkpart primary fat16 {part2_start_sector} {part2_end_sector} set 1 boot on quit";
                                    partedExit = RunCommand("parted", partedArgs, out _, out _);
                                }

                                if (partedExit != 0)
                                {
                                    throw new Exception("Parted failed to partition the disk.");
                                }

                                lock (_progressLock) { _percent = 30; }

                                if (style == 0) // MBR: write 0xEF to offset 466
                                {
                                    using (var fs = File.OpenWrite(diskPath))
                                    {
                                        fs.Position = 466;
                                        fs.WriteByte(0xEF);
                                        fs.Flush();
                                    }
                                }
                                else // GPT: run vtoycli gpt
                                {
                                    RunCommand(vtoycliPath, $"-f {diskPath}", out _, out _);
                                }

                                // Trigger partition table reload
                                RunCommand("udevadm", "trigger", out _, out _);
                                RunCommand("partprobe", "", out _, out _);
                                RunCommand("partx", $"-u {diskPath}", out _, out _);
                                await Task.Delay(3000);

                                lock (_progressLock) { _percent = 40; }

                                string part1Path = GetPartName(diskPath, 1);
                                string part2Path = GetPartName(diskPath, 2);

                                // Wait for partitions to appear
                                bool partsExist = false;
                                for (int i = 0; i < 10; i++)
                                {
                                    if (File.Exists(part1Path) && File.Exists(part2Path))
                                    {
                                        partsExist = true;
                                        break;
                                    }
                                    await Task.Delay(1000);
                                }
                                if (!partsExist)
                                {
                                    throw new Exception("Partition block devices failed to appear.");
                                }

                                lock (_progressLock) { _percent = 50; }

                                // Format Partition 2 (EFI FAT)
                                bool efiFormatSuccess = false;
                                for (int i = 0; i < 5; i++)
                                {
                                    RunCommand("umount", part2Path, out _, out _);
                                    int exitVal = RunCommand("mkfs.vfat", $"-F 16 -n VTOYEFI -s 1 {part2Path}", out _, out _);
                                    if (exitVal == 0) { efiFormatSuccess = true; break; }
                                    await Task.Delay(2000);
                                }
                                if (!efiFormatSuccess)
                                {
                                    throw new Exception("Failed to format EFI Partition 2.");
                                }

                                lock (_progressLock) { _percent = 60; }

                                // Format Partition 1 (Data NTFS/exFAT)
                                bool dataFormatSuccess = false;
                                for (int i = 0; i < 5; i++)
                                {
                                    RunCommand("umount", part1Path, out _, out _);
                                    int exitVal = -1;
                                    if (fsType.Equals("ntfs", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string mkfsNtfsPath = Path.Combine(installDir, "tool", toolDir, "mkfs.ntfs");
                                        if (File.Exists(mkfsNtfsPath))
                                        {
                                            RunCommand("chmod", $"+x {mkfsNtfsPath}", out _, out _);
                                            exitVal = RunCommand(mkfsNtfsPath, $"-f -F -L \"Ventoy\" {part1Path}", out _, out _);
                                        }
                                        else
                                        {
                                            exitVal = RunCommand("mkfs.ntfs", $"-f -F -L \"Ventoy\" {part1Path}", out _, out _);
                                        }
                                    }
                                    else
                                    {
                                        long diskSizeGb = sectorCount / 2097152;
                                        int clusterSectors = (diskSizeGb > 32) ? 256 : 64;
                                        string mkexfatfsPath = Path.Combine(installDir, "tool", toolDir, "mkexfatfs");
                                        if (File.Exists(mkexfatfsPath))
                                        {
                                            RunCommand("chmod", $"+x {mkexfatfsPath}", out _, out _);
                                            exitVal = RunCommand(mkexfatfsPath, $"-n \"Ventoy\" -s {clusterSectors} {part1Path}", out _, out _);
                                        }
                                        else
                                        {
                                            exitVal = RunCommand("mkexfatfs", $"-n \"Ventoy\" -s {clusterSectors} {part1Path}", out _, out _);
                                        }
                                    }
                                    if (exitVal == 0) { dataFormatSuccess = true; break; }
                                    await Task.Delay(2000);
                                }
                                if (!dataFormatSuccess)
                                {
                                    throw new Exception("Failed to format Data Partition 1.");
                                }

                                lock (_progressLock) { _percent = 70; }

                                // Write boot.img, core.img, ventoy.disk.img and GUIDs
                                using (var fs = File.OpenWrite(diskPath))
                                {
                                    // 1. Write first 446 bytes of boot.img
                                    string bootImgPath = Path.Combine(installDir, "boot", "boot.img");
                                    byte[] bootBytes = File.ReadAllBytes(bootImgPath);
                                    fs.Position = 0;
                                    fs.Write(bootBytes, 0, 446);

                                    // 2. Write core.img
                                    string coreImgXz = Path.Combine(installDir, "boot", "core.img.xz");
                                    if (style == 1) // GPT
                                    {
                                        fs.Position = 92;
                                        fs.WriteByte(0x22);
                                        fs.Position = 34 * 512;
                                        DecompressAndWrite(coreImgXz, xzcatPath, fs);
                                        fs.Position = 17908;
                                        fs.WriteByte(0x23);
                                    }
                                    else // MBR
                                    {
                                        fs.Position = 512;
                                        DecompressAndWrite(coreImgXz, xzcatPath, fs);
                                    }

                                    // 3. Write ventoy.disk.img
                                    string ventoyDiskXz = Path.Combine(installDir, "ventoy", "ventoy.disk.img.xz");
                                    fs.Position = part2_start_sector * 512;
                                    DecompressAndWrite(ventoyDiskXz, xzcatPath, fs);

                                    // 4. Generate and write Disk UUID and signature
                                    byte[] uuid = new byte[16];
                                    Random.Shared.NextBytes(uuid);
                                    fs.Position = 384;
                                    fs.Write(uuid, 0, 16);
                                    fs.Position = 440;
                                    fs.Write(uuid, 12, 4);

                                    fs.Flush();
                                }

                                lock (_progressLock) { _percent = 85; }

                                // Esp partition resize for secure boot
                                if (secureBoot != 1)
                                {
                                    RunCommand(vtoycliPath, $"partresize -s {diskPath} {part2_start_sector}", out _, out _);
                                }

                                RunCommand("sync", "", out _, out _);
                                lock (_progressLock) { _percent = 100; _processResult = "success"; }
                            }
                            else if (type == "update")
                            {
                                lock (_progressLock) { _percent = 10; }

                                using (var fs = File.Open(diskPath, FileMode.Open, FileAccess.ReadWrite))
                                {
                                    // 1. Read original disk UUID (16 bytes at 384)
                                    byte[] savedUuid = new byte[16];
                                    fs.Position = 384;
                                    fs.ReadExactly(savedUuid, 0, 16);

                                    // 2. Read original reserved data (8 sectors at 2040)
                                    byte[] savedRsv = new byte[8 * 512];
                                    fs.Position = 2040 * 512;
                                    fs.ReadExactly(savedRsv, 0, savedRsv.Length);

                                    // 3. Detect partition style from partition type byte at 450
                                    fs.Position = 450;
                                    int part1Type = fs.ReadByte();
                                    bool isGpt = (part1Type == 0xEE);

                                    lock (_progressLock) { _percent = 30; }

                                    // 4. Write first 440 bytes of boot.img
                                    string bootImgPath = Path.Combine(installDir, "boot", "boot.img");
                                    byte[] bootBytes = File.ReadAllBytes(bootImgPath);
                                    fs.Position = 0;
                                    fs.Write(bootBytes, 0, 440);

                                    // 5. Restore saved UUID
                                    fs.Position = 384;
                                    fs.Write(savedUuid, 0, 16);

                                    // 6. Write core.img
                                    string coreImgXz = Path.Combine(installDir, "boot", "core.img.xz");
                                    if (isGpt)
                                    {
                                        fs.Position = 92;
                                        fs.WriteByte(0x22);
                                        fs.Position = 34 * 512;
                                        DecompressAndWrite(coreImgXz, xzcatPath, fs);
                                        fs.Position = 17908;
                                        fs.WriteByte(0x23);
                                    }
                                    else
                                    {
                                        // If MBR, handle active flag
                                        fs.Position = 446;
                                        int part1Active = fs.ReadByte();
                                        fs.Position = 462;
                                        int part2Active = fs.ReadByte();
                                        if (part1Active == 0x00 && part2Active == 0x80)
                                        {
                                            fs.Position = 446;
                                            fs.WriteByte(0x80);
                                            fs.Position = 462;
                                            fs.WriteByte(0x00);
                                        }

                                        fs.Position = 512;
                                        DecompressAndWrite(coreImgXz, xzcatPath, fs);
                                    }

                                    // 7. Restore reserved data
                                    fs.Position = 2040 * 512;
                                    fs.Write(savedRsv, 0, savedRsv.Length);

                                    lock (_progressLock) { _percent = 60; }

                                    // 8. Find part2 start sector
                                    string part2Name = GetPartName(diskName, 2);
                                    string startFile = $"/sys/class/block/{part2Name}/start";
                                    if (!File.Exists(startFile))
                                    {
                                        throw new Exception($"Cannot find partition start info at {startFile}");
                                    }
                                    long part2_start_sector = long.Parse(File.ReadAllText(startFile).Trim());

                                    // 9. Write ventoy.disk.img
                                    string ventoyDiskXz = Path.Combine(installDir, "ventoy", "ventoy.disk.img.xz");
                                    fs.Position = part2_start_sector * 512;
                                    DecompressAndWrite(ventoyDiskXz, xzcatPath, fs);

                                    fs.Flush();

                                    lock (_progressLock) { _percent = 85; }

                                    // Esp partition resize for secure boot
                                    if (secureBoot != 1)
                                    {
                                        RunCommand(vtoycliPath, $"partresize -s {diskPath} {part2_start_sector}", out _, out _);
                                    }

                                    if (isGpt)
                                    {
                                        RunCommand(vtoycliPath, $"gpt -f {diskPath}", out _, out _);
                                    }
                                }

                                RunCommand("sync", "", out _, out _);
                                lock (_progressLock) { _percent = 100; _processResult = "success"; }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error during native Linux execution: {ex.Message}");
                            lock (_progressLock) { _percent = 100; _processResult = "failed"; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                    lock (_progressLock)
                    {
                        _percent = 100;
                        _processResult = "failed";
                    }
                }
            });
        }

        private static string GetToolDir()
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            if (arch == Architecture.Arm64) return "aarch64";
            if (arch == Architecture.X64) return "x86_64";
            try
            {
                var psi = new ProcessStartInfo("uname", "-m") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                string m = p?.StandardOutput.ReadToEnd().Trim() ?? "";
                p?.WaitForExit();
                if (m.Contains("aarch64") || m.Contains("arm64")) return "aarch64";
                if (m.Contains("x86_64") || m.Contains("amd64")) return "x86_64";
                if (m.Contains("mips64")) return "mips64el";
            }
            catch {}
            return "i386";
        }

        private static int RunCommand(string cmd, string args, out string output, out string error)
        {
            var psi = new ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                output = "";
                error = "Failed to start process";
                return -1;
            }
            output = proc.StandardOutput.ReadToEnd();
            error = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode;
        }

        private static string GetPartName(string diskPath, int num)
        {
            if (diskPath.Contains("nvme") || diskPath.Contains("mmcblk") || diskPath.Contains("loop") || diskPath.Contains("md"))
            {
                return $"{diskPath}p{num}";
            }
            return $"{diskPath}{num}";
        }

        private static void ZeroDiskHeader(string diskPath)
        {
            using (var fs = File.OpenWrite(diskPath))
            {
                byte[] zeroes = new byte[32768]; // 64 sectors
                fs.Position = 0;
                fs.Write(zeroes, 0, zeroes.Length);
                fs.Flush();
            }
        }

        private static void UmountDisk(string diskName)
        {
            try
            {
                RunCommand("umount", $"-l /dev/{diskName}", out _, out _);
                for (int i = 1; i <= 9; i++)
                {
                    RunCommand("umount", $"-l /dev/{diskName}{i}", out _, out _);
                    RunCommand("umount", $"-l /dev/{diskName}p{i}", out _, out _);
                }
                var lines = File.ReadAllLines("/proc/mounts");
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && parts[0].Contains($"/dev/{diskName}"))
                    {
                        RunCommand("umount", $"-l {parts[0]}", out _, out _);
                    }
                }
            }
            catch {}
        }

        private static void DecompressAndWrite(string xzPath, string xzcatPath, Stream diskStream)
        {
            var psi = new ProcessStartInfo(xzcatPath, $"\"{xzPath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                using var outStream = proc.StandardOutput.BaseStream;
                outStream.CopyTo(diskStream);
                proc.WaitForExit();
            }
        }

        // Plugson JSON operations
        private static string GetConfigPath()
        {
            string targetDir = string.IsNullOrEmpty(_plugsonMountPoint) ? "." : _plugsonMountPoint;
            return Path.Combine(targetDir, "ventoy", "ventoy.json");
        }

        private static JsonObject LoadConfig()
        {
            string path = GetConfigPath();
            if (File.Exists(path))
            {
                try
                {
                    string content = File.ReadAllText(path);
                    var node = JsonNode.Parse(content);
                    if (node is JsonObject obj) return obj;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading ventoy.json: {ex.Message}");
                }
            }
            return new JsonObject();
        }

        private static void SaveConfig(JsonObject obj)
        {
            string path = GetConfigPath();
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var options = new JsonSerializerOptions { WriteIndented = true };
                string content = obj.ToJsonString(options);
                File.WriteAllText(path, content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing ventoy.json: {ex.Message}");
            }
        }

        private static IResult HandlePlugsonGet(string key)
        {
            var config = LoadConfig();
            var response = new JsonObject();
            if (config.TryGetPropertyValue(key, out var valNode))
            {
                response[key] = valNode?.DeepClone();
            }
            else
            {
                response[key] = new JsonObject();
            }
            return Results.Json(response);
        }

        private static IResult HandlePlugsonSave(string key, JsonElement root)
        {
            var config = LoadConfig();
            if (root.TryGetProperty(key, out var valElem))
            {
                var node = JsonNode.Parse(valElem.GetRawText());
                config[key] = node;
            }
            else
            {
                var node = JsonNode.Parse(root.GetRawText()) as JsonObject;
                if (node != null)
                {
                    node.Remove("method");
                    node.Remove("token");

                    if (node.TryGetPropertyValue("index", out var idxNode))
                    {
                        int idx = idxNode?.GetValue<int>() ?? 0;
                        node.Remove("index");
                        if (config.TryGetPropertyValue(key, out var arrNode) && arrNode is JsonArray arr && idx >= 0 && idx < arr.Count)
                        {
                            arr[idx] = node.DeepClone();
                        }
                    }
                    else
                    {
                        config[key] = node.DeepClone();
                    }
                }
            }
            SaveConfig(config);
            return Results.Json(new { result = "success" });
        }

        private static IResult HandlePlugsonAdd(string key, JsonElement root)
        {
            var config = LoadConfig();
            if (!config.TryGetPropertyValue(key, out var arrNode) || arrNode is not JsonArray arr)
            {
                arr = new JsonArray();
                config[key] = arr;
            }

            var item = JsonNode.Parse(root.GetRawText()) as JsonObject;
            if (item != null)
            {
                item.Remove("method");
                item.Remove("token");
                arr.Add(item.DeepClone());
            }

            SaveConfig(config);
            return Results.Json(new { result = "success" });
        }

        private static IResult HandlePlugsonDel(string key, JsonElement root)
        {
            var config = LoadConfig();
            if (config.TryGetPropertyValue(key, out var arrNode) && arrNode is JsonArray arr)
            {
                if (root.TryGetProperty("index", out var idxElem) && idxElem.TryGetInt32(out int idx))
                {
                    if (idx >= 0 && idx < arr.Count)
                    {
                        arr.RemoveAt(idx);
                        SaveConfig(config);
                    }
                }
            }
            return Results.Json(new { result = "success" });
        }

        private static IResult HandlePlugsonCheckExist(JsonElement root)
        {
            string relativePath = root.GetProperty("path").GetString() ?? string.Empty;
            string mnt = string.IsNullOrEmpty(_plugsonMountPoint) ? "." : _plugsonMountPoint;
            string fullPath = Path.Combine(mnt, relativePath.TrimStart('/'));
            bool exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            return Results.Json(new { exist = exists ? 1 : 0 });
        }

        private static IResult HandlePlugsonHandshake()
        {
            var response = new JsonObject();
            response["status"] = 0;
            response["save_error"] = 0;

            string[] pluginNames = { "control", "theme", "persistence", "auto_install", "menu_alias", "menu_tip", "menu_class", "auto_memdisk", "image_list", "password", "conf_replace", "dud", "injection", "hotkey" };
            foreach (var name in pluginNames)
            {
                response[$"exist_{name}"] = new JsonArray { 1, 1, 1, 1 };
            }
            return Results.Json(response);
        }

        private static IResult GetPlugsonDeviceInfo()
        {
            string mnt = string.IsNullOrEmpty(_plugsonMountPoint) ? "." : _plugsonMountPoint;
            string model = "Ventoy USB Partition";
            string sizeStr = "Unknown Capacity";
            string fsName = "exFAT";
            int style = 0;
            int secureBoot = 0;
            string ver = GetVentoyVersion();

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (mnt.Length >= 2 && mnt[1] == ':')
                    {
                        char letter = mnt[0];
                        var driveInfo = new DriveInfo(letter.ToString());
                        model = driveInfo.VolumeLabel + $" (Drive {letter}:)";
                        sizeStr = FormatSize(driveInfo.TotalSize);
                        fsName = driveInfo.DriveFormat;

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

                            if (success)
                            {
                                uint diskNum = extents.Extent.DiskNumber;
                                var tempDisk = new DiskInfo { Name = $"PhysicalDrive{diskNum}" };
                                DetectVentoyWindows(tempDisk, letter);
                                style = tempDisk.VtoyPartStyle;
                                secureBoot = tempDisk.VtoySecureBoot;
                                ver = string.IsNullOrEmpty(tempDisk.VtoyVer) ? ver : tempDisk.VtoyVer;
                            }
                        }
                    }
                }
                else
                {
                    string absMnt = Path.GetFullPath(mnt);
                    string partitionDevice = string.Empty;
                    var lines = File.ReadAllLines("/proc/mounts");
                    foreach (var line in lines)
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && Path.GetFullPath(parts[1]) == absMnt)
                        {
                            partitionDevice = parts[0];
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(partitionDevice))
                    {
                        string resolved = partitionDevice;
                        if (File.Exists(partitionDevice))
                        {
                            var target = File.ResolveLinkTarget(partitionDevice, true);
                            if (target != null) resolved = target.FullName;
                        }
                        string partName = Path.GetFileName(resolved);

                        string parentDir = Path.GetFullPath(Path.Combine($"/sys/class/block/{partName}", ".."));
                        string diskName = Path.GetFileName(parentDir);

                        var psi = new ProcessStartInfo("lsblk", $"-J -b -o NAME,MODEL,SIZE,FSTYPE /dev/{diskName}")
                        {
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var proc = Process.Start(psi))
                        {
                            if (proc != null)
                            {
                                string output = proc.StandardOutput.ReadToEnd();
                                proc.WaitForExit();
                                using (var doc = JsonDocument.Parse(output))
                                {
                                    if (doc.RootElement.TryGetProperty("blockdevices", out var devices) && devices.GetArrayLength() > 0)
                                    {
                                        var dev = devices[0];
                                        model = dev.TryGetProperty("model", out var mProp) ? mProp.GetString() ?? model : model;
                                        long size = dev.GetProperty("size").GetInt64();
                                        sizeStr = FormatSize(size);

                                        if (dev.TryGetProperty("children", out var children))
                                        {
                                            foreach (var child in children.EnumerateArray())
                                            {
                                                if (child.GetProperty("name").GetString() == partName)
                                                {
                                                    fsName = child.TryGetProperty("fstype", out var fsProp) ? fsProp.GetString() ?? fsName : fsName;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        var tempDisk = new DiskInfo { Name = diskName };
                        DetectVentoy(tempDisk);
                        style = tempDisk.VtoyPartStyle;
                        secureBoot = tempDisk.VtoySecureBoot;
                        ver = string.IsNullOrEmpty(tempDisk.VtoyVer) ? ver : tempDisk.VtoyVer;
                    }
                }
            }
            catch { }

            return Results.Json(new
            {
                dev_name = model,
                dev_capacity = sizeStr,
                dev_fs = fsName,
                ventoy_ver = ver,
                part_style = style,
                secure_boot = secureBoot
            });
        }

        public static void Main(string[] args)
        {
            // Parse CLI arguments
            string mode = "ventoy2disk"; // default mode
            string hostIp = "127.0.0.1";
            int port = 24680;

            if (args.Length > 0)
            {
                mode = args[0].ToLowerInvariant();
            }
            if (args.Length > 1)
            {
                hostIp = args[1];
            }
            if (args.Length > 2 && int.TryParse(args[2], out int p))
            {
                port = p;
            }
            if (args.Length > 3 && mode == "plugson")
            {
                _plugsonMountPoint = args[3];
            }

            if (mode != "ventoy2disk" && mode != "plugson")
            {
                Console.WriteLine("Usage: dotnet Ventoy2DiskDotNet.dll [ventoy2disk|plugson] [ip] [port] [mount_point]");
                return;
            }

            var builder = WebApplication.CreateBuilder(new string[0]);
            builder.WebHost.UseUrls($"http://{hostIp}:{port}");
            var app = builder.Build();

            // Set static file mapping
            string webDir = "";
            if (mode == "plugson")
            {
                webDir = "/root/ventoy-conv/Ventoy/Plugson/www";
                if (!Directory.Exists(webDir))
                {
                    webDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "www");
                }
            }
            else
            {
                webDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            }

            app.UseFileServer(new FileServerOptions
            {
                FileProvider = new PhysicalFileProvider(webDir),
                RequestPath = "",
                EnableDefaultFiles = true
            });

            // Endpoint for JSON POST handlers
            app.MapPost("/vtoy/json", async (HttpContext context) =>
            {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var body = await reader.ReadToEndAsync();
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement;
                        string method = root.GetProperty("method").GetString() ?? string.Empty;

                        if (method != "sysinfo")
                        {
                            string token = root.TryGetProperty("token", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;
                            if (token != _curServerToken)
                            {
                                return Results.Json(new { result = "token_error" });
                            }
                        }

                        switch (method)
                        {
                            case "sysinfo":
                                return Results.Json(new
                                {
                                    token = _curServerToken,
                                    language = _curLanguage,
                                    ventoy_ver = GetVentoyVersion(),
                                    partstyle = _curPartStyle,
                                    busy = _percent < 100,
                                    process_disk = _processDisk,
                                    process_type = _processType
                                });

                            case "sel_language":
                                _curLanguage = root.GetProperty("language").GetString() ?? "en-US";
                                return Results.Json(new { result = "success" });

                            case "sel_partstyle":
                                _curPartStyle = root.GetProperty("partstyle").GetInt32();
                                return Results.Json(new { result = "success" });

                            case "refresh_device":
                                return Results.Json(new { result = "success" });

                            case "get_dev_list":
                                bool alldev = root.TryGetProperty("alldev", out var allProp) && allProp.GetUInt32() == 1;
                                var disks = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? GetWindowsDisks(alldev) : GetLinuxDisks(alldev);
                                return Results.Json(new
                                {
                                    list = disks.Select(d => new
                                    {
                                        name = d.Name,
                                        model = d.Model,
                                        size = d.HumanSize,
                                        vtoy_valid = d.VtoyValid,
                                        vtoy_ver = d.VtoyVer,
                                        vtoy_secure_boot = d.VtoySecureBoot,
                                        vtoy_partstyle = d.VtoyPartStyle
                                    })
                                });

                            case "get_percent":
                                lock (_progressLock)
                                {
                                    return Results.Json(new
                                    {
                                        result = _processResult,
                                        process_disk = _processDisk,
                                        process_type = _processType,
                                        percent = _percent
                                    });
                                }

                            case "install":
                            case "update":
                            case "clean":
                                string diskName = root.GetProperty("disk").GetString() ?? string.Empty;
                                int style = root.TryGetProperty("partstyle", out var styleProp) ? styleProp.GetInt32() : _curPartStyle;
                                int secureBoot = root.TryGetProperty("secure_boot", out var sbProp) ? sbProp.GetInt32() : 0;
                                string reserveSpace = root.TryGetProperty("reserve_space", out var rsvProp) ? (rsvProp.GetString() ?? "0") : "0";
                                string fsType = root.TryGetProperty("fs", out var fsProp) ? (fsProp.GetString() ?? "exfat") : "exfat";

                                StartBackgroundOperation(method, diskName, style, secureBoot, reserveSpace, fsType);
                                return Results.Json(new { result = "success" });

                            // PLUGSON Configurator Endpoints
                            case "handshake":
                                return HandlePlugsonHandshake();

                            case "device_info":
                                return GetPlugsonDeviceInfo();

                            case "check_exist":
                            case "check_exist2":
                                return HandlePlugsonCheckExist(root);

                            case string m when m.StartsWith("get_"):
                                return HandlePlugsonGet(m.Substring(4));

                            case string m when m.StartsWith("save_"):
                                return HandlePlugsonSave(m.Substring(5), root);

                            case string m when m.EndsWith("_add"):
                                return HandlePlugsonAdd(m.Substring(0, m.Length - 4), root);

                            case string m when m.EndsWith("_del"):
                                return HandlePlugsonDel(m.Substring(0, m.Length - 4), root);

                            default:
                                return Results.Json(new { result = "invalid_method" });
                        }
                    }
                }
            });

            Console.WriteLine("");
            Console.WriteLine("===============================================================");
            Console.WriteLine($"  Ventoy .NET Web GUI Server ({mode}) has started...");
            Console.WriteLine($"  Please open your browser and visit: http://{hostIp}:{port}");
            Console.WriteLine("===============================================================");
            Console.WriteLine("");

            app.Run();
        }
    }
}
