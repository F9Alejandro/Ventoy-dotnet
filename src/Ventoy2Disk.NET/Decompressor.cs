using System;
using System.IO;
using SharpCompress.Compressors.Xz;

namespace Ventoy2Disk.NET
{
    public static class Decompressor
    {
        public static byte[] DecompressXz(byte[] compressedData)
        {
            using (var compressedStream = new MemoryStream(compressedData))
            using (var xzStream = new XZStream(compressedStream))
            using (var decompressedStream = new MemoryStream())
            {
                xzStream.CopyTo(decompressedStream);
                return decompressedStream.ToArray();
            }
        }

        public static byte[] DecompressXzFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Compressed file not found: {filePath}");
            }
            using (var fs = File.OpenRead(filePath))
            using (var xzStream = new XZStream(fs))
            using (var decompressedStream = new MemoryStream())
            {
                xzStream.CopyTo(decompressedStream);
                return decompressedStream.ToArray();
            }
        }
    }
}
