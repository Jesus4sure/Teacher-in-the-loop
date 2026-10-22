using System.Buffers.Binary;
using System.Text;

namespace Titlape.CrossCutting.Communication;

/// <summary>
/// Protocol Buffers wire format (proto3 encoding rules: varints, zig-zag, length-delimited,
/// fixed64). Implemented natively so edge nodes need no code generator; messages produced here are
/// readable by any standard protobuf decoder that has the matching .proto schema.
/// </summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public enum WireType { Varint = 0, Fixed64 = 1, LengthDelimited = 2, Fixed32 = 5 }

    public ProtoWriter Tag(int field, WireType wt) { WriteVarint(((ulong)field << 3) | (uint)wt); return this; }

    public ProtoWriter UInt64(int field, ulong value)
    {
        if (value == 0) return this; // proto3 default elision
        Tag(field, WireType.Varint); WriteVarint(value); return this;
    }

    public ProtoWriter Int64(int field, long value) => UInt64(field, ZigZag(value));
    public ProtoWriter Int32(int field, int value) => UInt64(field, ZigZag(value));
    public ProtoWriter Bool(int field, bool value) => UInt64(field, value ? 1UL : 0UL);

    public ProtoWriter Double(int field, double value)
    {
        if (value == 0) return this;
        Tag(field, WireType.Fixed64);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(buf, value);
        _ms.Write(buf);
        return this;
    }

    public ProtoWriter String(int field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return this;
        return Bytes(field, Encoding.UTF8.GetBytes(value));
    }

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return this;
        Tag(field, WireType.LengthDelimited);
        WriteVarint((ulong)value.Length);
        _ms.Write(value);
        return this;
    }

    public ProtoWriter Message(int field, IProtoMessage msg) => Bytes(field, msg.ToProto());

    public byte[] ToArray() => _ms.ToArray();

    private void WriteVarint(ulong v)
    {
        while (v >= 0x80) { _ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
        _ms.WriteByte((byte)v);
    }

    private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
}

public ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> buf) { _buf = buf; _pos = 0; }

    public bool TryReadTag(out int field, out ProtoWriter.WireType wireType)
    {
        if (_pos >= _buf.Length) { field = 0; wireType = default; return false; }
        var tag = ReadVarint();
        field = (int)(tag >> 3);
        wireType = (ProtoWriter.WireType)(tag & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0; int shift = 0;
        while (true)
        {
            if (_pos >= _buf.Length) throw new FormatException("Truncated varint");
            var b = _buf[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new FormatException("Varint too long");
        }
    }

    public long ReadSInt64() { var v = ReadVarint(); return (long)(v >> 1) ^ -(long)(v & 1); }
    public int ReadSInt32() => (int)ReadSInt64();
    public bool ReadBool() => ReadVarint() != 0;

    public double ReadDouble()
    {
        var d = BinaryPrimitives.ReadDoubleLittleEndian(_buf.Slice(_pos, 8));
        _pos += 8; return d;
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var len = (int)ReadVarint();
        var s = _buf.Slice(_pos, len); _pos += len; return s;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public void Skip(ProtoWriter.WireType wt)
    {
        switch (wt)
        {
            case ProtoWriter.WireType.Varint: ReadVarint(); break;
            case ProtoWriter.WireType.Fixed64: _pos += 8; break;
            case ProtoWriter.WireType.Fixed32: _pos += 4; break;
            case ProtoWriter.WireType.LengthDelimited: ReadBytes(); break;
            default: throw new FormatException($"Unknown wire type {wt}");
        }
    }
}

public interface IProtoMessage
{
    byte[] ToProto();
}
