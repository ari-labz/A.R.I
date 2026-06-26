using System.Buffers.Binary;
using System.Text;

namespace ARI.LLM;

/// <summary>
/// Reads a small subset of GGUF metadata needed for KV-cache size estimation:
/// block_count (n_layers), attention.head_count_kv (n_kv_heads), attention.key_length (head_dim).
/// Spec: https://github.com/ggerganov/ggml/blob/master/docs/gguf.md
/// </summary>
public static class GgufReader
{
    private const uint MagicGGUF = 0x46554747; // "GGUF"

    public record KvArchParams(int NLayers, int NKvHeads, int HeadDim);

    public static KvArchParams? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            if (br.ReadUInt32() != MagicGGUF) return null;
            var version = br.ReadUInt32(); // 1, 2, or 3
            if (version is < 1 or > 3) return null;

            var tensorCount  = version >= 2 ? (long)br.ReadUInt64() : br.ReadUInt32();
            var metaKvCount  = version >= 2 ? (long)br.ReadUInt64() : br.ReadUInt32();

            int? nLayers = null, nKvHeads = null, headDim = null;

            for (long i = 0; i < metaKvCount; i++)
            {
                var key   = ReadString(br);
                var vtype = (GgufValueType)br.ReadUInt32();
                var value = ReadValue(br, vtype);

                // Keys look like "llama.block_count", "qwen2.attention.head_count_kv", etc.
                var bare = key.Contains('.') ? key[(key.IndexOf('.') + 1)..] : key;

                if      (bare == "block_count")              nLayers  = ToInt(value);
                else if (bare == "attention.head_count_kv")  nKvHeads = ToInt(value);
                else if (bare is "attention.key_length" or "rope.dimension_count") headDim = ToInt(value);

                if (nLayers.HasValue && nKvHeads.HasValue && headDim.HasValue)
                    return new(nLayers.Value, nKvHeads.Value, headDim.Value);
            }

            // head_dim fallback: if not present, llama.cpp uses head_dim = embedding / n_heads
            // We won't have n_heads easily here, so return partial if we have the critical two
            if (nLayers.HasValue && nKvHeads.HasValue && headDim.HasValue)
                return new(nLayers.Value, nKvHeads.Value, headDim.Value);
        }
        catch { /* corrupt / truncated file */ }

        return null;
    }

    // ── private helpers ──────────────────────────────────────────────

    private enum GgufValueType : uint
    {
        Uint8 = 0, Int8 = 1, Uint16 = 2, Int16 = 3,
        Uint32 = 4, Int32 = 5, Float32 = 6, Bool = 7,
        String = 8, Array = 9, Uint64 = 10, Int64 = 11, Float64 = 12,
    }

    private static string ReadString(BinaryReader br)
    {
        var len  = (long)br.ReadUInt64();
        var bytes = br.ReadBytes((int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static object ReadValue(BinaryReader br, GgufValueType vtype) => vtype switch
    {
        GgufValueType.Uint8   => br.ReadByte(),
        GgufValueType.Int8    => br.ReadSByte(),
        GgufValueType.Uint16  => br.ReadUInt16(),
        GgufValueType.Int16   => br.ReadInt16(),
        GgufValueType.Uint32  => br.ReadUInt32(),
        GgufValueType.Int32   => br.ReadInt32(),
        GgufValueType.Float32 => br.ReadSingle(),
        GgufValueType.Bool    => br.ReadByte(),
        GgufValueType.String  => ReadString(br),
        GgufValueType.Uint64  => br.ReadUInt64(),
        GgufValueType.Int64   => br.ReadInt64(),
        GgufValueType.Float64 => br.ReadDouble(),
        GgufValueType.Array   => SkipArray(br),
        _                     => throw new InvalidDataException($"Unknown GGUF type {vtype}"),
    };

    private static object SkipArray(BinaryReader br)
    {
        var elemType  = (GgufValueType)br.ReadUInt32();
        var count     = (long)br.ReadUInt64();
        for (long i = 0; i < count; i++) ReadValue(br, elemType);
        return 0;
    }

    private static int? ToInt(object v) => v switch
    {
        byte b    => b,
        sbyte sb  => sb,
        ushort us => us,
        short s   => s,
        uint ui   => (int)ui,
        int i     => i,
        ulong ul  => (int)ul,
        long l    => (int)l,
        _         => null,
    };
}
