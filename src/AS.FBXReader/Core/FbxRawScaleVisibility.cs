using System.IO.Compression;
using System.Text;
using OpenTK.Mathematics;

namespace AS.FBXReader.Core;

/// <summary>
/// Read the FBX SDK's authored Lcl Scaling animation curves by numeric object
/// ID, preserving the AnimationStack/Layer/CurveNode/Curve connections.
/// Assimp can synthesize zero-scale keys on otherwise visible SpriteSkin mesh
/// nodes, especially when its FBX optimizer folds animation channels. These
/// original curves are authoritative for sprite enable/disable timing.
/// Geometry, rotation, translation and skinning still come from Assimp.
/// </summary>
public sealed class FbxRawScaleVisibility
{
    private const double FbxTicksPerSecond = 46186158000.0;
    private readonly List<Dictionary<string, ScaleTrack>> _stacks = new();
    private readonly Dictionary<string, int> _stackIndexByName = new(StringComparer.Ordinal);
    public int AnimationCount => _stacks.Count;
    public int TrackCount => _stacks.Sum(stack => stack.Count);
    // Some AssetStudio exports serialize every static SpriteSkin at scale=0
    // but omit their scale tracks from ALL takes. Such meshes form the fixed
    // body/head/limbs while animated alternatives have explicit 0/1 curves.
    public bool HasAnyScaleCurve(string modelName)
        => _stacks.Any(stack => stack.ContainsKey(modelName));


