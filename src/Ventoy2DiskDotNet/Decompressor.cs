using System.IO;
using SharpCompress.Compressors.Xz;

namespace Ventoy2DiskDotNet
{
    public static class Decompressor
    {
        public static byte[] DecompressXz(string filePath)
        {
            using (var fileStream = File.OpenRead(filePath))
            using (var xzStream = new XZStream(fileStream))
            using (var memoryStream = new MemoryStream())
            {
                xzStream.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }

        public static byte[] DecompressXz(byte[] compressedData)
        {
            using (var compressedStream = new MemoryStream(compressedData))
            using (var xzStream = new XZStream(compressedStream))
            using (var memoryStream = new MemoryStream())
            {
                xzStream.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }
    }
}
