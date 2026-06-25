#!/bin/bash
# Rebuild all Windows targets for .NET projects (win-x64)

set -e

echo "=== Rebuilding Windows Target: Ventoy2Disk.NET ==="
/root/.dotnet/dotnet publish src/Ventoy2Disk.NET/Ventoy2Disk.NET.csproj -c Release -r win-x64 --self-contained true

echo "=== Rebuilding Windows Target: VentoyVlnk ==="
/root/.dotnet/dotnet publish src/VentoyVlnk/VentoyVlnk.csproj -c Release -r win-x64 --self-contained true

echo "=== Rebuilding Windows Target: Ventoy2DiskDotNet ==="
/root/.dotnet/dotnet publish src/Ventoy2DiskDotNet/Ventoy2DiskDotNet.csproj -c Release -r win-x64 --self-contained true

echo "=== Rebuilding Windows Target: Ventoy2DiskAvalonia (Desktop GUI) ==="
/root/.dotnet/dotnet publish src/Ventoy2DiskAvalonia/Ventoy2DiskAvalonia.csproj -c Release -r win-x64 --self-contained true

echo "=== All .NET Windows Targets compiled successfully! ==="
echo "Published files are available in the respective bin/Release/net8.0/win-x64/publish/ directories."
