using System;
using System.Text;

namespace Ventoy2DiskDotNet
{
    public static class PartitionService
    {
        private static readonly uint[] CrcTable = new uint[256];

        static PartitionService()
        {
            uint poly = 0xEDB88320;
            for (uint i = 0; i < 256; i++)
            {
                uint temp = i;
                for (int j = 8; j > 0; j--)
                {
                    if ((temp & 1) == 1)
                    {
                        temp = (temp >> 1) ^ poly;
                    }
                    else
                    {
                        temp >>= 1;
                    }
                }
                CrcTable[i] = temp;
            }
        }

        public static uint CalculateCrc32(byte[] buffer, int offset, int length)
        {
            uint crc = 0xffffffff;
            for (int i = 0; i < length; i++)
            {
                byte b = buffer[offset + i];
                crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xff];
            }
            return crc ^ 0xffffffff;
        }

        public static int FillMbrLocation(ulong diskSizeInBytes, uint startSectorId, uint sectorCount, ref PartitionEntry table)
        {
            byte nSector = 63;
            byte nHead = 8;
            uint cylinder;
            uint endSectorId;

            while (nHead != 0 && (diskSizeInBytes / 512 / nSector / nHead) > 1024)
            {
                nHead = (byte)(nHead * 2);
            }

            if (nHead == 0)
            {
                nHead = 255;
            }

            cylinder = startSectorId / nSector / nHead;
            byte startHead = (byte)((startSectorId / nSector) % nHead);
            byte startSector = (byte)((startSectorId % nSector) + 1);

            table.StartHead = startHead;
            table.SetStart(startSector, (ushort)cylinder);

            endSectorId = startSectorId + sectorCount - 1;
            cylinder = endSectorId / nSector / nHead;
            byte endHead = (byte)((endSectorId / nSector) % nHead);
            byte endSector = (byte)((endSectorId % nSector) + 1);

            table.EndHead = endHead;
            table.SetEnd(endSector, (ushort)cylinder);

            table.StartSectorId = startSectorId;
            table.SectorCount = sectorCount;

            return 0;
        }

        public static MbrHead FillMbr(ulong diskSizeInBytes, byte[] bootImg, byte fsFlag)
        {
            MbrHead mbr = new MbrHead();
            Array.Copy(bootImg, 0, mbr.BootCode, 0, Math.Min(bootImg.Length, 446));

            Guid guid = Guid.NewGuid();
            byte[] guidBytes = guid.ToByteArray();
            uint diskSignature = BitConverter.ToUInt32(guidBytes, 0);

            // Write disk signature at offset 0x1B8 (4 bytes)
            Array.Copy(BitConverter.GetBytes(diskSignature), 0, mbr.BootCode, 0x1B8, 4);

            // Write guid at offset 0x180 (16 bytes)
            Array.Copy(guidBytes, 0, mbr.BootCode, 0x180, 16);

            ulong diskSectorCount = diskSizeInBytes / 512;
            uint diskSectorCount32 = diskSectorCount > uint.MaxValue ? uint.MaxValue : (uint)diskSectorCount;

            uint reservedSector = 0;
            // Alignment checks (simplified standard)
            // Sector 2048 is the standard starting sector for partition 1 in Ventoy
            uint part1StartSector = 2048;
            uint efiPartSectors = 65536; // 32MB / 512 = 65536 sectors

            uint part1SectorCount = diskSectorCount32 - reservedSector - efiPartSectors - part1StartSector;

            // Fills Partition 1
            PartitionEntry part1 = new PartitionEntry();
            FillMbrLocation(diskSizeInBytes, part1StartSector, part1SectorCount, ref part1);
            part1.Active = 0x80; // bootable
            part1.FsFlag = fsFlag; // File system flag: 0x07 (exFAT/NTFS) or 0x0C (FAT32)
            mbr.PartTbl[0] = part1;

            // Fills Partition 2
            uint part2StartSector = part1StartSector + part1SectorCount;
            PartitionEntry part2 = new PartitionEntry();
            FillMbrLocation(diskSizeInBytes, part2StartSector, efiPartSectors, ref part2);
            part2.Active = 0x00;
            part2.FsFlag = 0xEF; // EFI partition type
            mbr.PartTbl[1] = part2;

            mbr.Byte55 = 0x55;
            mbr.ByteAA = 0xAA;

            return mbr;
        }

        public static MbrHead FillProtectMbr(ulong diskSizeInBytes, byte[] bootImg)
        {
            MbrHead mbr = new MbrHead();
            Array.Copy(bootImg, 0, mbr.BootCode, 0, Math.Min(bootImg.Length, 446));

            Guid guid = Guid.NewGuid();
            byte[] guidBytes = guid.ToByteArray();
            uint diskSignature = BitConverter.ToUInt32(guidBytes, 0);

            // Write disk signature at offset 0x1B8
            Array.Copy(BitConverter.GetBytes(diskSignature), 0, mbr.BootCode, 0x1B8, 4);
            // Write GUID at offset 0x180
            Array.Copy(guidBytes, 0, mbr.BootCode, 0x180, 16);

            ulong diskSectorCount = (diskSizeInBytes / 512) - 1;
            uint diskSectorCount32 = diskSectorCount > uint.MaxValue ? uint.MaxValue : (uint)diskSectorCount;

            // Empty partition table entries first
            for (int i = 0; i < 4; i++)
            {
                mbr.PartTbl[i] = new PartitionEntry();
            }

            // Fill protective entry at 0
            PartitionEntry part0 = new PartitionEntry
            {
                Active = 0x00,
                FsFlag = 0xEE, // Protective GPT entry type
                StartHead = 0,
                StartSectorAndCyl = 0x0001, // Sector 1, Cylinder 0
                EndHead = 254,
                EndSectorAndCyl = 0xFFFF, // Max sector & cylinder
                StartSectorId = 1,
                SectorCount = diskSectorCount32
            };
            mbr.PartTbl[0] = part0;

            mbr.Byte55 = 0x55;
            mbr.ByteAA = 0xAA;

            // In GPT style, boot code byte 92 must be 34 (0x22)
            mbr.BootCode[92] = 0x22;

            return mbr;
        }

