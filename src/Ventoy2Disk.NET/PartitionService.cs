using System;
using System.Text;

namespace Ventoy2Disk.NET
{
    public static class PartitionService
    {
        public static readonly Guid WindowsDataPartType = new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
        public static readonly Guid EspPartType = new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
        public static readonly Guid BiosGrubPartType = new Guid("21686148-6449-6e6f-744e-656564454649");

        private static readonly uint[] CrcTable = new uint[256];

        static PartitionService()
        {
            const uint polynomial = 0xEDB88320;
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 8; j > 0; j--)
                {
                    if ((crc & 1) == 1)
                    {
                        crc = (crc >> 1) ^ polynomial;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
                CrcTable[i] = crc;
            }
        }

        public static uint CalculateCrc32(byte[] buffer, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < length; i++)
            {
                byte b = buffer[offset + i];
                crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
            }
            return crc ^ 0xFFFFFFFF;
        }

        public static uint CalculateCrc32(byte[] buffer)
        {
            return CalculateCrc32(buffer, 0, buffer.Length);
        }

        public static void VentoyFillMBRLocation(ulong diskSizeBytes, uint startSectorId, uint sectorCount, ref Structures.PartTableEntry table)
        {
            byte nSector = 63;
            byte nHead = 8;
            while (nHead != 0 && (diskSizeBytes / 512 / nSector / nHead) > 1024)
            {
                nHead = (byte)(nHead * 2);
            }

            if (nHead == 0)
            {
                nHead = 255;
            }

            uint cylinder = startSectorId / nSector / nHead;
            byte head = (byte)((startSectorId / nSector) % nHead);
            byte sector = (byte)((startSectorId % nSector) + 1);

            table.StartHead = head;
            table.StartSector = sector;
            table.StartCylinder = (ushort)cylinder;

            uint endSectorId = startSectorId + sectorCount - 1;
            cylinder = endSectorId / nSector / nHead;
            head = (byte)((endSectorId / nSector) % nHead);
            sector = (byte)((endSectorId % nSector) + 1);

            table.EndHead = head;
            table.EndSector = sector;
            table.EndCylinder = (ushort)cylinder;

            table.StartSectorId = startSectorId;
            table.SectorCount = sectorCount;
        }

        public static Structures.MbrHead CreateMbr(ulong diskSizeBytes, byte[] bootImgContent, int partStyle, byte fsFlag, uint reservedSpaceMB)
        {
            var mbr = new Structures.MbrHead();
            Array.Copy(bootImgContent, 0, mbr.BootCode, 0, 446);

            Guid guid = Guid.NewGuid();
            byte[] guidBytes = guid.ToByteArray();
            Array.Copy(guidBytes, 0, mbr.BootCode, 0x180, 16);

            uint diskSignature = BitConverter.ToUInt32(guidBytes, 0);
            Array.Copy(BitConverter.GetBytes(diskSignature), 0, mbr.BootCode, 0x1B8, 4);

            ulong diskSectorCount = diskSizeBytes / 512;
            if (diskSectorCount > 0xFFFFFFFF)
            {
                diskSectorCount = 0xFFFFFFFF;
            }

            uint reservedSectors = reservedSpaceMB * 2048;
            if (partStyle != 0)
            {
                reservedSectors += 33;
            }

            uint part1StartSector = 2048;
            uint part1SectorCount = (uint)(diskSectorCount - reservedSectors - (Structures.VentoyEfiPartSize / 512) - part1StartSector);
            
            var part1 = new Structures.PartTableEntry();
            VentoyFillMBRLocation(diskSizeBytes, part1StartSector, part1SectorCount, ref part1);
            part1.Active = 0x80;
            part1.FsFlag = fsFlag;
            mbr.PartTbl[0] = part1;

            uint part2StartSector = part1StartSector + part1SectorCount;
            uint part2SectorCount = Structures.VentoyEfiPartSize / 512;

            var part2 = new Structures.PartTableEntry();
            VentoyFillMBRLocation(diskSizeBytes, part2StartSector, part2SectorCount, ref part2);
            part2.Active = 0x00;
            part2.FsFlag = 0xEF;
            mbr.PartTbl[1] = part2;

            mbr.Byte55 = 0x55;
            mbr.ByteAA = 0xAA;

            return mbr;
        }

