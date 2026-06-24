# Ventoy .NET (ventoy-dotnet)

A modern, cross-platform rebuild of the **Ventoy** installation and utility tools in **.NET 8.0**, replacing the legacy C-based Win32 UI, Linux GTK/Qt launchers, bash scripts, and Unix components with a single, portable C# codebase.

## Project Structure

This repository is organized into a clean .NET solution containing the following projects:

- **[Ventoy2DiskDotNet](src/Ventoy2DiskDotNet)**: The core Ventoy installation, update, and disk wiping engine.
  - Features a native C# implementation for Linux disk operations (wiping, partition table creation via `parted`, filesystem formatting, writing bootloaders/core images).
  - Integrates the **VentoyPlugson** web server configuration API endpoints, allowing users to configure plugins (`ventoy.json`) directly from their web browser.
- **[Ventoy2DiskAvalonia](src/Ventoy2DiskAvalonia)**: The unified, cross-platform desktop GUI built using **Avalonia UI**.
  - Replaces *both* the legacy Windows Win32 UI (`Ventoy2Disk.exe`) and the Linux GTK/Qt C launcher wrappers (`VentoyGUI`).
  - Includes a modern, responsive layout with two main views:
    1. **Ventoy2Disk Installer**: Choose target drives, select partition styles (MBR/GPT), toggle secure boot, and perform safe installs/updates.
    2. **Virtual Link (Vlnk) Creator**: Visually choose source image files, auto-resolve target partition offsets/disk signatures, and generate `.vlnk` files cross-platform.
- **[VentoyVlnk](src/VentoyVlnk)**: The cross-platform CLI utility to create virtual link files (`.vlnk`) for booting files from other partition/disk targets.

All dependencies, bootloaders, and static web pages are cleanly packaged inside `src/Ventoy2DiskDotNet/ventoy/` and `src/Ventoy2DiskDotNet/www/` and are copied to the build output directory automatically on compilation.

## Build Requirements

- **.NET 8.0 SDK** or higher.
- (Linux) Standard system utilities for raw disk partitioning and formatting: `parted`, `mkfs.vfat`, `udevadm`, `partprobe`.

## Building the Solution

To build all projects in the solution, run the following command from the root directory:

```bash
/root/.dotnet/dotnet build VentoyDotNet.sln
```

## Running the Desktop GUI (Avalonia)

### On Linux

Since raw disk access and filesystem formatting require superuser privileges, run the GUI application with `sudo` under your X11/Wayland desktop session:

```bash
sudo /root/.dotnet/dotnet run --project src/Ventoy2DiskAvalonia/Ventoy2DiskAvalonia.csproj
```

### On Windows

Open a terminal as Administrator and execute:

```cmd
dotnet run --project src\Ventoy2DiskAvalonia\Ventoy2DiskAvalonia.csproj
```

---

## Running the Web GUI (Plugson Web Mode)

### On Linux

```bash
sudo /root/.dotnet/dotnet run --project src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj
```

By default, the server starts on `http://127.0.0.1:24681`. Open your browser and navigate to this URL to install or update Ventoy.

### On Windows

Run the application as Administrator:

```cmd
dotnet run --project src\Ventoy2DiskDotNet\Ventoy2DiskDotNet.csproj
```

---

## Running the CLI Virtual Link Tool (VentoyVlnk)

To create a virtual link file via the command line:

```bash
/root/.dotnet/dotnet run --project src/VentoyVlnk/VentoyVlnk.csproj -- -c <filepath>
```

---

## Packaging and Standalone Deployment

You can publish standalone, self-contained packages for specific target platforms (meaning the user doesn't even need .NET installed to run them):

### Package for Linux (x64)

```bash
/root/.dotnet/dotnet publish src/Ventoy2DiskAvalonia/Ventoy2DiskAvalonia.csproj -c Release -r linux-x64 --self-contained true
```

The output standalone directory will be generated at `src/Ventoy2DiskAvalonia/bin/Release/net8.0/linux-x64/publish/`.

### Package for Windows (x64)

```bash
/root/.dotnet/dotnet publish src/Ventoy2DiskAvalonia/Ventoy2DiskAvalonia.csproj -c Release -r win-x64 --self-contained true
```

The output standalone directory will be generated at `src/Ventoy2DiskAvalonia/bin/Release/net8.0/win-x64/publish/`.