    private sealed record Curve(long[] Times, float[] Values)
    {
        public float Hold(double ticks, float fallback)
        {
            if (Times.Length == 0 || Values.Length == 0)
                return fallback;

            var low = 0;
            var high = Math.Min(Times.Length, Values.Length) - 1;
            // FBX visibility switches are discrete. Take the last key at or
            // before this instant (including duplicate timestamps).
            if (ticks < Times[0])
                return Values[0];
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (Times[mid] <= ticks)
                    low = mid;
                else
                    high = mid - 1;
            }
            return Values[low];
        }
    }

    private sealed class ScaleTrack
    {
        public Curve? X;
        public Curve? Y;
        public Curve? Z;

        public Vector3 Sample(double seconds, Vector3 fallback)
        {
            var ticks = Math.Max(0, seconds) * FbxTicksPerSecond;
            return new Vector3(
                X?.Hold(ticks, fallback.X) ?? fallback.X,
                Y?.Hold(ticks, fallback.Y) ?? fallback.Y,
                Z?.Hold(ticks, fallback.Z) ?? fallback.Z);
        }
    }

    public bool TrySampleScale(int animationIndex, string modelName,
        double seconds, Vector3 fallback, out Vector3 scale,
        string? animationName = null)
    {
        scale = fallback;
        // Assimp may reorder FBX takes on import. Match the authored take
        // by name when available, and fall back to source index otherwise.
        if (!string.IsNullOrEmpty(animationName) &&
            _stackIndexByName.TryGetValue(animationName, out var sourceIndex))
            animationIndex = sourceIndex;
        if (animationIndex < 0 || animationIndex >= _stacks.Count ||
            !_stacks[animationIndex].TryGetValue(modelName, out var track))
            return false;

        scale = track.Sample(seconds, fallback);
        return true;
    }

    /// <summary>Describe the original compressed FBX curve, not the Assimp output.</summary>
    public string DescribeTrack(int animationIndex, string modelName,
        string? animationName = null)
    {
        if (!string.IsNullOrEmpty(animationName) &&
            _stackIndexByName.TryGetValue(animationName, out var sourceIndex))
            animationIndex = sourceIndex;

        if (animationIndex < 0 || animationIndex >= _stacks.Count)
            return "NO_MATCHING_SOURCE_TAKE";
        if (!_stacks[animationIndex].TryGetValue(modelName, out var track))
            return "NO_AUTHORED_SCALE_CURVE";

        static string Describe(Curve? c)
        {
            if (c is null) return "missing";
            if (c.Values.Length == 0) return "empty";
            var min = c.Values.Min();
            var max = c.Values.Max();
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} keys, range [{1:0.###},{2:0.###}], first={3:0.###}, last={4:0.###}",
                c.Values.Length, min, max, c.Values[0], c.Values[^1]);
        }
        return "X: " + Describe(track.X) +
               " | Y: " + Describe(track.Y) +
               " | Z: " + Describe(track.Z);
    }

    public static FbxRawScaleVisibility Read(string path)
    {
        var output = new FbxRawScaleVisibility();
        using var stream = File.OpenRead(path);
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(br.ReadBytes(20)) != "Kaydara FBX Binary  ")
            return output;

        stream.Position = 23;
        var wide = br.ReadUInt32() >= 7500;
        stream.Position = 27;

        var models = new Dictionary<long, string>();
        var stackIds = new List<long>();
        var stackNames = new Dictionary<long, string>();
        var layerIds = new HashSet<long>();
        var nodeIds = new HashSet<long>();
        var curves = new Dictionary<long, Curve>();
        var connections = new List<Connection>();

        while (HasNode(stream, wide, stream.Length))
        {
            var header = ReadNode(br, wide);
            if (header.End == 0 || !Valid(header, stream))
                break;

            if (header.Name == "Objects")
                ParseObjects(br, header.End, wide, models, stackIds,
                    stackNames, layerIds, nodeIds, curves);
            else if (header.Name == "Connections")
                ParseConnections(br, header.End, wide, connections);

            stream.Position = header.End;
        }

        var layerToStack = new Dictionary<long, long>();
        var nodeToLayer = new Dictionary<long, long>();
        var nodeToModel = new Dictionary<long, long>();
        var nodeProps = new Dictionary<long, string>();
        var nodeCurves = new Dictionary<long, Dictionary<string, long>>();

        foreach (var c in connections)
        {
            if (c.Kind == "OO")
            {
                if (layerIds.Contains(c.Source) && stackIds.Contains(c.Target))
                    layerToStack[c.Source] = c.Target;
                else if (nodeIds.Contains(c.Source) && layerIds.Contains(c.Target))
                    nodeToLayer[c.Source] = c.Target;
            }
            else if (c.Kind == "OP")
            {
                if (nodeIds.Contains(c.Source) && models.ContainsKey(c.Target))
                {
                    nodeToModel[c.Source] = c.Target;
                    nodeProps[c.Source] = c.Property ?? "";
                }
                else if (curves.ContainsKey(c.Source) && nodeIds.Contains(c.Target))
                {
                    if (!nodeCurves.TryGetValue(c.Target, out var components))
                        nodeCurves[c.Target] = components =
                            new Dictionary<string, long>(StringComparer.Ordinal);
                    components[c.Property ?? ""] = c.Source;
                }
            }
        }

        var stackPositions = stackIds.Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        foreach (var stackId in stackIds)
        {
            var index = output._stacks.Count;
            output._stacks.Add(new Dictionary<string, ScaleTrack>(StringComparer.Ordinal));
            if (stackNames.TryGetValue(stackId, out var takeName) &&
                !string.IsNullOrEmpty(takeName))
                output._stackIndexByName.TryAdd(takeName, index);
        }

        foreach (var (nodeId, modelId) in nodeToModel)
        {
            if (!nodeProps.TryGetValue(nodeId, out var property) ||
                property != "Lcl Scaling" || !models.TryGetValue(modelId, out var name) ||
                !nodeToLayer.TryGetValue(nodeId, out var layerId) ||
                !layerToStack.TryGetValue(layerId, out var stackId) ||
                !stackPositions.TryGetValue(stackId, out var stackIndex) ||
                !nodeCurves.TryGetValue(nodeId, out var components))
                continue;

            var track = new ScaleTrack();
            foreach (var (component, id) in components)
            {
                if (!curves.TryGetValue(id, out var curve))
                    continue;
                switch (component)
                {
                    case "d|X": track.X = curve; break;
                    case "d|Y": track.Y = curve; break;
                    case "d|Z": track.Z = curve; break;
                }
            }

            if (track.X is not null || track.Y is not null || track.Z is not null)
                output._stacks[stackIndex][name] = track;
        }

        return output;
    }

    private static void ParseObjects(BinaryReader br, long end, bool wide,
        Dictionary<long, string> models, List<long> stackIds,
        Dictionary<long, string> stackNames, HashSet<long> layerIds,
        HashSet<long> nodeIds, Dictionary<long, Curve> curves)
    {
        var stream = br.BaseStream;
        while (HasNode(stream, wide, end))
        {
            var h = ReadNode(br, wide);
            if (h.End == 0 || h.End > end || !Valid(h, stream))
                break;

            switch (h.Name)
            {
                case "Model":
                {
                    var id = ReadProperty(br);
                    var name = ReadProperty(br);
                    if (id is long n && name is string s)
                        models[n] = s.Split('\0')[0];
                    break;
                }
                case "AnimationStack":
                case "AnimationLayer":
                case "AnimationCurveNode":
                {
                    var id = ReadProperty(br);
                    if (id is long n)
                    {
                        if (h.Name == "AnimationStack")
                        {
                            stackIds.Add(n);
                            if (h.PropertyCount >= 2 && ReadProperty(br) is string take)
                                stackNames[n] = take.Split('\0')[0]
                                    .Replace("AnimStack", string.Empty,
                                        StringComparison.OrdinalIgnoreCase).Trim();
                        }
                        else if (h.Name == "AnimationLayer") layerIds.Add(n);
                        else nodeIds.Add(n);
                    }
                    break;
                }
                case "AnimationCurve":
                {
                    var id = ReadProperty(br);
                    if (id is long n)
                    {
                        for (ulong i = 1; i < h.PropertyCount; i++)
                            ReadProperty(br);

                        long[] times = Array.Empty<long>();
                        float[] values = Array.Empty<float>();
                        while (HasNode(stream, wide, h.End))
                        {
                            var ch = ReadNode(br, wide);
                            if (ch.End == 0 || ch.End > h.End || !Valid(ch, stream))
                                break;

                            if (ch.PropertyCount > 0 && ch.Name == "KeyTime")
                                times = ReadLongArrayProperty(br);
                            if (ch.PropertyCount > 0 && ch.Name == "KeyValueFloat")
                                values = ReadFloatArrayProperty(br);
                            stream.Position = ch.End;
                        }

                        if (times.Length > 0 && times.Length == values.Length)
                            curves[n] = new Curve(times, values);
                    }
                    break;
                }
            }

            stream.Position = h.End;
        }
    }

    private static void ParseConnections(BinaryReader br, long end, bool wide,
        List<Connection> connections)
    {
        var stream = br.BaseStream;
        while (HasNode(stream, wide, end))
        {
            var h = ReadNode(br, wide);
            if (h.End == 0 || h.End > end || !Valid(h, stream))
                break;

            if (h.Name == "C" && h.PropertyCount >= 3)
            {
                var kind = ReadProperty(br) as string;
                var src = ReadProperty(br);
                var dst = ReadProperty(br);
                string? prop = h.PropertyCount >= 4 ? ReadProperty(br) as string : null;
                if (kind is not null && src is long s && dst is long d)
                    connections.Add(new Connection(kind, s, d, prop));
            }
            stream.Position = h.End;
        }
    }

    private static bool HasNode(Stream stream, bool wide, long limit)
        => stream.Position + (wide ? 25 : 13) <= limit;

    private static NodeHeader ReadNode(BinaryReader br, bool wide)
    {
        var end = wide ? checked((long)br.ReadUInt64()) : br.ReadUInt32();
        var count = wide ? br.ReadUInt64() : br.ReadUInt32();
        _ = wide ? br.ReadUInt64() : br.ReadUInt32();
        var name = Encoding.UTF8.GetString(br.ReadBytes(br.ReadByte()));
        return new NodeHeader(end, count, name);
    }

    private static bool Valid(NodeHeader header, Stream stream)
        => header.End >= stream.Position && header.End <= stream.Length;

    private static object? ReadProperty(BinaryReader br)
    {
        var stream = br.BaseStream;
        switch ((char)br.ReadByte())
        {
            case 'Y': return br.ReadInt16();
            case 'C': return br.ReadByte() != 0;
            case 'I': return br.ReadInt32();
            case 'F': return br.ReadSingle();
            case 'D': return br.ReadDouble();
            case 'L': return br.ReadInt64();
            case 'S':
            {
                var size = br.ReadUInt32();
                return Encoding.UTF8.GetString(br.ReadBytes(checked((int)size)));
            }
            case 'R':
            {
                stream.Position += br.ReadUInt32();
                return null;
            }
            case 'f': case 'd': case 'i': case 'l': case 'b': case 'c':
                _ = br.ReadUInt32();
                _ = br.ReadUInt32();
                stream.Position += br.ReadUInt32();
                return null;
            default: throw new InvalidDataException("Unknown FBX property type.");
        }
    }

    private static long[] ReadLongArrayProperty(BinaryReader br)
    {
        if (br.ReadByte() != (byte)'l')
            return Array.Empty<long>();
        var raw = DecompressArray(br, 8, out var count);
        var result = new long[count];
        using var ms = new MemoryStream(raw);
        using var reader = new BinaryReader(ms);
        for (var i = 0; i < count; i++) result[i] = reader.ReadInt64();
        return result;
    }

    private static float[] ReadFloatArrayProperty(BinaryReader br)
    {
        if (br.ReadByte() != (byte)'f')
            return Array.Empty<float>();
        var raw = DecompressArray(br, 4, out var count);
        var result = new float[count];
        using var ms = new MemoryStream(raw);
        using var reader = new BinaryReader(ms);
        for (var i = 0; i < count; i++) result[i] = reader.ReadSingle();
        return result;
    }

    private static byte[] DecompressArray(BinaryReader br, int stride, out int count)
    {
        count = checked((int)br.ReadUInt32());
        if (count > 1_000_000)
            throw new InvalidDataException("FBX animation array exceeds safety limit.");
        var encoding = br.ReadUInt32();
        var byteLength = checked((int)br.ReadUInt32());
        var bytes = br.ReadBytes(byteLength);
        if (bytes.Length != byteLength)
            throw new EndOfStreamException("Truncated FBX keyframe array.");
        if (encoding == 0)
            return bytes;
        if (encoding != 1)
            throw new InvalidDataException("Unknown FBX animation compression.");
        using var compressed = new MemoryStream(bytes);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        inflater.CopyTo(decoded);
        if (decoded.Length != (long)count * stride)
            throw new InvalidDataException("FBX animation decompression length mismatch.");
        return decoded.ToArray();
    }

    private readonly record struct NodeHeader(long End, ulong PropertyCount, string Name);
    private readonly record struct Connection(string Kind, long Source, long Target, string? Property);
}