        public static Structures.MbrHead CreateProtectiveMbr(ulong diskSizeBytes, byte[] bootImgContent)
        {
            var mbr = new Structures.MbrHead();
            Array.Copy(bootImgContent, 0, mbr.BootCode, 0, 446);

            Guid guid = Guid.NewGuid();
            byte[] guidBytes = guid.ToByteArray();
            Array.Copy(guidBytes, 0, mbr.BootCode, 0x180, 16);

            uint diskSignature = BitConverter.ToUInt32(guidBytes, 0);
            Array.Copy(BitConverter.GetBytes(diskSignature), 0, mbr.BootCode, 0x1B8, 4);

            ulong diskSectorCount = diskSizeBytes / 512 - 1;
            if (diskSectorCount > 0xFFFFFFFF)
            {
                diskSectorCount = 0xFFFFFFFF;
            }

            var part1 = new Structures.PartTableEntry();
            part1.Active = 0x00;
            part1.FsFlag = 0xEE;
            part1.StartHead = 0;
            part1.StartSector = 1;
            part1.StartCylinder = 0;
            part1.EndHead = 254;
            part1.EndSector = 254 & 0x3F;
            part1.EndCylinder = 1023 & 0x3FF;
            part1.StartSectorId = 1;
            part1.SectorCount = (uint)diskSectorCount;

            mbr.PartTbl[0] = part1;
            mbr.Byte55 = 0x55;
            mbr.ByteAA = 0xAA;

            return mbr;
        }

        public static (Structures.GptHeader Header, Structures.GptPartEntry[] Partitions) CreateGpt(ulong diskSizeBytes, uint reservedSpaceMB)
        {
            ulong diskSectorCount = diskSizeBytes / 512;
            uint reservedSectors = 33 + reservedSpaceMB * 2048;

            uint part1StartSector = 2048;
            uint part1SectorCount = (uint)(diskSectorCount - reservedSectors - (Structures.VentoyEfiPartSize / 512) - part1StartSector);

            var header = new Structures.GptHeader();
            header.EfiStartLBA = 1;
            header.EfiBackupLBA = diskSectorCount - 1;
            header.PartAreaStartLBA = 34;
            header.PartAreaEndLBA = diskSectorCount - 34;
            header.DiskGuid = Guid.NewGuid();
            header.PartTblStartLBA = 2;
            header.PartTblTotNum = 128;
            header.PartTblEntryLen = 128;

            var partitions = new Structures.GptPartEntry[128];
            for (int i = 0; i < 128; i++)
            {
                partitions[i] = new Structures.GptPartEntry();
            }

            partitions[0].PartType = WindowsDataPartType;
            partitions[0].PartGuid = Guid.NewGuid();
            partitions[0].StartLBA = part1StartSector;
            partitions[0].LastLBA = part1StartSector + part1SectorCount - 1;
            partitions[0].Attr = 0;
            partitions[0].Name = "Ventoy";

            partitions[1].PartType = WindowsDataPartType;
            partitions[1].PartGuid = Guid.NewGuid();
            partitions[1].StartLBA = partitions[0].LastLBA + 1;
            partitions[1].LastLBA = partitions[1].StartLBA + (Structures.VentoyEfiPartSize / 512) - 1;
            partitions[1].Attr = Structures.VentoyEfiPartAttr;
            partitions[1].Name = "VTOYEFI";

            byte[] partArrayBytes = new byte[128 * 128];
            for (int i = 0; i < 128; i++)
            {
                byte[] entryBytes = partitions[i].Serialize();
                Array.Copy(entryBytes, 0, partArrayBytes, i * 128, 128);
            }

            header.PartTblCrc = CalculateCrc32(partArrayBytes);

            header.Crc = 0;
            byte[] headerBytes = header.Serialize();
            header.Crc = CalculateCrc32(headerBytes);

            return (header, partitions);
        }

        public static Structures.GptHeader CreateBackupGptHeader(Structures.GptHeader primaryHeader, ulong diskSectorCount)
        {
            var backup = new Structures.GptHeader();
            backup.Signature = primaryHeader.Signature;
            backup.Version = primaryHeader.Version;
            backup.Length = primaryHeader.Length;
            backup.Reserved = primaryHeader.Reserved;
            
            backup.EfiStartLBA = primaryHeader.EfiBackupLBA;
            backup.EfiBackupLBA = primaryHeader.EfiStartLBA;
            backup.PartAreaStartLBA = primaryHeader.PartAreaStartLBA;
            backup.PartAreaEndLBA = primaryHeader.PartAreaEndLBA;
            backup.DiskGuid = primaryHeader.DiskGuid;
            backup.PartTblStartLBA = primaryHeader.EfiBackupLBA + 1 - 33;
            backup.PartTblTotNum = primaryHeader.PartTblTotNum;
            backup.PartTblEntryLen = primaryHeader.PartTblEntryLen;
            backup.PartTblCrc = primaryHeader.PartTblCrc;

            backup.Crc = 0;
            byte[] headerBytes = backup.Serialize();
            backup.Crc = CalculateCrc32(headerBytes);

            return backup;
        }
    }
}
