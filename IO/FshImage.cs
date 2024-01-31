using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QWCModelTool
{
    // TODO: Mipmaps
    // TODO: Global palette extraction
    internal static class FshImage
    {
        // bool hasGlobalPalette = false;

        public static Image LoadImage(Stream stream) => FromStreamInternal(stream).FirstOrDefault();
        public static List<Image> LoadAllImages(Stream stream) => FromStreamInternal(stream);

        private static List<Image> FromStreamInternal(Stream stream)
        {
            List<FshDirEntry> dirList = new List<FshDirEntry>();
            List<Image> images = new List<Image>();
            using BinaryReader reader = new BinaryReader(stream);
            // Parse header
            FshHeader header = ReadFileHeader(stream);

            if (header.Magic != 0x49504853) //"SHPI"
            {
                throw new InvalidDataException("Failed to match magic string");
            }
            if (header.NumEntries < 0)
            {
                throw new InvalidDataException(nameof(header.NumEntries));
            }

            dirList = new List<FshDirEntry>(header.NumEntries);
            for (int i = 0; i < header.NumEntries; ++i)
            {
                dirList.Add(ReadDirEntry(stream));
            }

            foreach (FshDirEntry entry in dirList)
            {
                stream.Position = entry.EntryOffset;
                FshDecoder decoder = new();
                var img = decoder.Decode<Rgba32>(stream);
                images.Add(img);
            }

            return images;
        }

        private static FshHeader ReadFileHeader(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[FshHeader.Size];
            stream.Read(buffer);
            return FshHeader.Parse(buffer);
        }

        private static FshDirEntry ReadDirEntry(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[0x8];
            stream.Read(buffer);
            return FshDirEntry.Parse(buffer);
        }
    }
}
