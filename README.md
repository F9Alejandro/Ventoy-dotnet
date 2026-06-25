# Ventoy .NET Installer (Cross-Platform)

A cross-platform port of the Ventoy Windows installer (`Ventoy2Disk`) rebuilt from scratch in **.NET 8.0** to run natively on Windows and Linux.

## Features

1. **Cross-Platform Drive Scanning**: Lists physical disks on Windows (using Win32 API) and Linux (using `/sys/block` scanning) with sizes, models, and vendors.
2. **Direct Raw Disk Writing**: Writes MBR/GPT partition tables and Stage 2 Grub bootloader directly to raw streams of target devices.
3. **In-Memory Secure Boot Toggle**: Modifies partition 2 (`ventoy.disk.img.xz` FAT image) directly in-memory using `DiscUtils.Fat` before writing to the disk, dynamically removing shim/MokManager files when Secure Boot support is disabled.
4. **Native exFAT Formatting**: Automatically formats partition 1 with exFAT and labels it "Ventoy" using native OS capabilities (via `diskpart` script on Windows and bundled `mkexfatfs` on Linux).

## Prerequisites

- **.NET SDK 8.0**
- On **Linux**, administrative (`sudo`/`root`) privileges are required to write directly to block devices.
- On **Windows**, the application must be run as **Administrator** to acquire raw handles to physical disks.

## Usage

### List Physical Drives
To scan and list all available physical devices:
```bash
# On Linux (as root):
/root/.dotnet/dotnet run --project src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj -- --list

# On Windows (as Administrator):
dotnet run --project src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj -- --list
```

### Install Ventoy (Wipes Disk)
To perform a clean installation of Ventoy onto a target device (MBR style with Secure Boot support enabled):
```bash
/root/.dotnet/dotnet run --project src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj -- --install --device /dev/sdb --style MBR --secureboot yes
```

To perform a clean GPT style installation with Secure Boot support disabled:
```bash
/root/.dotnet/dotnet run --project src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj -- --install --device /dev/sdb --style GPT --secureboot no
```

### Update Ventoy (Preserves Partition 1 Data)
To update the Ventoy system files on a device without deleting your ISOs or personal data:
```bash
/root/.dotnet/dotnet run --project src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj -- --update --device /dev/sdb --secureboot yes
```

## Structure

- [Program.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/Program.cs): Entry point parsing arguments, presenting warnings, and coordinating installation/updates.
- [Structures.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/Structures.cs): Binary layouts and serialization models for Partition Table Entries, MBR boot code, and GPT Headers.
- [PartitionService.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/PartitionService.cs): Fills in partition tables, handles GPT CRC checksums, and manages LBA offsets.
- [DiskService.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/DiskService.cs): Manages physical disk queries and acquires exclusive read/write disk streams.
- [Decompressor.cs](file:///root/ventoy-dotnet/src/Ventoy2DiskDotNet/Decompressor.cs): XZ decompression logic using `SharpCompress`.
