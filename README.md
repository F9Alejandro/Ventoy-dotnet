# Ventoy .NET Installer (Cross-Platform)

A cross-platform port of the Ventoy Windows installer (`Ventoy2Disk`) rebuilt from scratch in **.NET 8.0** to run natively on Windows and Linux.

## Features

1. **Cross-Platform Drive Scanning**: Lists physical disks on Windows (using Win32 API) and Linux (using `/sys/block` scanning) with sizes, models, and vendors.
2. **Direct Raw Disk Writing**: Writes MBR/GPT partition tables and Stage 2 Grub bootloader directly to raw streams of target devices.
3. **In-Memory Secure Boot Toggle**: Modifies partition 2 (`ventoy.disk.img.xz` FAT image) directly in-memory using `DiscUtils.Fat` before writing to the disk, dynamically removing shim/MokManager files when Secure Boot support is disabled.
4. **Native exFAT Formatting**: Automatically formats partition 1 with exFAT and labels it "Ventoy" using native OS capabilities (via `diskpart` script on Windows and bundled `mkexfatfs` on Linux).

## Prerequisites

- **.NET SDK 8.0** (If the `dotnet` CLI is not in your system `PATH`, you can run it using its absolute path, e.g. `/root/.dotnet/dotnet`).
- On **Linux**, administrative (`sudo`/`root`) privileges are required to write directly to block devices.
- On **Windows**, the application must be run as **Administrator** to acquire raw handles to physical disks.

## Compilation & Standalone Publishing

You can build and publish standalone, self-contained binaries for Windows and Linux. This bundles the `.NET 8.0` runtime so the user does not need to have it installed.

### Build standalone CLI:
```bash
# For Linux x64
dotnet publish src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# For Windows x64
dotnet publish src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Build standalone GUI:
```bash
# For Linux x64
dotnet publish src/Ventoy2DiskAvalonia/Ventoy2DiskAvalonia.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# For Windows x64
dotnet publish src/Ventoy2DiskAvalonia/Ventoy2DiskAvalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Running the Compiled Binaries

Once compiled, navigate to the output `publish` folder (e.g. `src/Ventoy2DiskDotNet/bin/Release/net8.0/linux-x64/publish/` or `src/Ventoy2DiskAvalonia/bin/Release/net8.0/linux-x64/publish/`) to run the binaries.

### Running the CLI
```bash
# List Physical Drives
# Linux (as root):
./Ventoy2DiskDotNet --list
# Windows (as Administrator):
Ventoy2DiskDotNet.exe --list

# Clean Install Ventoy (Wipes Disk)
# GPT Style, exFAT Filesystem, Secure Boot Disabled
# Linux (as root):
./Ventoy2DiskDotNet --install --device /dev/sdb --style GPT --secureboot no --filesystem exfat
# Windows (as Administrator):
Ventoy2DiskDotNet.exe --install --device 1 --style GPT --secureboot no --filesystem exfat

# Update Ventoy (Preserves Partition 1 data files)
# Linux (as root):
./Ventoy2DiskDotNet --update --device /dev/sdb --secureboot yes
# Windows (as Administrator):
Ventoy2DiskDotNet.exe --update --device 1 --secureboot yes
```

### Running the GUI
```bash
# Linux (as root):
./Ventoy2DiskAvalonia

# Windows (as Administrator):
Ventoy2DiskAvalonia.exe
```

### Running via dotnet run (Development)
You can also run directly from source using the .NET CLI:
```bash
# List physical drives
dotnet run --project src/Ventoy2DiskDotNet/ -- --list

# Launch Avalonia GUI
dotnet run --project src/Ventoy2DiskAvalonia/
```


## Structure

- [Program.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/Program.cs): Entry point parsing arguments, presenting warnings, and coordinating installation/updates.
- [Structures.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/Structures.cs): Binary layouts and serialization models for Partition Table Entries, MBR boot code, and GPT Headers.
- [PartitionService.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/PartitionService.cs): Fills in partition tables, handles GPT CRC checksums, and manages LBA offsets.
- [DiskService.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/DiskService.cs): Manages physical disk queries and acquires exclusive read/write disk streams.
- [Decompressor.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/Decompressor.cs): XZ decompression logic using `SharpCompress`.
