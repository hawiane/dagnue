using System.Numerics;
using System.Text.Json;

namespace Nexus.App.Controls;

/// <summary>
/// Loads a deliberately sparse triangle sample from a binary glTF model. Keeping the
/// sample small lets Win2D animate the hologram at interactive frame rates while the
/// vertices still come directly from the supplied 3D asset.
/// </summary>
internal sealed class GlbWireframeModel
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinaryChunk = 0x004E4942;

    public required Vector3[] Vertices { get; init; }
    public required int[] TriangleIndices { get; init; }
    public required Vector3 Minimum { get; init; }
    public required Vector3 Maximum { get; init; }

    public static GlbWireframeModel Load(string path, int triangleBudget = 2400)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != GlbMagic || reader.ReadUInt32() != 2)
            throw new InvalidDataException("The portrait asset is not a glTF 2.0 binary file.");
        _ = reader.ReadUInt32();

        JsonDocument? document = null;
        long binaryOffset = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var length = reader.ReadUInt32();
            var type = reader.ReadUInt32();
            var payloadOffset = stream.Position;
            if (type == JsonChunk)
            {
                var json = reader.ReadBytes(checked((int)length));
                document = JsonDocument.Parse(json);
            }
            else
            {
                if (type == BinaryChunk) binaryOffset = payloadOffset;
                stream.Position = payloadOffset + length;
            }
        }

        using (document)
        {
            if (document is null || binaryOffset < 0)
                throw new InvalidDataException("The GLB is missing its JSON or binary chunk.");

            var root = document.RootElement;
            var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
            if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                throw new InvalidDataException("Only triangle-list portrait meshes are supported.");

            var positionAccessorIndex = primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
            var indexAccessorIndex = primitive.GetProperty("indices").GetInt32();
            var accessors = root.GetProperty("accessors");
            var views = root.GetProperty("bufferViews");
            var positionAccessor = accessors[positionAccessorIndex];
            var indexAccessor = accessors[indexAccessorIndex];
            if (positionAccessor.GetProperty("componentType").GetInt32() != 5126 ||
                positionAccessor.GetProperty("type").GetString() != "VEC3")
                throw new InvalidDataException("The portrait POSITION accessor must contain float VEC3 values.");

            var positionView = views[positionAccessor.GetProperty("bufferView").GetInt32()];
            var indexView = views[indexAccessor.GetProperty("bufferView").GetInt32()];
            var positionBase = AccessorOffset(binaryOffset, positionView, positionAccessor);
            var indexBase = AccessorOffset(binaryOffset, indexView, indexAccessor);
            var positionStride = positionView.TryGetProperty("byteStride", out var ps) ? ps.GetInt32() : 12;
            var indexComponentType = indexAccessor.GetProperty("componentType").GetInt32();
            var indexSize = indexComponentType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 };
            if (indexSize == 0) throw new InvalidDataException("Unsupported portrait index format.");
            var indexStride = indexView.TryGetProperty("byteStride", out var ins) ? ins.GetInt32() : indexSize;
            var triangleCount = indexAccessor.GetProperty("count").GetInt32() / 3;
            var sampleCount = Math.Min(Math.Max(1, triangleBudget), triangleCount);

            var compactVertices = new List<Vector3>(sampleCount * 3);
            var compactIndices = new int[sampleCount * 3];
            var vertexMap = new Dictionary<uint, int>(sampleCount * 3);
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var triangle = (long)sample * triangleCount / sampleCount;
                for (var corner = 0; corner < 3; corner++)
                {
                    var sourceIndex = ReadIndex(reader, indexBase + (triangle * 3 + corner) * indexStride,
                        indexComponentType);
                    if (!vertexMap.TryGetValue(sourceIndex, out var compactIndex))
                    {
                        stream.Position = positionBase + sourceIndex * (long)positionStride;
                        compactIndex = compactVertices.Count;
                        compactVertices.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                        vertexMap.Add(sourceIndex, compactIndex);
                    }
                    compactIndices[sample * 3 + corner] = compactIndex;
                }
            }

            var minimum = ReadVector3(positionAccessor.GetProperty("min"));
            var maximum = ReadVector3(positionAccessor.GetProperty("max"));
            return new GlbWireframeModel
            {
                Vertices = compactVertices.ToArray(),
                TriangleIndices = compactIndices,
                Minimum = minimum,
                Maximum = maximum
            };
        }
    }

    private static long AccessorOffset(long binaryOffset, JsonElement view, JsonElement accessor) =>
        binaryOffset + (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt64() : 0) +
        (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt64() : 0);

    private static uint ReadIndex(BinaryReader reader, long offset, int componentType)
    {
        reader.BaseStream.Position = offset;
        return componentType switch
        {
            5121 => reader.ReadByte(),
            5123 => reader.ReadUInt16(),
            5125 => reader.ReadUInt32(),
            _ => throw new InvalidDataException("Unsupported portrait index format.")
        };
    }

    private static Vector3 ReadVector3(JsonElement element) =>
        new(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());
}
