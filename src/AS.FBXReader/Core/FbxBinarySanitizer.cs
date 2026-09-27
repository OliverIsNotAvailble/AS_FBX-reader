using System.Text;

namespace AS.FBXReader.Core;

// AssetStudio's FBX SDK output can legally contain multiple Model objects with
// identical names in different branches (e.g. two independent "bone_1" nodes).
//
// Assimp's FBX animation converter groups animation curves by the Model name,
// not the FBX object ID. That can merge two independent skeleton nodes before
// AS_FBX-reader even receives the aiScene.
//
// We only modify the temporary staged FBX, never the user's original file.
// Duplicate Model names are replaced in-place with same-byte-length ASCII names.
// Because FBX object relationships use numeric IDs, no Connection record changes.
public static class FbxBinarySanitizer
{
    private static readonly byte[] BinaryHeader =
        Encoding.ASCII.GetBytes("Kaydara FBX Binary  ");

    public static int MakeDuplicateModelNamesUnique(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);

        using var reader = new BinaryReader(
            stream,
            Encoding.UTF8,
            leaveOpen: true);

        if (!IsBinaryFbx(reader))
            return 0;

        stream.Position = 23;
        var version = reader.ReadUInt32();
        var wide = version >= 7500;
        var nullRecordSize = wide ? 25 : 13;

        stream.Position = 27;

        NodeHeader? objects = null;

        while (stream.Position + nullRecordSize <= stream.Length)
        {
            var header = ReadNodeHeader(reader, wide);

            if (header.EndOffset == 0)
                break;

            if (string.Equals(
                    header.Name,
                    "Objects",
                    StringComparison.Ordinal))
            {
                objects = header;
                break;
            }

            stream.Position = checked((long)header.EndOffset);
        }

        if (objects is null)
            return 0;

        var modelNames = new List<ModelNameEntry>();

        while (stream.Position + nullRecordSize <= (long)objects.Value.EndOffset)
        {
            var childStart = stream.Position;
            var child = ReadNodeHeader(reader, wide);

            if (child.EndOffset == 0)
                break;

            if (string.Equals(
                    child.Name,
                    "Model",
                    StringComparison.Ordinal))
            {
                var entry = TryReadModelName(
                    reader,
                    child,
                    childStart);

                if (entry is not null)
                    modelNames.Add(entry.Value);
            }

            stream.Position = checked((long)child.EndOffset);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reserved = modelNames
            .Select(x => x.BaseName)
            .ToHashSet(StringComparer.Ordinal);

        var replacements = new List<(long Position, byte[] Bytes)>();
        var serial = 1;

        foreach (var entry in modelNames)
        {
            if (seen.Add(entry.BaseName))
                continue;

            var replacement = CreateUniqueAsciiName(
                entry.BaseByteLength,
                reserved,
                ref serial);

            reserved.Add(replacement);

            replacements.Add((
                entry.BasePosition,
                Encoding.ASCII.GetBytes(replacement)));
        }

        foreach (var replacement in replacements)
        {
            stream.Position = replacement.Position;
            stream.Write(
                replacement.Bytes,
                0,
                replacement.Bytes.Length);
        }

        stream.Flush();
        return replacements.Count;
    }

    private static bool IsBinaryFbx(BinaryReader reader)
    {
        reader.BaseStream.Position = 0;

        var header = reader.ReadBytes(BinaryHeader.Length);

        return header.SequenceEqual(BinaryHeader);
    }

