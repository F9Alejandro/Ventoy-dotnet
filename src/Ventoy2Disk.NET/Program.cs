using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ventoy2Disk.NET
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("           Ventoy2Disk.NET - Cross-Platform       ");
            Console.WriteLine("==================================================");
            Console.WriteLine();

            string diskPath = "";
            bool secureBoot = true;
            bool useGpt = false;
            bool isUpdate = false;
            bool listOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-d" || args[i] == "--disk")
                {
                    if (i + 1 < args.Length)
                    {
                        diskPath = args[++i];
                    }
                }
                else if (args[i] == "-s" || args[i] == "--secure-boot")
                {
                    secureBoot = true;
                }
                else if (args[i] == "--no-secure-boot")
                {
                    secureBoot = false;
                }
                else if (args[i] == "-g" || args[i] == "--gpt")
                {
                    useGpt = true;
                }
                else if (args[i] == "-u" || args[i] == "--update")
                {
                    isUpdate = true;
                }
                else if (args[i] == "-l" || args[i] == "--list")
                {
                    listOnly = true;
                }
                else if (args[i] == "-h" || args[i] == "--help")
                {
                    PrintHelp();
                    return 0;
                }
            }

            if (listOnly)
            {
                ListDrives();
                return 0;
            }

            if (string.IsNullOrEmpty(diskPath))
            {
                ListDrives();
                Console.Write("Enter disk path (e.g. /dev/sdb or \\\\.\\PhysicalDrive1): ");
                diskPath = Console.ReadLine()?.Trim() ?? "";
                if (string.IsNullOrEmpty(diskPath))
                {
                    Console.WriteLine("Error: Disk path is required.");
                    return 1;
                }
            }

            string? bootImgPath = FindAsset("boot/boot.img");
            string? coreImgPath = FindAsset("boot/core.img.xz");
            string? diskImgPath = FindAsset("ventoy/ventoy.disk.img.xz");

            if (bootImgPath == null || coreImgPath == null || diskImgPath == null)
            {
                Console.WriteLine("Error: Required Ventoy asset files (boot.img, core.img.xz, ventoy.disk.img.xz) could not be found.");
                Console.WriteLine("Make sure you are running this tool near a Ventoy installation structure or Ventoy/INSTALL folder.");
                return 1;
            }

            Console.WriteLine($"Found Asset boot.img: {bootImgPath}");
            Console.WriteLine($"Found Asset core.img.xz: {coreImgPath}");
            Console.WriteLine($"Found Asset ventoy.disk.img.xz: {diskImgPath}");

            try
            {
                if (isUpdate)
                {
                    UpdateVentoy(diskPath, bootImgPath, coreImgPath, diskImgPath, secureBoot, useGpt);
                }
                else
                {
                    InstallVentoy(diskPath, bootImgPath, coreImgPath, diskImgPath, secureBoot, useGpt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error occurred: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }

            return 0;
        }

        static void PrintHelp()
        {
            Console.WriteLine("Usage: Ventoy2Disk.NET [options]");
            Console.WriteLine("Options:");
            Console.WriteLine("  -d, --disk <path>       Target disk path (/dev/sdX on Linux or \\\\.\\PhysicalDriveX on Windows)");
            Console.WriteLine("  -s, --secure-boot       Enable secure boot support (default)");
            Console.WriteLine("  --no-secure-boot        Disable secure boot support");
            Console.WriteLine("  -g, --gpt               Use GPT partition style (default: MBR)");
            Console.WriteLine("  -u, --update            Update Ventoy on disk without formatting partition 1");
            Console.WriteLine("  -l, --list              List all physical disks on the system");
            Console.WriteLine("  -h, --help              Show help information");
        }

        static void ListDrives()
        {
            Console.WriteLine("Scanning physical disks...");
            var disks = DiskService.ListDisks();
            if (disks.Count == 0)
            {
                Console.WriteLine("No physical disks found (or run with admin/root privileges).");
                return;
            }
            for (int i = 0; i < disks.Count; i++)
            {
                Console.WriteLine($"[{i}] {disks[i]}");
            }
            Console.WriteLine();
        }

        static string? FindAsset(string relativePath)
        {
            string[] searchPaths = {
                Path.Combine(Directory.GetCurrentDirectory(), relativePath),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), "Ventoy", "INSTALL", relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "Ventoy", "INSTALL", relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "Ventoy", "INSTALL", relativePath),
                Path.Combine("/root/ventoy-dotnet/Ventoy/INSTALL", relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "Ventoy2DiskDotNet", "ventoy", relativePath.Replace("ventoy/", ""))
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        static void InstallVentoy(string diskPath, string bootImgPath, string coreImgPath, string diskImgPath, bool secureBoot, bool useGpt)
        {
            Console.WriteLine($"Starting Ventoy clean installation on {diskPath} (SecureBoot: {secureBoot}, Style: {(useGpt ? "GPT" : "MBR")})...");

            ulong diskSize = 0;
            var disks = DiskService.ListDisks();
            foreach (var d in disks)
            {
                if (d.Path.Equals(diskPath, StringComparison.OrdinalIgnoreCase))
                {
                    diskSize = d.Size;
                    break;
                }
            }

            if (diskSize == 0)
            {
                Console.WriteLine("Warning: Disk size could not be detected from scan list. Attempting to query size directly...");
                using (var testStream = DiskService.OpenDriveStream(diskPath, false))
                {
                    diskSize = (ulong)testStream.Length;
                }
            }

            Console.WriteLine($"Disk size detected: {diskSize / (1024.0 * 1024.0 * 1024.0):F2} GB ({diskSize} bytes)");

            if (diskSize < 34 * 1024 * 1024)
            {
                throw new Exception("Disk size is too small for Ventoy installation.");
            }

            byte[] bootImgBytes = File.ReadAllBytes(bootImgPath);
            Console.WriteLine("Decompressing stage 1 grub core.img...");
            byte[] coreImgBytes = Decompressor.DecompressXzFile(coreImgPath);
            Console.WriteLine("Decompressing partition 2 VTOYEFI image...");
            byte[] diskImgBytes = Decompressor.DecompressXzFile(diskImgPath);

            if (!secureBoot)
            {
                Console.WriteLine("Modifying partition 2 FAT image for non-secure boot...");
                diskImgBytes = ModifyPart2ImageForNonSecureBoot(diskImgBytes);
            }

            Console.WriteLine("Opening physical disk for write access. (Dismounting volumes if on Windows)...");
            using (var driveStream = DiskService.OpenDriveStream(diskPath, true))
            {
                Console.WriteLine("Disk locked. Formatting partition table...");

                byte[] zero512 = new byte[512];
                driveStream.Position = 0;
                driveStream.Write(zero512, 0, 512);

                byte[] zeroGPT = new byte[33 * 512];
                driveStream.Write(zeroGPT, 0, zeroGPT.Length);

                driveStream.Flush();

                ulong diskSectorCount = diskSize / 512;
                uint reservedSpaceMB = 0;

                uint part1StartSector = 2048;
                uint part1SectorCount = (uint)(diskSectorCount - (reservedSpaceMB * 2048) - (Structures.VentoyEfiPartSize / 512) - part1StartSector);
                uint part2StartSector = part1StartSector + part1SectorCount;

                if (useGpt)
                {
                    var pmbr = PartitionService.CreateProtectiveMbr(diskSize, bootImgBytes);
                    byte[] pmbrBytes = pmbr.Serialize();
                    pmbrBytes[92] = 0x22;

                    driveStream.Position = 0;
                    driveStream.Write(pmbrBytes, 0, 512);

                    var gpt = PartitionService.CreateGpt(diskSize, reservedSpaceMB);
                    byte[] primaryHeaderBytes = gpt.Header.Serialize();
                    byte[] primaryHeaderSector = new byte[512];
                    Array.Copy(primaryHeaderBytes, 0, primaryHeaderSector, 0, primaryHeaderBytes.Length);

                    byte[] partTableBytes = new byte[128 * 128];
                    for (int i = 0; i < 128; i++)
                    {
                        Array.Copy(gpt.Partitions[i].Serialize(), 0, partTableBytes, i * 128, 128);
                    }

                    driveStream.Position = 512;
                    driveStream.Write(primaryHeaderSector, 0, 512);
                    driveStream.Write(partTableBytes, 0, partTableBytes.Length);

                    var backupHeader = PartitionService.CreateBackupGptHeader(gpt.Header, diskSectorCount);
                    byte[] backupHeaderBytes = backupHeader.Serialize();
                    byte[] backupHeaderSector = new byte[512];
                    Array.Copy(backupHeaderBytes, 0, backupHeaderSector, 0, backupHeaderBytes.Length);

                    driveStream.Position = (long)(diskSize - 33 * 512 - 512);
                    driveStream.Write(partTableBytes, 0, partTableBytes.Length);
                    driveStream.Write(backupHeaderSector, 0, 512);

                    coreImgBytes[500] = 0x23;
                    driveStream.Position = 34 * 512;
                    driveStream.Write(coreImgBytes, 0, Math.Min(coreImgBytes.Length, 2014 * 512));
                }
                else
                {
                    var mbr = PartitionService.CreateMbr(diskSize, bootImgBytes, 0, 0x07, reservedSpaceMB);
                    byte[] mbrBytes = mbr.Serialize();

                    driveStream.Position = 0;
                    driveStream.Write(mbrBytes, 0, 512);

                    driveStream.Position = 512;
                    driveStream.Write(coreImgBytes, 0, Math.Min(coreImgBytes.Length, 2047 * 512));
                }

                Console.WriteLine($"Writing Partition 2 image to sector {part2StartSector}...");
                driveStream.Position = (long)(part2StartSector * 512);
                driveStream.Write(diskImgBytes, 0, diskImgBytes.Length);

                driveStream.Flush();
            }

            Console.WriteLine("Partition table and bootloaders written successfully.");
            DiskService.UpdateProperties(diskPath);

            Thread.Sleep(3000);

            Console.WriteLine("Formatting Partition 1 (Ventoy data partition) with exFAT...");
            FormatPartition1(diskPath);

            Console.WriteLine("Ventoy installation completed successfully!");
        }

        static void UpdateVentoy(string diskPath, string bootImgPath, string coreImgPath, string diskImgPath, bool secureBoot, bool useGpt)
        {
            Console.WriteLine($"Starting Ventoy update on {diskPath} (SecureBoot: {secureBoot})...");

            ulong diskSize = 0;
            ulong part2StartSector = 0;

            using (var driveStream = DiskService.OpenDriveStream(diskPath, false))
            {
                diskSize = (ulong)driveStream.Length;

                byte[] mbrBytes = new byte[512];
                driveStream.Position = 0;
                driveStream.Read(mbrBytes, 0, 512);

                var mbr = Structures.MbrHead.Deserialize(mbrBytes);
                if (mbr.PartTbl[0].FsFlag == 0xEE)
                {
                    byte[] partTableBytes = new byte[128 * 128];
                    driveStream.Position = 2 * 512;
                    driveStream.Read(partTableBytes, 0, partTableBytes.Length);

                    byte[] entryBytes = new byte[128];
                    Array.Copy(partTableBytes, 128, entryBytes, 0, 128);
                    
                    part2StartSector = BitConverter.ToUInt64(entryBytes, 32);
                    useGpt = true;
                }
                else
                {
                    part2StartSector = mbr.PartTbl[1].StartSectorId;
                    useGpt = false;
                }
            }

            if (part2StartSector == 0)
            {
                throw new Exception("Error: Could not locate existing Ventoy partition 2 start sector. Is Ventoy installed on this drive?");
            }

            Console.WriteLine($"Existing Ventoy Partition 2 starts at sector: {part2StartSector}");

            byte[] bootImgBytes = File.ReadAllBytes(bootImgPath);
            byte[] coreImgBytes = Decompressor.DecompressXzFile(coreImgPath);
            byte[] diskImgBytes = Decompressor.DecompressXzFile(diskImgPath);

            if (!secureBoot)
            {
                diskImgBytes = ModifyPart2ImageForNonSecureBoot(diskImgBytes);
            }

            using (var driveStream = DiskService.OpenDriveStream(diskPath, true))
            {
                if (useGpt)
                {
                    byte[] pmbrBytes = PartitionService.CreateProtectiveMbr(diskSize, bootImgBytes).Serialize();
                    pmbrBytes[92] = 0x22;

                    driveStream.Position = 0;
                    driveStream.Write(pmbrBytes, 0, 512);

                    coreImgBytes[500] = 0x23;
                    driveStream.Position = 34 * 512;
                    driveStream.Write(coreImgBytes, 0, Math.Min(coreImgBytes.Length, 2014 * 512));
                }
                else
                {
                    byte[] existingMbr = new byte[512];
                    driveStream.Position = 0;
                    driveStream.Read(existingMbr, 0, 512);

                    Array.Copy(bootImgBytes, 0, existingMbr, 0, 446);
                    
                    Guid guid = Guid.NewGuid();
                    byte[] guidBytes = guid.ToByteArray();
                    Array.Copy(guidBytes, 0, existingMbr, 0x180, 16);
                    Array.Copy(BitConverter.GetBytes(BitConverter.ToUInt32(guidBytes, 0)), 0, existingMbr, 0x1B8, 4);

                    driveStream.Position = 0;
                    driveStream.Write(existingMbr, 0, 512);

                    driveStream.Position = 512;
                    driveStream.Write(coreImgBytes, 0, Math.Min(coreImgBytes.Length, 2047 * 512));
                }

                driveStream.Position = (long)(part2StartSector * 512);
                driveStream.Write(diskImgBytes, 0, diskImgBytes.Length);

                driveStream.Flush();
            }

            DiskService.UpdateProperties(diskPath);
            Console.WriteLine("Ventoy update completed successfully!");
        }

        static byte[] ModifyPart2ImageForNonSecureBoot(byte[] fatImageBytes)
        {
            using (var diskStream = new MemoryStream(fatImageBytes))
            {
                using (var fs = new DiscUtils.Fat.FatFileSystem(diskStream))
                {
                    if (fs.FileExists(@"EFI\BOOT\grubx64_real.efi"))
                    {
                        byte[] grubBytes;
                        using (var src = fs.OpenFile(@"EFI\BOOT\grubx64_real.efi", FileMode.Open, FileAccess.Read))
                        using (var ms = new MemoryStream())
                        {
                            src.CopyTo(ms);
                            grubBytes = ms.ToArray();
                        }

                        string[] filesToDelete = {
                            @"EFI\BOOT\BOOTX64.EFI",
                            @"EFI\BOOT\grubx64.efi",
                            @"EFI\BOOT\grubx64_real.efi",
                            @"EFI\BOOT\MokManager.efi",
                            @"EFI\BOOT\mmx64.efi",
                            @"ENROLL_THIS_KEY_IN_MOKMANAGER.cer",
                            @"EFI\BOOT\grub.efi"
                        };

                        foreach (var file in filesToDelete)
                        {
                            if (fs.FileExists(file))
                            {
                                try { fs.DeleteFile(file); } catch { }
                            }
                        }

                        using (var dst = fs.OpenFile(@"EFI\BOOT\BOOTX64.EFI", FileMode.Create, FileAccess.Write))
                        {
                            dst.Write(grubBytes, 0, grubBytes.Length);
                        }
                    }

                    if (fs.FileExists(@"EFI\BOOT\grubia32_real.efi"))
                    {
                        byte[] grubBytes;
                        using (var src = fs.OpenFile(@"EFI\BOOT\grubia32_real.efi", FileMode.Open, FileAccess.Read))
                        using (var ms = new MemoryStream())
                        {
                            src.CopyTo(ms);
                            grubBytes = ms.ToArray();
                        }

                        string[] filesToDelete = {
                            @"EFI\BOOT\BOOTIA32.EFI",
                            @"EFI\BOOT\grubia32.efi",
                            @"EFI\BOOT\grubia32_real.efi",
                            @"EFI\BOOT\mmia32.efi"
                        };

                        foreach (var file in filesToDelete)
                        {
                            if (fs.FileExists(file))
                            {
                                try { fs.DeleteFile(file); } catch { }
                            }
                        }

                        using (var dst = fs.OpenFile(@"EFI\BOOT\BOOTIA32.EFI", FileMode.Create, FileAccess.Write))
                        {
                            dst.Write(grubBytes, 0, grubBytes.Length);
                        }
                    }
                }
                return diskStream.ToArray();
            }
        }

        static void FormatPartition1(string diskPath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string partPath = diskPath;
                if (diskPath.Contains("nvme"))
                {
                    partPath += "p1";
                }
                else
                {
                    partPath += "1";
                }

                Console.WriteLine($"Formatting Linux partition {partPath} to exFAT...");
                
                var psi = new ProcessStartInfo
                {
                    FileName = "mkfs.exfat",
                    Arguments = $"-n Ventoy {partPath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                try
                {
                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            p.WaitForExit();
                            if (p.ExitCode == 0)
                            {
                                Console.WriteLine("mkfs.exfat formatted partition successfully.");
                                return;
                            }
                            Console.WriteLine($"System mkfs.exfat returned code {p.ExitCode}. Attempting bundled mkexfatfs...");
                        }
                    }
                }
                catch
                {
                    Console.WriteLine("System mkfs.exfat failed or not found. Attempting bundled mkexfatfs...");
                }

                string? bundledMkexfatfs = FindAsset("tool/x86_64/mkexfatfs");
                if (bundledMkexfatfs == null)
                {
                    bundledMkexfatfs = FindAsset("tool/i386/mkexfatfs");
                }

                if (bundledMkexfatfs != null)
                {
                    try
                    {
                        Process.Start("chmod", $"+x {bundledMkexfatfs}").WaitForExit();
                    }
                    catch { }

                    psi = new ProcessStartInfo
                    {
                        FileName = bundledMkexfatfs,
                        Arguments = $"-n Ventoy {partPath}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };

                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            p.WaitForExit();
                            if (p.ExitCode == 0)
                            {
                                Console.WriteLine("Bundled mkexfatfs formatted partition successfully.");
                                return;
                            }
                            throw new Exception($"Bundled mkexfatfs failed with exit code: {p.ExitCode}");
                        }
                    }
                }

                throw new Exception($"Could not format partition {partPath} using mkfs.exfat or mkexfatfs.");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string numStr = diskPath.Replace(@"\\.\PhysicalDrive", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!int.TryParse(numStr, out int diskNum))
                {
                    throw new Exception($"Could not extract disk number from path {diskPath}");
                }

                Console.WriteLine($"Formatting Windows Disk {diskNum} Partition 1 to exFAT...");

                string scriptPath = Path.Combine(Path.GetTempPath(), "ventoy_format.txt");
                File.WriteAllText(scriptPath, $@"
select disk {diskNum}
select partition 1
format fs=exfat quick label=Ventoy
assign
exit
");

                var psi = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = $"/s \"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                using (var p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit();
                        try { File.Delete(scriptPath); } catch { }

                        if (p.ExitCode == 0)
                        {
                            Console.WriteLine("DiskPart successfully formatted partition 1.");
                            return;
                        }
                        throw new Exception($"Diskpart failed with exit code: {p.ExitCode}");
                    }
                }
            }
            throw new PlatformNotSupportedException();
        }
    }
}
