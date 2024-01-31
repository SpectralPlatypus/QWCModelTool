using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace QWCModelTool
{
    /// <summary>
    /// Top level header for a FSH file
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct FshHeader
    {
        public const int Size = 16;

        public uint Magic { get; }
        public int FileSize { get; }
        public int NumEntries { get; }
        public uint DirId { get; }

        public FshHeader(uint magic, int fileSize, int numEntries, uint dirId)
        {
            this.Magic = magic;
            this.FileSize = fileSize;
            this.NumEntries = numEntries;
            this.DirId = dirId;
        }

        public static FshHeader Parse(Span<byte> data) => MemoryMarshal.Cast<byte, FshHeader>(data)[0];
    }

    internal struct FshDirEntry
    {
        public string Name { get; set; }
        public int EntryOffset { get; set; }

        public FshDirEntry(string Name, int EntryOffset)
        {
            this.Name = Name;
            this.EntryOffset = EntryOffset;
        }

        public static FshDirEntry Parse(Span<byte> data) => new FshDirEntry(
            Name: Encoding.ASCII.GetString(data[..4]),
            EntryOffset: BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4))
            );
    }

    internal readonly struct FshChunk
    {
        public FshChunk(FshChunkType recordId, int nextBlockOffset, IMemoryOwner<byte> data = null)
        {
            RecordId = recordId;
            NextBlockOffset = nextBlockOffset;
            Data = data;
        }

        public bool HasNextBlock() => NextBlockOffset > 0;

        public FshChunkType RecordId { get; }
        public int NextBlockOffset { get; } = 0;
        public IMemoryOwner<byte> Data { get; } = null;
    }

    enum FshChunkType
    {
        // Palette Code
        LUT_RGB_24 = 0x24,      // RGB 888
        LUT_ARGB_32 = 0x2A,     // ARGB 8888
        // Bitmap Codes
        DXT1 = 0x60,            // DXT1 4x4, 1 bit alpha
        DXT3 = 0x61,            // DXT3 4x4, 4 bit alpha
        ARGB_16 = 0x6D,         // ARGB 4444
        RGB_31 = 0x78,          // RGB 565
        P_4 = 0x79,             // 4bpp palletized
        P_8 = 0x7B,             // 8bpp palletized
        ARGB_32 = 0x7D,         // ARGB 8888
        ARGB_1_15 = 0x7E,       // ARGB 1555
        // Non-image related entries
        NAME = 0x70,      // Entry name
    }
}
