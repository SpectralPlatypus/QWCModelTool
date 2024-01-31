using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using System;
using System.Buffers.Binary;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Data;
#nullable enable

namespace QWCModelTool
{
    class FshDecoder
    {
        Stream _stream = null!;
        // File info
        ushort _width;
        ushort _height;
        private ushort _xCenter;
        private ushort _yCenter;
        private ushort _xOffset;
        private ushort _yOffset;
        FshChunkType _record;

        // uint _bitDepth;
        Color[] colorTable = null!;
        IMemoryOwner<byte>? imageBlob = null;

        public FshDecoder()
        { }

        public Image<TPixel> Decode<TPixel>(Stream stream)
             where TPixel : unmanaged, IPixel<TPixel>
        {
            _stream = stream;
            FshChunk chunk = default;
            long startPos = _stream.Position;
            Image<TPixel>? image = null;

            // Start reading the first chunk and continue until the current chunk's next block offset is != 0
            do
            {
                startPos += chunk.NextBlockOffset;
                _stream.Position = startPos;
                if (!TryReadChunk(out chunk))
                {
                    break;
                }
                try
                {
                    switch (chunk.RecordId)
                    {
                        case FshChunkType.P_4:
                        case FshChunkType.P_8:
                            // Hold on to the raw image data (palette or pixels)
                            imageBlob = MemoryAllocator.Default.Allocate<byte>(chunk.Data.Memory.Length);
                            chunk.Data.Memory.CopyTo(imageBlob.Memory);
                            // The first 12-bytes are header info
                            ParseImageMetaData(imageBlob.Memory.Span[..12]);
                            _record = chunk.RecordId;
                            //imageBlo = startPos + 16;
                            break;
                        case FshChunkType.LUT_ARGB_32:
                            ReadPalette<Bgra32>(chunk.Data.Memory.Span);
                            break;
                        case FshChunkType.LUT_RGB_24:
                            ReadPalette<Rgb24>(chunk.Data.Memory.Span);
                            break;
                        case FshChunkType.ARGB_32:
                            // Image load
                            break;
                        case FshChunkType.NAME:
                            // Currently unused
                            ReadName(out string nameData);
                            break;
                        default:
                            throw new NotSupportedException("Unsupported chunk type:" + Enum.GetName(chunk.RecordId));
                    }
                }
                finally
                {
                    chunk.Data?.Dispose();
                }
            } while (chunk.HasNextBlock());

            // By this point, we should either have palette + palletized image or image itself
            if (image is null && imageBlob != null && colorTable.Length != 0)
            {
                if(!BuildImage(imageBlob.Memory.Span[12..], out image))
                {
                    image?.Dispose();
                    throw new InvalidDataException("Failed to build palletized image");
                }
            }

            imageBlob?.Dispose();
            if(image is null)
            {
                throw new InvalidDataException("Failed to build image");
            
            }

            return image;
        }

        private bool TryReadChunk(out FshChunk chunk)
        {
            if (_stream.Position >= _stream.Length - 1)
            {
                chunk = default;
                return false;
            }

            Span<byte> hdr = stackalloc byte[4];
            if (_stream.Read(hdr) != hdr.Length)
            {
                chunk = default;
                return false;
            }

            int packedHeader = BinaryPrimitives.ReadInt32BigEndian(hdr);
            FshChunkType record = (FshChunkType)((packedHeader >> 24) & 0x7F);
            int nbOffset = BinaryPrimitives.ReverseEndianness(packedHeader << 8);

            if (nbOffset != 0 && record != FshChunkType.NAME)
            {
                int length = nbOffset - hdr.Length;
                IMemoryOwner<byte> buffer = MemoryAllocator.Default.Allocate<byte>(length, AllocationOptions.Clean);
                _stream.Read(buffer.Memory.Span);
                chunk = new FshChunk(record, nbOffset, buffer);
            }
            else
            {
                chunk = new FshChunk(record, nbOffset);
            }

            return true;
        }

