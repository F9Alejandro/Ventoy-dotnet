using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ventoy2Disk.NET
{
    public static class Structures
    {
        public const int SectorSize = 512;
        public const ulong VentoyEfiPartAttr = 0x8000000000000000UL;
        public const uint VentoyEfiPartSize = 32 * 1024 * 1024; // 32MB

        public struct PartTableEntry
        {
            public byte Active;
            public byte StartHead;
            public byte StartSector;
            public ushort StartCylinder;
            public byte FsFlag;
            public byte EndHead;
            public byte EndSector;
            public ushort EndCylinder;
            public uint StartSectorId;
            public uint SectorCount;

            public byte[] Serialize()
            {
                byte[] data = new byte[16];
                data[0] = Active;
                data[1] = StartHead;
                
                // Pack sector (6 bits) and cylinder (10 bits)
                ushort startChs = (ushort)((StartSector & 0x3F) | ((StartCylinder & 0x3FF) << 6));
                data[2] = (byte)(startChs & 0xFF);
                data[3] = (byte)((startChs >> 8) & 0xFF);

                data[4] = FsFlag;
                data[5] = EndHead;

                ushort endChs = (ushort)((EndSector & 0x3F) | ((EndCylinder & 0x3FF) << 6));
                data[6] = (byte)(endChs & 0xFF);
                data[7] = (byte)((endChs >> 8) & 0xFF);

                Array.Copy(BitConverter.GetBytes(StartSectorId), 0, data, 8, 4);
                Array.Copy(BitConverter.GetBytes(SectorCount), 0, data, 12, 4);

                return data;
            }

            public static PartTableEntry Deserialize(byte[] data, int offset)
            {
                PartTableEntry entry = new PartTableEntry();
                entry.Active = data[offset];
                entry.StartHead = data[offset + 1];

                ushort startChs = BitConverter.ToUInt16(data, offset + 2);
                entry.StartSector = (byte)(startChs & 0x3F);
                entry.StartCylinder = (ushort)((startChs >> 6) & 0x3FF);

                entry.FsFlag = data[offset + 4];
                entry.EndHead = data[offset + 5];

                ushort endChs = BitConverter.ToUInt16(data, offset + 6);
                entry.EndSector = (byte)(endChs & 0x3F);
                entry.EndCylinder = (ushort)((endChs >> 6) & 0x3FF);

                entry.StartSectorId = BitConverter.ToUInt32(data, offset + 8);
                entry.SectorCount = BitConverter.ToUInt32(data, offset + 12);

                return entry;
            }
        }

        public class MbrHead
        {
            public byte[] BootCode = new byte[446];
            public PartTableEntry[] PartTbl = new PartTableEntry[4];
            public byte Byte55 = 0x55;
            public byte ByteAA = 0xAA;

            public byte[] Serialize()
            {
                byte[] data = new byte[512];
                Array.Copy(BootCode, 0, data, 0, 446);
                for (int i = 0; i < 4; i++)
                {
                    byte[] partData = PartTbl[i].Serialize();
                    Array.Copy(partData, 0, data, 446 + i * 16, 16);
                }
                data[510] = Byte55;
                data[511] = ByteAA;
                return data;
            }

            public static MbrHead Deserialize(byte[] data)
            {
                MbrHead mbr = new MbrHead();
                Array.Copy(data, 0, mbr.BootCode, 0, 446);
                for (int i = 0; i < 4; i++)
                {
                    mbr.PartTbl[i] = PartTableEntry.Deserialize(data, 446 + i * 16);
                }
                mbr.Byte55 = data[510];
                mbr.ByteAA = data[511];
                return mbr;
            }
        }

        public class GptHeader
        {
            public byte[] Signature = Encoding.ASCII.GetBytes("EFI PART"); // 8 bytes
            public uint Version = 0x00010000;
            public uint Length = 92;
            public uint Crc;
            public uint Reserved = 0;
            public ulong EfiStartLBA;
            public ulong EfiBackupLBA;
            public ulong PartAreaStartLBA = 34;
            public ulong PartAreaEndLBA;
            public Guid DiskGuid;
            public ulong PartTblStartLBA;
            public uint PartTblTotNum = 128;
            public uint PartTblEntryLen = 128;
            public uint PartTblCrc;

            public byte[] Serialize()
            {
                byte[] data = new byte[92];
                Array.Copy(Signature, 0, data, 0, 8);
                Array.Copy(BitConverter.GetBytes(Version), 0, data, 8, 4);
                Array.Copy(BitConverter.GetBytes(Length), 0, data, 12, 4);
                Array.Copy(BitConverter.GetBytes(Crc), 0, data, 16, 4);
                Array.Copy(BitConverter.GetBytes(Reserved), 0, data, 20, 4);
                Array.Copy(BitConverter.GetBytes(EfiStartLBA), 0, data, 24, 8);
                Array.Copy(BitConverter.GetBytes(EfiBackupLBA), 0, data, 32, 8);
                Array.Copy(BitConverter.GetBytes(PartAreaStartLBA), 0, data, 40, 8);
                Array.Copy(BitConverter.GetBytes(PartAreaEndLBA), 0, data, 48, 8);
                Array.Copy(DiskGuid.ToByteArray(), 0, data, 56, 16);
                Array.Copy(BitConverter.GetBytes(PartTblStartLBA), 0, data, 72, 8);
                Array.Copy(BitConverter.GetBytes(PartTblTotNum), 0, data, 80, 4);
                Array.Copy(BitConverter.GetBytes(PartTblEntryLen), 0, data, 84, 4);
                Array.Copy(BitConverter.GetBytes(PartTblCrc), 0, data, 88, 4);
                return data;
            }
        }

        public class GptPartEntry
        {
            public Guid PartType;
            public Guid PartGuid;
            public ulong StartLBA;
            public ulong LastLBA;
            public ulong Attr;
            public string Name = ""; // Unicode 36 chars

            public byte[] Serialize()
            {
                byte[] data = new byte[128];
                Array.Copy(PartType.ToByteArray(), 0, data, 0, 16);
                Array.Copy(PartGuid.ToByteArray(), 0, data, 16, 16);
                Array.Copy(BitConverter.GetBytes(StartLBA), 0, data, 32, 8);
                Array.Copy(BitConverter.GetBytes(LastLBA), 0, data, 40, 8);
                Array.Copy(BitConverter.GetBytes(Attr), 0, data, 48, 8);

                byte[] nameBytes = Encoding.Unicode.GetBytes(Name);
                int len = Math.Min(nameBytes.Length, 72);
                Array.Copy(nameBytes, 0, data, 56, len);

                return data;
            }
        }
    }
}
