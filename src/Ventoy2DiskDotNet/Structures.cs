using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ventoy2DiskDotNet
{
    public struct PartitionEntry
    {
        public byte Active;
        public byte StartHead;
        public ushort StartSectorAndCyl; // Low 6 bits = Sector, High 10 bits = Cylinder
        public byte FsFlag;
        public byte EndHead;
        public ushort EndSectorAndCyl; // Low 6 bits = Sector, High 10 bits = Cylinder
        public uint StartSectorId;
        public uint SectorCount;

        public void SetStart(byte sector, ushort cylinder)
        {
            StartSectorAndCyl = (ushort)((sector & 0x3F) | ((cylinder & 0x3FF) << 6));
        }

        public void SetEnd(byte sector, ushort cylinder)
        {
            EndSectorAndCyl = (ushort)((sector & 0x3F) | ((cylinder & 0x3FF) << 6));
        }

        public byte[] Serialize()
        {
            byte[] bytes = new byte[16];
            bytes[0] = Active;
            bytes[1] = StartHead;
            bytes[2] = (byte)(StartSectorAndCyl & 0xFF);
            bytes[3] = (byte)((StartSectorAndCyl >> 8) & 0xFF);
            bytes[4] = FsFlag;
            bytes[5] = EndHead;
            bytes[6] = (byte)(EndSectorAndCyl & 0xFF);
            bytes[7] = (byte)((EndSectorAndCyl >> 8) & 0xFF);
            
            // Write StartSectorId and SectorCount as little-endian
            var startBytes = BitConverter.GetBytes(StartSectorId);
            var countBytes = BitConverter.GetBytes(SectorCount);
            Array.Copy(startBytes, 0, bytes, 8, 4);
            Array.Copy(countBytes, 0, bytes, 12, 4);
            return bytes;
        }

        public static PartitionEntry Deserialize(byte[] bytes, int offset)
        {
            PartitionEntry entry = new PartitionEntry();
            entry.Active = bytes[offset];
            entry.StartHead = bytes[offset + 1];
            entry.StartSectorAndCyl = (ushort)(bytes[offset + 2] | (bytes[offset + 3] << 8));
            entry.FsFlag = bytes[offset + 4];
            entry.EndHead = bytes[offset + 5];
            entry.EndSectorAndCyl = (ushort)(bytes[offset + 6] | (bytes[offset + 7] << 8));
            entry.StartSectorId = BitConverter.ToUInt32(bytes, offset + 8);
            entry.SectorCount = BitConverter.ToUInt32(bytes, offset + 12);
            return entry;
        }
    }

    public class MbrHead
    {
        public byte[] BootCode = new byte[446];
        public PartitionEntry[] PartTbl = new PartitionEntry[4];
        public byte Byte55 = 0x55;
        public byte ByteAA = 0xAA;

        public MbrHead()
        {
            for (int i = 0; i < 4; i++)
            {
                PartTbl[i] = new PartitionEntry();
            }
        }

        public byte[] Serialize()
        {
            byte[] bytes = new byte[512];
            Array.Copy(BootCode, 0, bytes, 0, 446);
            for (int i = 0; i < 4; i++)
            {
                byte[] partBytes = PartTbl[i].Serialize();
                Array.Copy(partBytes, 0, bytes, 446 + (i * 16), 16);
            }
            bytes[510] = Byte55;
            bytes[511] = ByteAA;
            return bytes;
        }

        public static MbrHead Deserialize(byte[] bytes)
        {
            MbrHead mbr = new MbrHead();
            Array.Copy(bytes, 0, mbr.BootCode, 0, 446);
            for (int i = 0; i < 4; i++)
            {
                mbr.PartTbl[i] = PartitionEntry.Deserialize(bytes, 446 + (i * 16));
            }
            mbr.Byte55 = bytes[510];
            mbr.ByteAA = bytes[511];
            return mbr;
        }
    }

    public class GptHeader
    {
        public byte[] Signature = Encoding.ASCII.GetBytes("EFI PART"); // 8 bytes
        public byte[] Version = new byte[] { 0x00, 0x00, 0x01, 0x00 }; // 4 bytes
        public uint Length = 92;
        public uint Crc;
        public uint Reserved1;
        public ulong EfiStartLBA = 1;
        public ulong EfiBackupLBA;
        public ulong PartAreaStartLBA = 34;
        public ulong PartAreaEndLBA;
        public Guid DiskGuid;
        public ulong PartTblStartLBA = 2;
        public uint PartTblTotNum = 128;
        public uint PartTblEntryLen = 128;
        public uint PartTblCrc;
        public byte[] Reserved2 = new byte[420];

        public byte[] Serialize()
        {
            byte[] bytes = new byte[512];
            Array.Copy(Signature, 0, bytes, 0, 8);
            Array.Copy(Version, 0, bytes, 8, 4);
            
            Array.Copy(BitConverter.GetBytes(Length), 0, bytes, 12, 4);
            Array.Copy(BitConverter.GetBytes(Crc), 0, bytes, 16, 4);
            Array.Copy(BitConverter.GetBytes(Reserved1), 0, bytes, 20, 4);
            
            Array.Copy(BitConverter.GetBytes(EfiStartLBA), 0, bytes, 24, 8);
            Array.Copy(BitConverter.GetBytes(EfiBackupLBA), 0, bytes, 32, 8);
            Array.Copy(BitConverter.GetBytes(PartAreaStartLBA), 0, bytes, 40, 8);
            Array.Copy(BitConverter.GetBytes(PartAreaEndLBA), 0, bytes, 48, 8);
            
            Array.Copy(DiskGuid.ToByteArray(), 0, bytes, 56, 16);
            
            Array.Copy(BitConverter.GetBytes(PartTblStartLBA), 0, bytes, 72, 8);
            Array.Copy(BitConverter.GetBytes(PartTblTotNum), 0, bytes, 80, 4);
            Array.Copy(BitConverter.GetBytes(PartTblEntryLen), 0, bytes, 84, 4);
            Array.Copy(BitConverter.GetBytes(PartTblCrc), 0, bytes, 88, 4);
            
            Array.Copy(Reserved2, 0, bytes, 92, 420);
            return bytes;
        }

        public static GptHeader Deserialize(byte[] bytes)
        {
            GptHeader hdr = new GptHeader();
            Array.Copy(bytes, 0, hdr.Signature, 0, 8);
            Array.Copy(bytes, 8, hdr.Version, 0, 4);
            hdr.Length = BitConverter.ToUInt32(bytes, 12);
            hdr.Crc = BitConverter.ToUInt32(bytes, 16);
            hdr.Reserved1 = BitConverter.ToUInt32(bytes, 20);
            hdr.EfiStartLBA = BitConverter.ToUInt64(bytes, 24);
            hdr.EfiBackupLBA = BitConverter.ToUInt64(bytes, 32);
            hdr.PartAreaStartLBA = BitConverter.ToUInt64(bytes, 40);
            hdr.PartAreaEndLBA = BitConverter.ToUInt64(bytes, 48);
            
            byte[] guidBytes = new byte[16];
            Array.Copy(bytes, 56, guidBytes, 0, 16);
            hdr.DiskGuid = new Guid(guidBytes);
            
            hdr.PartTblStartLBA = BitConverter.ToUInt64(bytes, 72);
            hdr.PartTblTotNum = BitConverter.ToUInt32(bytes, 80);
            hdr.PartTblEntryLen = BitConverter.ToUInt32(bytes, 84);
            hdr.PartTblCrc = BitConverter.ToUInt32(bytes, 88);
            
            Array.Copy(bytes, 92, hdr.Reserved2, 0, 420);
            return hdr;
        }
    }

    public class GptPartEntry
    {
        public Guid PartType;
        public Guid PartGuid;
        public ulong StartLBA;
        public ulong LastLBA;
        public ulong Attr;
        public byte[] Name = new byte[72]; // UTF-16LE, 36 characters

        public void SetName(string name)
        {
            Array.Clear(Name, 0, Name.Length);
            byte[] nameBytes = Encoding.Unicode.GetBytes(name);
            int copyLen = Math.Min(nameBytes.Length, Name.Length);
            Array.Copy(nameBytes, Name, copyLen);
        }

        public byte[] Serialize()
        {
            byte[] bytes = new byte[128];
            Array.Copy(PartType.ToByteArray(), 0, bytes, 0, 16);
            Array.Copy(PartGuid.ToByteArray(), 0, bytes, 16, 16);
            Array.Copy(BitConverter.GetBytes(StartLBA), 0, bytes, 32, 8);
            Array.Copy(BitConverter.GetBytes(LastLBA), 0, bytes, 40, 8);
            Array.Copy(BitConverter.GetBytes(Attr), 0, bytes, 48, 8);
            Array.Copy(Name, 0, bytes, 56, 72);
            return bytes;
        }

        public static GptPartEntry Deserialize(byte[] bytes, int offset)
        {
            GptPartEntry entry = new GptPartEntry();
            byte[] typeBytes = new byte[16];
            byte[] guidBytes = new byte[16];
            Array.Copy(bytes, offset, typeBytes, 0, 16);
            Array.Copy(bytes, offset + 16, guidBytes, 0, 16);
            entry.PartType = new Guid(typeBytes);
            entry.PartGuid = new Guid(guidBytes);
            entry.StartLBA = BitConverter.ToUInt64(bytes, offset + 32);
            entry.LastLBA = BitConverter.ToUInt64(bytes, offset + 40);
            entry.Attr = BitConverter.ToUInt64(bytes, offset + 48);
            Array.Copy(bytes, offset + 56, entry.Name, 0, 72);
            return entry;
        }
    }

    public class GptInfo
    {
        public MbrHead Mbr = new MbrHead();
        public GptHeader Head = new GptHeader();
        public GptPartEntry[] PartTbl = new GptPartEntry[128];

        public GptInfo()
        {
            for (int i = 0; i < 128; i++)
            {
                PartTbl[i] = new GptPartEntry();
            }
        }

        public byte[] Serialize()
        {
            byte[] bytes = new byte[512 + 512 + (128 * 128)]; // 512 + 512 + 16384 = 17408 bytes
            byte[] mbrBytes = Mbr.Serialize();
            byte[] headBytes = Head.Serialize();
            Array.Copy(mbrBytes, 0, bytes, 0, 512);
            Array.Copy(headBytes, 0, bytes, 512, 512);
            for (int i = 0; i < 128; i++)
            {
                byte[] entryBytes = PartTbl[i].Serialize();
                Array.Copy(entryBytes, 0, bytes, 1024 + (i * 128), 128);
            }
            return bytes;
        }
    }
}
