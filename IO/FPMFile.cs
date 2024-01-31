using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Processing;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Image = SixLabors.ImageSharp.Image;

namespace QWCModelTool
{
    struct BoundingBox
    {
        public Vector4 minBox { get; set; } 
        public Vector4 maxBox { get; set; }
    }
    struct FPMHeader
    {
        public FPMHeader(uint magic, uint length, uint firstShape)
        {
            Magic = magic;
            Length = length;
            FirstShape = firstShape;
        }

        public static FPMHeader Parse(Span<byte> data)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4,8));
            var firstShape = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            return new FPMHeader(magic, length, firstShape);
        }

        public UInt32 Magic { get; } // FPM1
        public UInt32 Length { get; }
        // UInt32 unk08;
        public UInt32 FirstShape { get; }
        // UInt32 pad10;
        // Matrix4x4 identity;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TextureInfo
    {
        public UInt32 textureOffset;
        public UInt32 unk00;
        public UInt32 unk08;

        public static TextureInfo Parse(ReadOnlySpan<byte> buffer) => MemoryMarshal.Cast<byte, TextureInfo>(buffer)[0];
    };

    struct FPMDirectory
    {
        // public BoundingBox Box { get; }
        public UInt32 DirCount { get; }
        public UInt32 FirstEntryOffset { get; }
        public UInt32 TextureCount { get; }
        // UInt32 FixedVal; // always 0x90

        public FPMDirectory(uint dirCount, uint firstEntryOffset, uint textureCount) : this()
        {
            DirCount = dirCount;
            FirstEntryOffset = firstEntryOffset;
            TextureCount = textureCount;
        }

        public static FPMDirectory Parse(Span<byte> data)
        {
            var dirCount = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
            var firstEntry = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 8));
            var textureCount = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);

            var fixedVal = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            if (fixedVal != 0x90)
            {
                throw new InvalidDataException("Invalid fixed value in FPM directory");
            }

            return new FPMDirectory(dirCount, firstEntry, textureCount);
        }
    }

    struct ShapeNode
    {
        public readonly static int SIZE = 0x30;

        public BoundingBox Box { get; }
        public UInt32 DataOffset { get; }
        public UInt16 TextureIndex { get; }

        public ShapeNode(BoundingBox box, uint dataOffset, ushort textureIndex)
        {
            this.Box = box;
            this.DataOffset = dataOffset;
            this.TextureIndex = textureIndex;
        }

        public static ShapeNode Parse(Span<byte> data) => new ShapeNode(
            box: MemoryMarshal.Cast<byte, BoundingBox>(data[..0x20])[0],
            dataOffset: BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x20, 4)),
            textureIndex: BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x2C,2))
            );

    }

    struct ShapeDataHeader
    {
        UInt32 faceDataOffset;
        public UInt16 FaceCount { get; }
        public byte VertexStride;
        UInt16 vertexCount;

        public ShapeDataHeader(uint faceDataOffset, ushort faceCount, byte vertexLen, ushort vertexCount)
        {
            this.faceDataOffset = faceDataOffset;
            this.FaceCount = faceCount;
            this.VertexStride = vertexLen;
            this.vertexCount = vertexCount;
        }

        public static ShapeDataHeader Parse(Span<byte> data) => new ShapeDataHeader(
            faceDataOffset: BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)),
            faceCount: BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x12, 2)),
            vertexLen: data[0x14],
            vertexCount: BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x16, 2))
        );

        public long BlockSize() => (uint)VertexStride * vertexCount;
    }
   
    #region SHAPE_DATA_TYPES 
    interface IShapeDataContainer : IDisposable
    {
        abstract (IVertexGeometry, IVertexMaterial) GetVertexGeometry(int index);
    }

    interface IShapeTrivialData
    {
        (IVertexGeometry, IVertexMaterial) GetVertexGeometry();
    }

    struct ShapeDataContainer<T>: IShapeDataContainer where T : struct,IShapeTrivialData
    {
        private IMemoryOwner<byte> vertexBuffer;

        public ShapeDataContainer(IMemoryOwner<byte> vertexBuffer)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                new NotSupportedException(typeof(T).Name);

            this.vertexBuffer = vertexBuffer;
        }

        public (IVertexGeometry, IVertexMaterial) GetVertexGeometry(int index)
        {
            int size = Unsafe.SizeOf<T>();
            T temp = MemoryMarshal.Cast<byte, T>(vertexBuffer.Memory.Span.Slice(size * index, size))[0];
            return temp.GetVertexGeometry();
        }

        public void Dispose()
        {
            vertexBuffer.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ShapeDataFullPadded : IShapeTrivialData
    { 
            public Vector3 vertexPos;
            public Vector3 normal;
            public Vector2 uv;
            UInt32 padding;

        public (IVertexGeometry, IVertexMaterial) GetVertexGeometry()
        {
            return (new VertexPositionNormal(vertexPos, normal), new VertexTexture1(uv));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ShapeDataFull : IShapeTrivialData
    {
        public Vector3 vertexPos;
        public Vector3 normal;
        public Vector2 uv;

        public (IVertexGeometry, IVertexMaterial) GetVertexGeometry()
        {
            return (new VertexPositionNormal(vertexPos, normal), new VertexTexture1(uv));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ShapeDataCompact : IShapeTrivialData
    {
        public Vector3 vertexPos;
        public Vector2 uv;

        public (IVertexGeometry, IVertexMaterial) GetVertexGeometry()
        {
            return (new VertexPosition(vertexPos), new VertexTexture1(uv));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ShapeDataCompactPadded : IShapeTrivialData
    {
        public Vector3 vertexPos;
        public UInt32 padding;
        public Vector2 uv;

        public (IVertexGeometry, IVertexMaterial) GetVertexGeometry()
        {
            return (new VertexPosition(vertexPos), new VertexTexture1(uv));
        }
    }
    #endregion

    struct NodeEntry
    {

        public readonly static int SIZE = 0x30;

        public BoundingBox Box { get; }
        public UInt32 CountNode { get; }
        public UInt32 SubNodeOffset { get; }

        public NodeEntry(BoundingBox box, uint countNode, uint subNodeOffset)
        {
            Box = box;
            CountNode = countNode;
            SubNodeOffset = subNodeOffset;
        }

        public static NodeEntry Parse(Span<byte> data) => new NodeEntry(
        box: MemoryMarshal.Cast<byte, BoundingBox>(data[..0x20])[0],
        countNode: BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x20, 4)),
        subNodeOffset: BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x24, 4))
        );
    }
    
    internal class FPMFile: IDisposable
    {
        FPMHeader header;
        FPMDirectory rootDir;
        ShapeNode[][] shapeNodes;
        List<string> textures = new List<string>();
        
        Stream _stream;
        string absPath;

        const UInt32 FPM1 = 0x314D5046;

        public FPMFile(string filePath)
        {
            if(!File.Exists(filePath))
            {
                throw new FileNotFoundException();
            }

            absPath = Path.GetFullPath(filePath);

            _stream = File.OpenRead(filePath);
            if (!_stream.CanSeek)
            {
                throw new NotSupportedException();
            }
        }

        private Image GetImage(int index)
        {
            string input = Path.Combine(Path.GetDirectoryName(absPath), textures[index]);
            using FileStream fs = new FileStream(input, FileMode.Open);
            return FshImage.LoadImage(fs);
        }

        public void Decode()
        {
            ParseHeader();
            if (header.Magic != FPM1)
            {
                throw new InvalidDataException("Unexpected header magic string");
            }

            _stream.Position += 0x50; // blank 16 bytes followed by 4x4 identity matrix?
            ParseDirectory();

            // Parse texture list
            ParseTextures();

            _stream.Seek(rootDir.FirstEntryOffset, SeekOrigin.Begin);
            // Parse and validate sequential dir entries before wildly seeking along the file
            List<NodeEntry> nodes = new List<NodeEntry>();
            shapeNodes = new ShapeNode[rootDir.DirCount][];
            for (int i = 0; i < rootDir.DirCount; i++)
            {
                nodes.Add(ParseNode());
            }

            // Change this if you encounter any dir entry with multiple sub-nodes
            for (int i  = 0;  i < nodes.Count; i++)
            {
                _stream.Position = nodes[i].SubNodeOffset;
                var shape = ParseShapeNode();
                shapeNodes[i] = shape.ToArray();
            }
        }

        public void SaveMeshHierarchy(string outputFile, float? lmAlpha = 0.5f)
        {
            var model = ModelRoot.CreateModel();
            var root = model.UseScene(0).CreateNode("Root");
            Span<byte> headerBuff = stackalloc byte[0x20];

            // Create a material for each texture
            MaterialBuilder[] materialBuilder = new MaterialBuilder[textures.Count];
            for (int i = 0; i < textures.Count; i++)
            {
                string output = Path.ChangeExtension(textures[i], "png");
                output = Path.Combine(Path.GetDirectoryName(absPath), output);
                var img = GetImage(i);

                // Remove extension (all textures end in .fsh)
                string matlName = textures[i][..^4];

                if (textures[i].StartsWith("lm_", StringComparison.OrdinalIgnoreCase))
                {
                    if (lmAlpha.HasValue)
                    {
                        // Add fixed transparency to lightmaps
                        img.Mutate(x => x.Opacity(lmAlpha.Value));

                        materialBuilder[i] = new MaterialBuilder($"{matlName}")
                        .WithMetallicRoughness(0, 0)
                        .WithDoubleSide(true)
                        .WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND);
                    }
                }
                else
                {
                    // Add a fixed transparency to lightmap image for Blender Eevee at least
                    materialBuilder[i] = new MaterialBuilder($"{matlName}")
                        .WithUnlitShader()
                        .WithDoubleSide(true)
                        .WithAlpha(SharpGLTF.Materials.AlphaMode.MASK, 0.0f);
                }

                img.SaveAsPng(output);
                materialBuilder[i] = materialBuilder[i].WithBaseColor(output);
            }
            int j = 0;
            foreach (var node in shapeNodes)
            {
                var cNode = root.CreateNode($"Node[{j++}]");
                foreach (var shape in node)
                {
                    // Lightmap shapes
                    if (materialBuilder[shape.TextureIndex] == default) 
                        continue;

                    _stream.Position = shape.DataOffset;
                    _stream.Read(headerBuff);
                    _stream.Position += 0x10;
                    var header = ShapeDataHeader.Parse(headerBuff);

                    // Round to the nearest address
                    _stream.Position = (_stream.Position + 15) & ~15;

                    var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("mesh");
                    var prim = mesh.UsePrimitive(materialBuilder[shape.TextureIndex]);

                    using var shapeData = CreateDataContainer(header);

                    _stream.Position = (_stream.Position + 15) & ~15;

                    var triBuffer = ParseTriStrips(header);
                    for (int i = 0; i < triBuffer.Length - 2; ++i)
                    {
                        if (i % 2 != 0)
                        {
                            var A = shapeData.GetVertexGeometry(triBuffer[i + 2]);
                            var B = shapeData.GetVertexGeometry(triBuffer[i + 1]);
                            var C = shapeData.GetVertexGeometry(triBuffer[i]);

                            prim.AddTriangle((new VertexPositionNormal(A.Item1), new VertexTexture1(A.Item2)),
                                (new VertexPositionNormal(B.Item1), new VertexTexture1(B.Item2)),
                                (new VertexPositionNormal(C.Item1), new VertexTexture1(C.Item2)));
                        }
                        else
                        {
                            var A = shapeData.GetVertexGeometry(triBuffer[i]);
                            var B = shapeData.GetVertexGeometry(triBuffer[i + 1]);
                            var C = shapeData.GetVertexGeometry(triBuffer[i + 2]);
                            prim.AddTriangle((new VertexPositionNormal(A.Item1), new VertexTexture1(A.Item2)),
                                (new VertexPositionNormal(B.Item1), new VertexTexture1(B.Item2)),
                                (new VertexPositionNormal(C.Item1), new VertexTexture1(C.Item2)));
                        }
                    }
                    var m = model.CreateMesh(mesh);
                    cNode.CreateNode($"{materialBuilder[shape.TextureIndex].Name}").WithMesh(m);
                }
            }
            model.Save(outputFile);
        }

        private IShapeDataContainer CreateDataContainer(ShapeDataHeader header)
        {
            // Cast the whole block at once, and move onto the tri strips
            IMemoryOwner<byte> buffer = MemoryAllocator.Default.Allocate<byte>((int)header.BlockSize());
            _stream.Read(buffer.Memory.Span);

            switch (header.VertexStride)
            {
                case 0x14:
                    return new ShapeDataContainer<ShapeDataCompact>(buffer);
                case 0x18:
                    return new ShapeDataContainer<ShapeDataCompactPadded>(buffer);
                case 0x20:
                    return new ShapeDataContainer<ShapeDataFull>(buffer);
                case 0x24:
                    return new ShapeDataContainer<ShapeDataFullPadded>(buffer);
            }

            return null;
        }

        private void ParseHeader()
        {
            Span<byte> buffer = stackalloc byte[16];
            if (_stream.Read(buffer) != buffer.Length)
            {
                throw new InvalidDataException("Unable to read header");
            }

            header = FPMHeader.Parse(buffer);
        }

        private void ParseTextures()
        {
            List<TextureInfo> textureInfos = new List<TextureInfo>((int)rootDir.TextureCount);
            Span<byte> buffer = stackalloc byte[12];

            for (uint i = 0; i < rootDir.TextureCount; i++) 
            {
                _stream.Read(buffer);
                textureInfos.Add(TextureInfo.Parse(buffer));
            }
            // Separating these loops as I don't know if the unknown bytes will come in handy later
            foreach(TextureInfo texInfo in textureInfos) 
            {
                if(texInfo.textureOffset > _stream.Length)
                {
                    throw new InvalidDataException("Invalid texture offset");
                }
                _stream.Seek(texInfo.textureOffset, SeekOrigin.Begin);

                // Read until we hit null char
                int val = _stream.ReadByte();
                byte[] nameBuf = new byte[0xFF];
                for (int i = 0; i < nameBuf.Length && (val > 0); ++i)
                {
                    nameBuf[i] = (byte)val;
                    val = _stream.ReadByte();
                }

                string name = System.Text.Encoding.ASCII.GetString(nameBuf);
                name = name.TrimEnd('\0') + ".fsh";
                textures.Add(name);
            }
        }

        private NodeEntry ParseNode()
        {
            // For each node, skip the first 0x20 bytes
            Span<byte> buffer = stackalloc byte[0x30];
            _stream.Read(buffer);

            var node =  NodeEntry.Parse(buffer);

            if (node.CountNode != 1)
            {
             //   throw new InvalidDataException("Expected a single child node");
            }

            if (node.SubNodeOffset > _stream.Length)
            {
                throw new InvalidDataException("Unexpected offset for child node");
            }

            return node;
        }
        
        private List<ShapeNode> ParseShapeNode()
        {
            Span<byte> buffer = stackalloc byte[ShapeNode.SIZE];
            _stream.Read(buffer);

            var subHeader = buffer[0x20..];
            uint subNodeCount = BinaryPrimitives.ReadUInt32LittleEndian(subHeader[..4]);
            uint subNodeOffset = BinaryPrimitives.ReadUInt32LittleEndian(subHeader[8..]);

            List <ShapeNode> nodeList = new((int)subNodeCount);

            _stream.Position = subNodeOffset;
            for (int i = 0; i < subNodeCount; ++i) 
            {
                _stream.Read(buffer);
                nodeList.Add(ShapeNode.Parse(buffer));
            }
            return nodeList;
        }

        private void ParseDirectory()
        {
            Span<byte> buffer = stackalloc byte[16];
            _stream.Position += 0x20; //Skip bounding box

            if (_stream.Read(buffer) != buffer.Length)
            {
                throw new InvalidDataException("Unable to read directory");
            }

            rootDir = FPMDirectory.Parse(buffer);
        }

        private ushort[] ParseTriStrips(ShapeDataHeader header)
        {
            // Convert Triangle Strips to Triangle Data
            using IMemoryOwner<byte> tempBuf = MemoryAllocator.Default.Allocate<byte>(header.FaceCount * sizeof(UInt16));
            _stream.Read(tempBuf.Memory.Span);
            ReadOnlySpan<ushort> triBuffer = MemoryMarshal.Cast<byte,ushort>(tempBuf.Memory.Span);

            ushort[] tris = triBuffer.ToArray();
            return tris;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
