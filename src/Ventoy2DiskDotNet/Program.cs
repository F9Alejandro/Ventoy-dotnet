using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ventoy2DiskDotNet
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("          Ventoy .NET Installer (Cross-Platform)  ");
            Console.WriteLine("==================================================");

            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            {
                PrintUsage();
                return 0;
            }

            try
            {
                if (args.Contains("--list") || args.Contains("-l"))
                {
                    ListDrives();
                    return 0;
                }

                bool isInstall = args.Contains("--install") || args.Contains("-i");
                bool isUpdate = args.Contains("--update") || args.Contains("-u");

                if (!isInstall && !isUpdate)
                {
                    Console.WriteLine("Error: Must specify either --install (-i) or --update (-u).");
                    PrintUsage();
                    return 1;
                }

                // Parse device
                string? deviceArg = GetArgValue(args, "--device", "-d");
                if (string.IsNullOrEmpty(deviceArg))
                {
                    Console.WriteLine("Error: Target device not specified. Use --device (-d) followed by drive number or path.");
                    return 1;
                }

                // Parse style
                string styleArg = (GetArgValue(args, "--style", "-s") ?? "mbr").ToLower();
                if (styleArg != "mbr" && styleArg != "gpt")
                {
                    Console.WriteLine("Error: Partition style must be 'mbr' or 'gpt'.");
                    return 1;
                }
                bool isGpt = (styleArg == "gpt");

                // Parse secureboot
                string secureBootArg = (GetArgValue(args, "--secureboot") ?? "yes").ToLower();
                bool secureBoot = (secureBootArg == "yes" || secureBootArg == "true" || secureBootArg == "1");

                // Parse filesystem
                string filesystem = (GetArgValue(args, "--filesystem", "-f") ?? "exfat").ToLower();
                if (filesystem != "exfat" && filesystem != "ntfs")
                {
                    Console.WriteLine("Error: Filesystem must be 'exfat' or 'ntfs'.");
                    return 1;
                }

                // Find matching disk
                var disks = DiskService.GetPhysicalDisks();
                PhysicalDisk? targetDisk = null;

                if (int.TryParse(deviceArg, out int diskNum))
                {
                    targetDisk = disks.FirstOrDefault(d => d.Number == diskNum);
                }
                else
                {
                    targetDisk = disks.FirstOrDefault(d => d.Path.Equals(deviceArg, StringComparison.OrdinalIgnoreCase) || 
                                                           d.SystemName.Equals(deviceArg, StringComparison.OrdinalIgnoreCase));
                }

                if (targetDisk == null)
                {
                    Console.WriteLine($"Error: Target device '{deviceArg}' not found.");
                    Console.WriteLine("Use --list to see available physical drives.");
                    return 1;
                }

                Console.WriteLine($"Selected Target Device: {targetDisk}");
                Console.WriteLine($"Partition Style:       {(isGpt ? "GPT" : "MBR")}");
                Console.WriteLine($"Secure Boot Support:   {(secureBoot ? "Enabled" : "Disabled")}");
                Console.WriteLine($"Format Filesystem:     {filesystem.ToUpper()}");
                Console.WriteLine();

                if (isInstall)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("WARNING: ALL DATA ON THE TARGET DISK WILL BE DESTROYED!");
                    Console.WriteLine("This action cannot be undone. Are you sure you want to proceed?");
                    Console.ResetColor();
                    Console.Write("Type 'YES' to confirm: ");
                    string? confirm = Console.ReadLine();
                    if (confirm != "YES")
                    {
                        Console.WriteLine("Operation cancelled by user.");
                        return 0;
                    }

                    ExecuteInstall(targetDisk, isGpt, secureBoot, filesystem);
                }
                else if (isUpdate)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("WARNING: This will update the Ventoy bootloader and system files on Partition 2.");
                    Console.WriteLine("Your files on Partition 1 should remain safe, but backups are always recommended.");
                    Console.ResetColor();
                    Console.Write("Type 'YES' to confirm: ");
                    string? confirm = Console.ReadLine();
                    if (confirm != "YES")
                    {
                        Console.WriteLine("Operation cancelled by user.");
                        return 0;
                    }

                    ExecuteUpdate(targetDisk, secureBoot);
                }

                Console.WriteLine("Operation completed successfully!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Fatal Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
                return 1;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  --list, -l                     List all available physical drives.");
            Console.WriteLine("  --install, -i                  Install Ventoy from scratch (wipes disk).");
            Console.WriteLine("  --update, -u                   Update existing Ventoy installation.");
            Console.WriteLine("  -d, --device <num|path>        Physical disk number (e.g. 1) or system path (e.g. /dev/sdb).");
            Console.WriteLine("  -s, --style <mbr|gpt>          Partition table style (default: MBR).");
            Console.WriteLine("  --secureboot <yes|no>          Enable or disable secure boot EFI loaders (default: yes).");
            Console.WriteLine("  -f, --filesystem <exfat|ntfs>  Filesystem type for Partition 1 (default: exfat).");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  dotnet run -- --list");
            Console.WriteLine("  dotnet run -- --install --device 1 --style gpt --secureboot no --filesystem ntfs");
            Console.WriteLine("  dotnet run -- --update --device /dev/sdb --secureboot yes");
        }

        static string? GetArgValue(string[] args, string flag, string? alternative = null)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) || 
                    (alternative != null && args[i].Equals(alternative, StringComparison.OrdinalIgnoreCase)))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        static void ListDrives()
        {
            Console.WriteLine("Scanning physical drives...");
            var disks = DiskService.GetPhysicalDisks();
            if (disks.Count == 0)
            {
                Console.WriteLine("No physical drives detected (make sure you are running as Administrator/root).");
                return;
            }

            foreach (var disk in disks)
            {
                Console.WriteLine($" - {disk}");
            }
        }

        static void ExecuteInstall(PhysicalDisk disk, bool isGpt, bool secureBoot, string filesystem)
        {
            string baseDir = AppContext.BaseDirectory;
            string bootImgPath = Path.Combine(baseDir, "boot", "boot.img");
            string coreImgXzPath = Path.Combine(baseDir, "boot", "core.img.xz");
            string diskImgXzPath = Path.Combine(baseDir, "ventoy", "ventoy.disk.img.xz");

            // Verify assets
            VerifyAssetExists(bootImgPath);
            VerifyAssetExists(coreImgXzPath);
            VerifyAssetExists(diskImgXzPath);

            Console.WriteLine("Step 1: Reading and decompressing binary bootloader assets...");
            byte[] bootImgBytes = File.ReadAllBytes(bootImgPath);
            byte[] coreImgBytes = Decompressor.DecompressXz(coreImgXzPath);
            byte[] fatImgBytes = Decompressor.DecompressXz(diskImgXzPath);

            if (!secureBoot)
            {
                fatImgBytes = ModifySecureBoot(fatImgBytes);
            }

            Console.WriteLine($"Step 2: Opening target physical drive stream: {disk.Path}");
            using (Stream driveStream = DiskService.OpenWriteHandle(disk))
            {
                Console.WriteLine("Step 3: Writing MBR/GPT partition tables...");
                if (!isGpt)
                {
                    // MBR Style installation
                    // FsFlag = 0x07 (exFAT/NTFS)
                    MbrHead mbr = PartitionService.FillMbr(disk.SizeInBytes, bootImgBytes, 0x07);
                    byte[] mbrBytes = mbr.Serialize();
                    driveStream.Position = 0;
                    driveStream.Write(mbrBytes, 0, 512);

                    Console.WriteLine("Step 4: Writing grub stage 2 core.img to sector 1...");
                    driveStream.Position = 512;
                    byte[] alignedCore = new byte[1024 * 1024 - 512];
                    Array.Copy(coreImgBytes, 0, alignedCore, 0, Math.Min(coreImgBytes.Length, alignedCore.Length));
                    driveStream.Write(alignedCore, 0, alignedCore.Length);

                    // Partition 2 offset
                    ulong part2StartSector = mbr.PartTbl[1].StartSectorId;
                    Console.WriteLine($"Step 5: Writing VTOYEFI FAT image to partition 2 at LBA {part2StartSector}...");
                    driveStream.Position = (long)(part2StartSector * 512);
                    driveStream.Write(fatImgBytes, 0, fatImgBytes.Length);
                }
                else
                {
                    // GPT Style installation
                    GptInfo gpt = PartitionService.FillGpt(disk.SizeInBytes, bootImgBytes);
                    byte[] gptBytes = gpt.Serialize();
                    driveStream.Position = 0;
                    driveStream.Write(gptBytes, 0, gptBytes.Length);

                    Console.WriteLine("Step 4: Writing grub stage 2 core.img to sector 34 (updating blocklist)...");
                    coreImgBytes[500] = 35;
                    driveStream.Position = 34 * 512;
                    byte[] alignedCore = new byte[1024 * 1024 - 34 * 512];
                    Array.Copy(coreImgBytes, 0, alignedCore, 0, Math.Min(coreImgBytes.Length, alignedCore.Length));
                    driveStream.Write(alignedCore, 0, alignedCore.Length);

                    // Partition 2 offset
                    ulong part2StartSector = gpt.PartTbl[1].StartLBA;
                    Console.WriteLine($"Step 5: Writing VTOYEFI FAT image to partition 2 at LBA {part2StartSector}...");
                    driveStream.Position = (long)(part2StartSector * 512);
                    driveStream.Write(fatImgBytes, 0, fatImgBytes.Length);

                    // Backup GPT
                    Console.WriteLine("Step 6: Writing Backup GPT header and partition array to end of disk...");
                    GptHeader backupHead = PartitionService.CreateBackupGptHeader(gpt);
                    byte[] backupHeadBytes = backupHead.Serialize();

                    byte[] partArrayBytes = new byte[128 * 128];
                    for (int i = 0; i < 128; i++)
                    {
                        byte[] entryBytes = gpt.PartTbl[i].Serialize();
                        Array.Copy(entryBytes, 0, partArrayBytes, i * 128, 128);
                    }

                    ulong backupPartArrayLba = (disk.SizeInBytes / 512) - 33;
                    driveStream.Position = (long)(backupPartArrayLba * 512);
                    driveStream.Write(partArrayBytes, 0, partArrayBytes.Length);

                    ulong backupHeaderLba = (disk.SizeInBytes / 512) - 1;
                    driveStream.Position = (long)(backupHeaderLba * 512);
                    driveStream.Write(backupHeadBytes, 0, 512);
                }

                driveStream.Flush();
            }

            Console.WriteLine($"Step 7: Formatting Partition 1 with {filesystem.ToUpper()} filesystem...");
            FormatPartition1(disk, filesystem);
        }

        static void ExecuteUpdate(PhysicalDisk disk, bool secureBoot)
        {
            string baseDir = AppContext.BaseDirectory;
            string bootImgPath = Path.Combine(baseDir, "boot", "boot.img");
            string coreImgXzPath = Path.Combine(baseDir, "boot", "core.img.xz");
            string diskImgXzPath = Path.Combine(baseDir, "ventoy", "ventoy.disk.img.xz");

            VerifyAssetExists(bootImgPath);
            VerifyAssetExists(coreImgXzPath);
            VerifyAssetExists(diskImgXzPath);

            Console.WriteLine("Step 1: Reading and decompressing binary bootloader assets...");
            byte[] bootImgBytes = File.ReadAllBytes(bootImgPath);
            byte[] coreImgBytes = Decompressor.DecompressXz(coreImgXzPath);
            byte[] fatImgBytes = Decompressor.DecompressXz(diskImgXzPath);

            if (!secureBoot)
            {
                fatImgBytes = ModifySecureBoot(fatImgBytes);
            }

            Console.WriteLine("Step 2: Checking existing Ventoy installation on the disk...");
            bool isGpt = false;
            ulong part2StartSector = 0;

            using (Stream driveStream = DiskService.OpenWriteHandle(disk))
            {
                byte[] sector0 = new byte[512];
                driveStream.Read(sector0, 0, 512);

                if (sector0[510] != 0x55 || sector0[511] != 0xAA)
                {
                    throw new Exception("Error: The target disk does not have a valid boot sector signature (0x55AA).");
                }

                isGpt = (sector0[446 + 4] == 0xEE);

                if (!isGpt)
                {
                    MbrHead mbr = MbrHead.Deserialize(sector0);
                    part2StartSector = mbr.PartTbl[1].StartSectorId;
                    if (part2StartSector == 0 || mbr.PartTbl[1].FsFlag != 0xEF)
                    {
                        throw new Exception("Error: Existing Ventoy MBR partition table structure not found on this disk.");
                    }

                    Console.WriteLine($"Detected Ventoy MBR installation. VTOYEFI starts at sector {part2StartSector}.");
                    Console.WriteLine("Step 3: Writing MBR boot code...");
                    MbrHead newMbr = PartitionService.FillMbr(disk.SizeInBytes, bootImgBytes, 0x07);
                    newMbr.PartTbl = mbr.PartTbl;
                    byte[] newMbrBytes = newMbr.Serialize();

                    driveStream.Position = 0;
                    driveStream.Write(newMbrBytes, 0, 512);

                    Console.WriteLine("Step 4: Writing grub stage 2 core.img to sector 1...");
                    driveStream.Position = 512;
                    byte[] alignedCore = new byte[1024 * 1024 - 512];
                    Array.Copy(coreImgBytes, 0, alignedCore, 0, Math.Min(coreImgBytes.Length, alignedCore.Length));
                    driveStream.Write(alignedCore, 0, alignedCore.Length);
                }
                else
                {
                    byte[] gptBytes = new byte[17408];
                    driveStream.Position = 0;
                    driveStream.Read(gptBytes, 0, 17408);

                    GptInfo existingGpt = new GptInfo();
                    existingGpt.Mbr = MbrHead.Deserialize(gptBytes);
                    existingGpt.Head = GptHeader.Deserialize(gptBytes.AsSpan(512, 512).ToArray());
                    for (int i = 0; i < 128; i++)
                    {
                        existingGpt.PartTbl[i] = GptPartEntry.Deserialize(gptBytes, 1024 + (i * 128));
                    }

                    part2StartSector = existingGpt.PartTbl[1].StartLBA;
                    if (part2StartSector == 0)
                    {
                        throw new Exception("Error: Existing Ventoy GPT partition table structure not found on this disk.");
                    }

                    Console.WriteLine($"Detected Ventoy GPT installation. VTOYEFI starts at sector {part2StartSector}.");
                    Console.WriteLine("Step 3: Writing GPT primary structures...");
                    
                    GptInfo newGpt = PartitionService.FillGpt(disk.SizeInBytes, bootImgBytes);
                    newGpt.PartTbl = existingGpt.PartTbl;

                    newGpt.Head.DiskGuid = existingGpt.Head.DiskGuid;
                    
                    byte[] partArrayBytes = new byte[128 * 128];
                    for (int i = 0; i < 128; i++)
                    {
                        byte[] entryBytes = newGpt.PartTbl[i].Serialize();
                        Array.Copy(entryBytes, 0, partArrayBytes, i * 128, 128);
                    }
                    newGpt.Head.PartTblCrc = PartitionService.CalculateCrc32(partArrayBytes, 0, partArrayBytes.Length);

                    byte[] newHeadBytes = newGpt.Head.Serialize();
                    Array.Clear(newHeadBytes, 16, 4);
                    newGpt.Head.Crc = PartitionService.CalculateCrc32(newHeadBytes, 0, 92);

                    byte[] newGptBytes = newGpt.Serialize();
                    driveStream.Position = 0;
                    driveStream.Write(newGptBytes, 0, newGptBytes.Length);

                    Console.WriteLine("Step 4: Writing grub stage 2 core.img to sector 34 (updating blocklist)...");
                    coreImgBytes[500] = 35;
                    driveStream.Position = 34 * 512;
                    byte[] alignedCore = new byte[1024 * 1024 - 34 * 512];
                    Array.Copy(coreImgBytes, 0, alignedCore, 0, Math.Min(coreImgBytes.Length, alignedCore.Length));
                    driveStream.Write(alignedCore, 0, alignedCore.Length);

                    Console.WriteLine("Step 5: Writing Backup GPT header and partition array to end of disk...");
                    GptHeader backupHead = PartitionService.CreateBackupGptHeader(newGpt);
                    byte[] backupHeadBytes = backupHead.Serialize();

                    ulong backupPartArrayLba = (disk.SizeInBytes / 512) - 33;
                    driveStream.Position = (long)(backupPartArrayLba * 512);
                    driveStream.Write(partArrayBytes, 0, partArrayBytes.Length);

                    ulong backupHeaderLba = (disk.SizeInBytes / 512) - 1;
                    driveStream.Position = (long)(backupHeaderLba * 512);
                    driveStream.Write(backupHeadBytes, 0, 512);
                }

                Console.WriteLine($"Step 5: Writing VTOYEFI FAT image to partition 2 at LBA {part2StartSector}...");
                driveStream.Position = (long)(part2StartSector * 512);
                driveStream.Write(fatImgBytes, 0, fatImgBytes.Length);

                driveStream.Flush();
            }
        }

        static void VerifyAssetExists(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Required binary asset file not found: {Path.GetFileName(path)}. Build the assets or download the latest release first.");
            }
        }

        static byte[] ModifySecureBoot(byte[] fatImgBytes)
        {
            Console.WriteLine("Secure Boot toggle: disabled. Modifying VTOYEFI FAT image...");
            using (var fatStream = new MemoryStream())
            {
                fatStream.Write(fatImgBytes, 0, fatImgBytes.Length);
                fatStream.Position = 0;

                using (var fs = new DiscUtils.Fat.FatFileSystem(fatStream))
                {
                    string grubx64Path = @"EFI\BOOT\grubx64_real.efi";
                    if (fs.FileExists(grubx64Path))
                    {
                        byte[] grubx64Bytes;
                        using (var fileStream = fs.OpenFile(grubx64Path, FileMode.Open, FileAccess.Read))
                        {
                            grubx64Bytes = new byte[fileStream.Length];
                            fileStream.Read(grubx64Bytes, 0, grubx64Bytes.Length);
                        }

                        DeleteFileIfExists(fs, @"EFI\BOOT\BOOTX64.EFI");
                        DeleteFileIfExists(fs, @"EFI\BOOT\grubx64.efi");
                        DeleteFileIfExists(fs, @"EFI\BOOT\grubx64_real.efi");
                        DeleteFileIfExists(fs, @"EFI\BOOT\MokManager.efi");
                        DeleteFileIfExists(fs, @"EFI\BOOT\mmx64.efi");
                        DeleteFileIfExists(fs, @"ENROLL_THIS_KEY_IN_MOKMANAGER.cer");
                        DeleteFileIfExists(fs, @"EFI\BOOT\grub.efi");

                        using (var fileStream = fs.OpenFile(@"EFI\BOOT\BOOTX64.EFI", FileMode.Create, FileAccess.Write))
                        {
                            fileStream.Write(grubx64Bytes, 0, grubx64Bytes.Length);
                        }
                        Console.WriteLine(" -> Successfully toggled Secure Boot OFF for x64.");
                    }

                    string grubia32Path = @"EFI\BOOT\grubia32_real.efi";
                    if (fs.FileExists(grubia32Path))
                    {
                        byte[] grubia32Bytes;
                        using (var fileStream = fs.OpenFile(grubia32Path, FileMode.Open, FileAccess.Read))
                        {
                            grubia32Bytes = new byte[fileStream.Length];
                            fileStream.Read(grubia32Bytes, 0, grubia32Bytes.Length);
                        }

                        DeleteFileIfExists(fs, @"EFI\BOOT\BOOTIA32.EFI");
                        DeleteFileIfExists(fs, @"EFI\BOOT\grubia32.efi");
                        DeleteFileIfExists(fs, @"EFI\BOOT\grubia32_real.efi");
                        DeleteFileIfExists(fs, @"EFI\BOOT\mmia32.efi");

                        using (var fileStream = fs.OpenFile(@"EFI\BOOT\BOOTIA32.EFI", FileMode.Create, FileAccess.Write))
                        {
                            fileStream.Write(grubia32Bytes, 0, grubia32Bytes.Length);
                        }
                        Console.WriteLine(" -> Successfully toggled Secure Boot OFF for ia32.");
                    }
                }

                return fatStream.ToArray();
            }
        }

        private static void DeleteFileIfExists(DiscUtils.Fat.FatFileSystem fs, string path)
        {
            if (fs.FileExists(path))
            {
                fs.DeleteFile(path);
            }
        }

        static void FormatPartition1(PhysicalDisk disk, string filesystem)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine($"Formatting Partition 1 as {filesystem.ToUpper()} (Windows diskpart)...");
                string fsType = filesystem == "ntfs" ? "ntfs" : "exfat";
                string script = $"select disk {disk.Number}\nselect partition 1\nformat fs={fsType} label=Ventoy quick\n";
                string tempFile = Path.GetTempFileName();
                File.WriteAllText(tempFile, script);

                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("diskpart", $"/s \"{tempFile}\"")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit();
                    string output = proc?.StandardOutput.ReadToEnd() ?? "";
                    Console.WriteLine(output);
                }
                finally
                {
                    File.Delete(tempFile);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Reread partition table
                try
                {
                    var pprobe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("partprobe", disk.Path) { UseShellExecute = false, CreateNoWindow = true });
                    pprobe?.WaitForExit();
                }
                catch {}
                try
                {
                    var bdev = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("blockdev", $"--rereadpt {disk.Path}") { UseShellExecute = false, CreateNoWindow = true });
                    bdev?.WaitForExit();
                }
                catch {}

                // Determine partition path
                string partPath = disk.Path;
                if (partPath.Contains("nvme"))
                {
                    partPath += "p1";
                }
                else
                {
                    partPath += "1";
                }

                Console.WriteLine($"Waiting for partition device file {partPath} to appear...");
                bool partitionFound = false;
                for (int i = 0; i < 20; i++)
                {
                    if (File.Exists(partPath))
                    {
                        partitionFound = true;
                        break;
                    }
                    Thread.Sleep(250);
                }

                if (!partitionFound)
                {
                    throw new Exception($"Error: Partition device file '{partPath}' did not appear in time. Formatting failed.");
                }

                string baseDir = AppContext.BaseDirectory;
                string arch = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "x86_64",
                    Architecture.Arm64 => "aarch64",
                    Architecture.X86 => "i386",
                    _ => "x86_64"
                };

                if (filesystem == "ntfs")
                {
                    string mkntfsPath = Path.Combine(baseDir, "tool", arch, "mkfs.ntfs");
                    if (!File.Exists(mkntfsPath))
                    {
                        string mkntfsAltPath = Path.Combine(baseDir, "tool", arch, "mkntfs");
                        if (File.Exists(mkntfsAltPath))
                        {
                            mkntfsPath = mkntfsAltPath;
                        }
                        else
                        {
                            throw new FileNotFoundException($"mkfs.ntfs tool not found at: {mkntfsPath}");
                        }
                    }

                    // Set executable permission
                    try
                    {
                        var chmodPsi = new System.Diagnostics.ProcessStartInfo("chmod", $"+x \"{mkntfsPath}\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = System.Diagnostics.Process.Start(chmodPsi);
                        proc?.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to set executable permission on mkfs.ntfs: {ex.Message}");
                    }

                    Console.WriteLine($"Formatting Partition 1 ({partPath}) as NTFS using bundled {arch} mkfs.ntfs...");
                    var formatPsi = new System.Diagnostics.ProcessStartInfo(mkntfsPath, $"-f -F -L Ventoy {partPath}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var formatProc = System.Diagnostics.Process.Start(formatPsi);
                    formatProc?.WaitForExit();
                    string output = formatProc?.StandardOutput.ReadToEnd() ?? "";
                    string error = formatProc?.StandardError.ReadToEnd() ?? "";

                    if (formatProc?.ExitCode != 0)
                    {
                        throw new Exception($"Formatting failed with exit code {formatProc?.ExitCode}. Stderr: {error}. Stdout: {output}");
                    }

                    Console.WriteLine("NTFS partition formatted successfully!");
                }
                else
                {
                    string mkexfatfsPath = Path.Combine(baseDir, "tool", arch, "mkexfatfs");
                    if (!File.Exists(mkexfatfsPath))
                    {
                        throw new FileNotFoundException($"mkexfatfs tool not found at: {mkexfatfsPath}");
                    }

                    // Set executable permission
                    try
                    {
                        var chmodPsi = new System.Diagnostics.ProcessStartInfo("chmod", $"+x \"{mkexfatfsPath}\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = System.Diagnostics.Process.Start(chmodPsi);
                        proc?.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to set executable permission on mkexfatfs: {ex.Message}");
                    }

                    Console.WriteLine($"Formatting Partition 1 ({partPath}) as exFAT using bundled {arch} mkexfatfs...");
                    var formatPsi = new System.Diagnostics.ProcessStartInfo(mkexfatfsPath, $"-n Ventoy {partPath}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var formatProc = System.Diagnostics.Process.Start(formatPsi);
                    formatProc?.WaitForExit();
                    string output = formatProc?.StandardOutput.ReadToEnd() ?? "";
                    string error = formatProc?.StandardError.ReadToEnd() ?? "";

                    if (formatProc?.ExitCode != 0)
                    {
                        throw new Exception($"Formatting failed with exit code {formatProc?.ExitCode}. Stderr: {error}. Stdout: {output}");
                    }

                    Console.WriteLine("exFAT partition formatted successfully!");
                }
            }
        }
    }
}
