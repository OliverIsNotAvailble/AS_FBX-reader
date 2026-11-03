using System.Text;

namespace AS.FBXReader.Core;

/// <summary>
/// Reads the authored DEFAULT visibility of FBX Model nodes directly from the
/// binary file. Assimp occasionally initializes AssetStudio's SpriteSkin nodes
/// with the transform of an animation's first frame instead of the authored
/// rest/default transform. A zero scale in that imported pose does not mean a
/// sprite is permanently hidden in every take.
/// </summary>
public static class FbxAuthoredVisibility
{
    private const string Header = "Kaydara FBX Binary  ";
    private const float Epsilon = 0.00001f;

    public static Dictionary<string, bool> Read(string path)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(Header.Length)) != Header)
            return result;

        stream.Position = 23;
        var wide = reader.ReadUInt32() >= 7500;
        var headerLength = wide ? 25 : 13;
        stream.Position = 27;

        while (stream.Position + headerLength <= stream.Length)
        {
            var node = ReadNode(reader, wide);
            if (node.End == 0 || !Valid(node, stream))
                break;

            if (node.Name == "Objects")
            {
                ReadObjects(reader, node.End, wide, result);
                break;
            }

            stream.Position = node.End;
        }

        return result;
    }

    private static void ReadObjects(BinaryReader reader, long end, bool wide,
        Dictionary<string, bool> result)
    {
        var stream = reader.BaseStream;
        var headerLength = wide ? 25 : 13;
        while (stream.Position + headerLength <= end)
        {
            var node = ReadNode(reader, wide);
            if (node.End == 0 || node.End > end || !Valid(node, stream))
                break;

            if (node.Name == "Model")
                ReadModel(reader, node, wide, result);

            stream.Position = node.End;
        }
    }

    private static void ReadModel(BinaryReader reader, FbxNode node, bool wide,
        Dictionary<string, bool> result)
    {
        var stream = reader.BaseStream;
        string? name = null;

        // Model properties: L id, S name\0\x01Model, S node class.
        for (ulong i = 0; i < node.PropertyCount; i++)
        {
            var value = ReadProperty(reader);
            if (i == 1 && value is string modelName)
            {
                var separator = modelName.IndexOf('\0');
                name = separator >= 0 ? modelName[..separator] : modelName;
            }
        }

        var visible = true; // FBX FbxNode defaults: Visibility=1, Lcl Scaling=(1,1,1).
        var headerLength = wide ? 25 : 13;
        while (stream.Position + headerLength <= node.End)
        {
            var child = ReadNode(reader, wide);
            if (child.End == 0 || child.End > node.End || !Valid(child, stream))
                break;

            if (child.Name == "Properties70")
                ReadProperties70(reader, child.End, wide, ref visible);

            stream.Position = child.End;
        }

        if (!string.IsNullOrEmpty(name))
            result[name] = visible;
    }

    private static void ReadProperties70(BinaryReader reader, long end, bool wide,
        ref bool visible)
    {
        var stream = reader.BaseStream;
        var headerLength = wide ? 25 : 13;
        while (stream.Position + headerLength <= end)
        {
            var node = ReadNode(reader, wide);
            if (node.End == 0 || node.End > end || !Valid(node, stream))
                break;

            if (node.Name == "P")
            {
                var values = new List<object?>();
                for (ulong i = 0; i < node.PropertyCount; i++)
                    values.Add(ReadProperty(reader));

                if (values.Count >= 5 && values[0] is string property)
                {
                    if (property == "Visibility" &&
                        TryNumber(values[^1], out var opacity))
                        visible &= opacity > 0.5;

                    if (property == "Lcl Scaling" && values.Count >= 7 &&
                        TryNumber(values[^3], out var x) &&
                        TryNumber(values[^2], out var y))
                        visible &= Math.Abs(x) > Epsilon && Math.Abs(y) > Epsilon;
                }
            }

            stream.Position = node.End;
        }
    }

    private static bool TryNumber(object? value, out double number)
    {
        number = 0;
        if (value is not (double or float or int or long or short or bool))
            return false;
        number = Convert.ToDouble(value);
        return double.IsFinite(number);
    }

    private static FbxNode ReadNode(BinaryReader reader, bool wide)
    {
        var end = wide ? checked((long)reader.ReadUInt64()) : reader.ReadUInt32();
        var count = wide ? reader.ReadUInt64() : reader.ReadUInt32();
        _ = wide ? reader.ReadUInt64() : reader.ReadUInt32(); // property bytes
        var nameLength = reader.ReadByte();
        var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
        return new FbxNode(end, count, name);
    }

    private static bool Valid(FbxNode node, Stream stream)
        => node.End >= stream.Position && node.End <= stream.Length;

    private static object? ReadProperty(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        var type = (char)reader.ReadByte();
        switch (type)
        {
            case 'Y': return reader.ReadInt16();
            case 'C': return reader.ReadByte() != 0;
            case 'I': return reader.ReadInt32();
            case 'F': return reader.ReadSingle();
            case 'D': return reader.ReadDouble();
            case 'L': return reader.ReadInt64();

            case 'S':
            {
                var size = reader.ReadUInt32();
                return Encoding.UTF8.GetString(reader.ReadBytes(checked((int)size)));
            }

            case 'R':
            {
                var size = reader.ReadUInt32();
                stream.Position += size;
                return null;
            }

            case 'f':
            case 'd':
            case 'i':
            case 'l':
            case 'b':
            case 'c':
            {
                _ = reader.ReadUInt32(); // count
                _ = reader.ReadUInt32(); // compressed?
                stream.Position += reader.ReadUInt32();
                return null;
            }

            default:
                throw new InvalidDataException($"Unsupported FBX property type: {type}");
        }
    }

    private readonly record struct FbxNode(long End, ulong PropertyCount, string Name);
}