        public static GptInfo FillGpt(ulong diskSizeInBytes, byte[] bootImg)
        {
            GptInfo gpt = new GptInfo();
            gpt.Mbr = FillProtectMbr(diskSizeInBytes, bootImg);

            ulong diskSectorCount = diskSizeInBytes / 512;
            uint reservedSector = 33; // protective MBR + primary GPT header + 32 partition array sectors
            uint efiPartSectors = 65536; // 32MB / 512

            uint part1SectorCount = (uint)(diskSectorCount - reservedSector - efiPartSectors - 2048);

            // Align part 1 sector count to 8 sectors (4KB alignment)
            uint mod = part1SectorCount % 8;
            if (mod > 0)
            {
                part1SectorCount -= mod;
            }

            // Fill GPT Header
            GptHeader head = gpt.Head;
            head.Signature = Encoding.ASCII.GetBytes("EFI PART");
            head.Version = new byte[] { 0x00, 0x00, 0x01, 0x00 };
            head.Length = 92;
            head.Crc = 0;
            head.EfiStartLBA = 1;
            head.EfiBackupLBA = diskSectorCount - 1;
            head.PartAreaStartLBA = 34;
            head.PartAreaEndLBA = diskSectorCount - 34;
            head.DiskGuid = Guid.NewGuid();
            head.PartTblStartLBA = 2;
            head.PartTblTotNum = 128;
            head.PartTblEntryLen = 128;

            // Fill Partition Table Array
            // Partition 1 (Basic Data)
            Guid windowsDataGuid = new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
            gpt.PartTbl[0].PartType = windowsDataGuid;
            gpt.PartTbl[0].PartGuid = Guid.NewGuid();
            gpt.PartTbl[0].StartLBA = 2048;
            gpt.PartTbl[0].LastLBA = 2048 + part1SectorCount - 1;
            gpt.PartTbl[0].Attr = 0;
            gpt.PartTbl[0].SetName("Ventoy");

            // Partition 2 (VTOYEFI)
            gpt.PartTbl[1].PartType = windowsDataGuid; // Basic Data to fix Windows issues
            gpt.PartTbl[1].PartGuid = Guid.NewGuid();
            gpt.PartTbl[1].StartLBA = gpt.PartTbl[0].LastLBA + 1;
            gpt.PartTbl[1].LastLBA = gpt.PartTbl[1].StartLBA + efiPartSectors - 1;
            gpt.PartTbl[1].Attr = 0x8000000000000000UL; // No automount
            gpt.PartTbl[1].SetName("VTOYEFI");

            // Zero out remaining partition table entries
            for (int i = 2; i < 128; i++)
            {
                gpt.PartTbl[i] = new GptPartEntry();
            }

            // Calculate CRCs
            // 1. Partition Table Array CRC (128 entries * 128 bytes = 16384 bytes)
            byte[] partArrayBytes = new byte[128 * 128];
            for (int i = 0; i < 128; i++)
            {
                byte[] entryBytes = gpt.PartTbl[i].Serialize();
                Array.Copy(entryBytes, 0, partArrayBytes, i * 128, 128);
            }
            head.PartTblCrc = CalculateCrc32(partArrayBytes, 0, partArrayBytes.Length);

            // 2. Header CRC (must be calculated with CRC field set to 0)
            byte[] headBytes = head.Serialize();
            // Zero out CRC field in serialized bytes (offset 16, length 4)
            Array.Clear(headBytes, 16, 4);
            head.Crc = CalculateCrc32(headBytes, 0, 92); // GPT Header CRC is over the first 92 bytes

            return gpt;
        }

        public static GptHeader CreateBackupGptHeader(GptInfo primaryGpt)
        {
            GptHeader backup = new GptHeader();
            // Copy primary header
            byte[] primaryBytes = primaryGpt.Head.Serialize();
            backup = GptHeader.Deserialize(primaryBytes);

            ulong startLba = backup.EfiStartLBA;
            ulong backupLba = backup.EfiBackupLBA;

            backup.EfiStartLBA = backupLba;
            backup.EfiBackupLBA = startLba;
            backup.PartTblStartLBA = backupLba + 1 - 33; // backup table starts 33 sectors before backup header

            // Re-calculate CRC
            byte[] backupBytes = backup.Serialize();
            Array.Clear(backupBytes, 16, 4);
            backup.Crc = CalculateCrc32(backupBytes, 0, 92);

            return backup;
        }
    }
}
