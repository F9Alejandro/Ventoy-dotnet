# Ventoy .NET (ventoy-dotnet)

A modern, cross-platform rebuild of the **Ventoy** installation and utility tools in **.NET 8.0**, replacing the legacy C-based Win32 UI, Linux bash scripts, and Unix components with a single, portable C# codebase.

## Project Structure

This repository is organized into a clean .NET solution containing the following projects:

- **[Ventoy2DiskDotNet](src/Ventoy2DiskDotNet)**: The main Ventoy installation, update, and disk wiping utility. It provides a web-based GUI server similar to VentoyWeb/Plugson, allowing users to configure, install, and update Ventoy on their USB drives.
  - Features a native C# implementation for Linux disk operations (wiping, partition table creation via `parted`, filesystem formatting, writing bootloaders/core images).
  - Integrates Plugson configuration endpoints for editing `ventoy.json` settings directly from the web browser.
- **[VentoyVlnk](src/VentoyVlnk)**: The cross-platform utility to create virtual link files (`.vlnk`) for booting files from other partition/disk targets.

All dependencies, bootloaders, and static web pages are cleanly packaged inside `src/Ventoy2DiskDotNet/ventoy/` and `src/Ventoy2DiskDotNet/www/` and are copied to the build output directory automatically on compilation.

## Build Requirements

- **.NET 8.0 SDK** or higher.
- (Linux) Standard system utilities for raw disk partitioning and formatting: `parted`, `mkfs.vfat`, `udevadm`, `partprobe`.

## Building the Solution

To build both projects in the solution, run the following command from the root directory:

```bash
/root/.dotnet/dotnet build VentoyDotNet.sln
```

The compiled binaries will be output to:
- `src/Ventoy2DiskDotNet/bin/Debug/net8.0/` (or `Release`)
- `src/VentoyVlnk/bin/Debug/net8.0/` (or `Release`)

## Running Ventoy2DiskDotNet

### On Linux

Since raw disk access requires superuser privileges, run the application with `sudo`:

```bash
sudo /root/.dotnet/dotnet run --project src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj
```

By default, the server starts on `http://127.0.0.1:24681`. Open your browser and navigate to this URL to install or update Ventoy.

### On Windows

Run the application as Administrator:

```cmd
dotnet run --project src\Ventoy2DiskDotNet\Ventoy2DiskDotNet.csproj
```

## Running VentoyVlnk

To create a virtual link file, invoke the utility from the command line:

```bash
/root/.dotnet/dotnet run --project src/VentoyVlnk/VentoyVlnk.csproj -- <filepath>
```