        /// <summary>
        /// Reads an image blob like ReadImageChunk, but populates the palette instead (always RGBA32)
        /// </summary>
        /// <typeparam name="TPixel"> Pixel type contained in the palette buffer</typeparam>
        /// <param name="data">Raw Palette Buffer</param>
        /// <exception cref="DataException">Thrown when palette header contains invalid data</exception>
        private void ReadPalette<TPixel>(ReadOnlySpan<byte> data)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            ushort width = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
            ushort height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));
            ushort xCenter = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));

            if (height != 1 || xCenter != width)
            {
                throw new DataException("Invalid palette chunk offsets");
            }

            var palette = data[12..].ToArray();

            this.colorTable = new Color[palette.Length / Unsafe.SizeOf<TPixel>()];
            ReadOnlySpan<TPixel> rgbTable = MemoryMarshal.Cast<byte, TPixel>(palette);
            for (int i = 0; i < colorTable.Length; i++)
            {
                Rgba32 colorEntry = Color.Black;
                rgbTable[i].ToRgba32(ref colorEntry);

                colorTable[i] = colorEntry;
            }
        }

        private bool BuildImage<TPixel>(ReadOnlySpan<byte> imageBlob, [NotNullWhen(true)] out Image<TPixel>? image)
           where TPixel : unmanaged, IPixel<TPixel>
        {
            // Skip the first 12-bytes as that's the meta data
            ReadOnlySpan<byte> data = imageBlob;
            image = new Image<TPixel>(_width, _height);
            Buffer2D<TPixel> pixels = image.Frames.RootFrame.PixelBuffer;
            IMemoryOwner<byte>? buffer = null;
            int bytesImg = (_height * _width) * GetBPP() / 8;

            bool result = false;
            try
            {
                ReadOnlySpan<byte> interpolatedScan = TryScaleUpTo8BitArray(data, bytesImg, GetBPP(), out buffer) ?
                    buffer.Memory.Span : data;
                ProcessPalette(interpolatedScan, pixels, 0, colorTable);
                result = true;
            }
            finally
            {
                buffer?.Dispose();
            }

            return result;
        }

        public void ProcessPalette<TPixel>(
        ReadOnlySpan<byte> rawSpan,
        Buffer2D<TPixel> pixelSpan,
        int pixelOffset,
        ReadOnlyMemory<Color> palette)
        where TPixel : unmanaged, IPixel<TPixel>
        {
            TPixel pixel = default;
            ref byte rawSpanRef = ref MemoryMarshal.GetReference(rawSpan);
            ref Color paletteBase = ref MemoryMarshal.GetReference(palette.Span);

            for (int y = 0; y < _height; ++y)
            {
                Span<TPixel> pixelRow = pixelSpan.DangerousGetRowSpan(y);
                int rowOffset = y * _width;
                for (int x = pixelOffset; x < _width; ++x)
                {
                    uint index = Unsafe.Add(ref rawSpanRef, x + rowOffset);
                    pixel.FromRgba32(Unsafe.Add(ref paletteBase, index));
                    pixelRow[x] = pixel;
                }
            }
        }

        private bool TryScaleUpTo8BitArray(ReadOnlySpan<byte> source, int bytesPerScanline, int bits, [NotNullWhen(true)] out IMemoryOwner<byte>? buffer)
        {
            if (bits >= 8)
            {
                buffer = null;
                return false;
            }

            buffer = MemoryAllocator.Default.Allocate<byte>(bytesPerScanline * 8 / bits, AllocationOptions.Clean);
            ref byte sourceRef = ref MemoryMarshal.GetReference(source);
            ref byte resultRef = ref MemoryMarshal.GetReference(buffer.Memory.Span);
            int mask = 0xFF >> (8 - bits);
            int resultOffset = 0;

            for (int i = 0; i < bytesPerScanline; i++)
            {
                byte b = Unsafe.Add(ref sourceRef, (uint)i);
                for (int shift = 0; shift < 8; shift += bits)
                {
                    int colorIndex = (b >> (shift)) & mask;
                    Unsafe.Add(ref resultRef, (uint)resultOffset) = (byte)colorIndex;
                    resultOffset++;
                }
            }

            return true;
        }

        private void ParseImageMetaData(ReadOnlySpan<byte> data)
        {
            _width = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
            _height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));

            _xCenter = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
            _yCenter = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
            _xOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
            _yOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10, 2));
        }

        private void ReadName(out string name)
        {
            // Read until we hit null char
            int val = _stream.ReadByte();
            byte[] buffer = new byte[0xFF];
            for (int i = 0; i < buffer.Length && (val > 0); ++i)
            {
                buffer[i] = (byte)val;
                val = _stream.ReadByte();
            }
            name = Encoding.ASCII.GetString(buffer);
            name = name.TrimEnd('\0');
        }

        private int GetBPP()
             => this._record
             switch
             {
                 FshChunkType.P_8 => 8,
                 FshChunkType.P_4 => 4,
                 FshChunkType.RGB_31 => 16,
                 FshChunkType.ARGB_16 => 16,
                 _ => 32
             };
    }
}
