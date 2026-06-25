#!/bin/bash
# Rebuild native Ventoy tools for local architecture (x86_64)

set -e

echo "=== Rebuilding Native vtoycli ==="
cd Ventoy/vtoycli
gcc -no-pie -O2 -D_FILE_OFFSET_BITS=64 vtoycli.c vtoyfat.c vtoygpt.c crc32.c partresize.c -Ifat_io_lib/include fat_io_lib/lib/libfat_io_64.a -o vtoycli_64
strip --strip-all vtoycli_64
echo "vtoycli built successfully."

echo "=== Rebuilding Native vlnk ==="
cd ../Vlnk
gcc -no-pie -O2 -D_FILE_OFFSET_BITS=64 src/crc32.c src/main_linux.c src/vlnk.c -Isrc -o vlnk_64
strip --strip-all vlnk_64
echo "vlnk built successfully."

echo "=== Copying newly compiled binaries to src directories ==="
cd ../..
cp Ventoy/vtoycli/vtoycli_64 src/Ventoy2DiskDotNet/ventoy/tool/x86_64/vtoycli
cp Ventoy/Vlnk/vlnk_64 src/Ventoy2DiskDotNet/ventoy/tool/x86_64/vlnk

# Clean up temp files
rm -f Ventoy/vtoycli/vtoycli_64
rm -f Ventoy/Vlnk/vlnk_64

echo "=== All native utilities rebuilt and copied successfully! ==="