    private static ModelNameEntry? TryReadModelName(
        BinaryReader reader,
        NodeHeader node,
        long nodeStart)
    {
        // Model's first properties are normally:
        //   L objectId
        //   S "name\0\1Model"
        //   S "LimbNode|Mesh|Null..."
        //
        // Still skip properties generically so this survives minor exporter changes.
        for (ulong i = 0; i < node.PropertyCount; i++)
        {
            var type = (char)reader.ReadByte();

            if (i == 1 && type == 'S')
            {
                var length = reader.ReadUInt32();
                var dataPosition = reader.BaseStream.Position;
                var raw = reader.ReadBytes(checked((int)length));

                var separator = FindModelSeparator(raw);
                var baseLength = separator >= 0
                    ? separator
                    : raw.Length;

                if (baseLength <= 0)
                    return null;

                var baseBytes = raw.AsSpan(0, baseLength).ToArray();
                var baseName = Encoding.UTF8.GetString(baseBytes);

                return new ModelNameEntry(
                    baseName,
                    dataPosition,
                    baseLength);
            }

            SkipPropertyPayload(reader, type);
        }

        return null;
    }

    private static int FindModelSeparator(byte[] raw)
    {
        for (var i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == 0x00 && raw[i + 1] == 0x01)
                return i;
        }

        return -1;
    }

    private static void SkipPropertyPayload(
        BinaryReader reader,
        char type)
    {
        switch (type)
        {
            case 'Y':
                reader.BaseStream.Position += 2;
                return;
            case 'C':
                reader.BaseStream.Position += 1;
                return;
            case 'I':
            case 'F':
                reader.BaseStream.Position += 4;
                return;
            case 'D':
            case 'L':
                reader.BaseStream.Position += 8;
                return;

            case 'S':
            case 'R':
            {
                var length = reader.ReadUInt32();
                reader.BaseStream.Position += length;
                return;
            }

            case 'f':
            case 'd':
            case 'l':
            case 'i':
            case 'b':
            case 'c':
            {
                _ = reader.ReadUInt32(); // element count
                _ = reader.ReadUInt32(); // encoding
                var compressedLength = reader.ReadUInt32();
                reader.BaseStream.Position += compressedLength;
                return;
            }

            default:
                throw new InvalidDataException(
                    $"Unsupported FBX property type '{type}'.");
        }
    }

    private static NodeHeader ReadNodeHeader(
        BinaryReader reader,
        bool wide)
    {
        ulong endOffset;
        ulong propertyCount;
        ulong propertyListLength;

        if (wide)
        {
            endOffset = reader.ReadUInt64();
            propertyCount = reader.ReadUInt64();
            propertyListLength = reader.ReadUInt64();
        }
        else
        {
            endOffset = reader.ReadUInt32();
            propertyCount = reader.ReadUInt32();
            propertyListLength = reader.ReadUInt32();
        }

        var nameLength = reader.ReadByte();

        if (endOffset == 0)
        {
            return new NodeHeader(
                0,
                0,
                0,
                string.Empty);
        }

        var name = Encoding.UTF8.GetString(
            reader.ReadBytes(nameLength));

        return new NodeHeader(
            endOffset,
            propertyCount,
            propertyListLength,
            name);
    }

    private static string CreateUniqueAsciiName(
        int byteLength,
        HashSet<string> reserved,
        ref int serial)
    {
        if (byteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteLength));

        while (true)
        {
            var counter = ToBase36(serial++);
            string candidate;

            if (byteLength == 1)
            {
                candidate = counter[^1].ToString();
            }
            else
            {
                var payloadLength = byteLength - 1;

                if (counter.Length > payloadLength)
                {
                    counter = counter[^payloadLength..];
                }

                candidate =
                    "~" +
                    counter.PadLeft(
                        payloadLength,
                        '0');
            }

            if (!reserved.Contains(candidate))
                return candidate;
        }
    }

    private static string ToBase36(int value)
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        if (value <= 0)
            return "0";

        Span<char> buffer = stackalloc char[16];
        var pos = buffer.Length;

        while (value > 0)
        {
            buffer[--pos] = alphabet[value % 36];
            value /= 36;
        }

        return new string(buffer[pos..]);
    }

    private readonly record struct NodeHeader(
        ulong EndOffset,
        ulong PropertyCount,
        ulong PropertyListLength,
        string Name);

    private readonly record struct ModelNameEntry(
        string BaseName,
        long BasePosition,
        int BaseByteLength);
}
