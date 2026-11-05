using System.Buffers.Binary;
using System.Text;

namespace MasterServer;

/// Writer matching the game's own serialisation conventions, modelled on
/// BattleBitAPI's Common.Serialization.Stream: little-endian throughout, strings as a
/// ushort byte-length prefix followed by UTF-8.
public sealed class PacketWriter
{
    readonly MemoryStream _ms = new();

    public PacketWriter Write(byte v) { _ms.WriteByte(v); return this; }
    public PacketWriter Write(bool v) { _ms.WriteByte(v ? (byte)1 : (byte)0); return this; }

    public PacketWriter Write(ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        _ms.Write(b);
        return this;
    }

    public PacketWriter Write(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        _ms.Write(b);
        return this;
    }

    public PacketWriter Write(int v) => Write((uint)v);

    public PacketWriter Write(ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        _ms.Write(b);
        return this;
    }

    public PacketWriter Write(long v) => Write((ulong)v);

    public PacketWriter Write(float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        _ms.Write(b);
        return this;
    }

    public PacketWriter Write(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        // The cast used to be unchecked, which is the worst outcome available: at 65536
        // bytes the declared length wraps to 0 while the payload is still written, so
        // the reader takes an empty string and then parses the remaining bytes as
        // whatever field comes next. Every caller is bounded well below this, so being
        // here at all is a bug worth hearing about rather than silently corrupting the
        // packet.
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentException(
                $"String is {bytes.Length} bytes; the wire format allows {ushort.MaxValue}.",
                nameof(value));
        Write((ushort)bytes.Length);
        _ms.Write(bytes);
        return this;
    }

    /// A ushort-length-prefixed blob, the shape PlayerStats uses for its
    /// ToolProgress / Achievements / Selections arrays.
    public PacketWriter WriteBlob(ReadOnlySpan<byte> data)
    {
        Write((ushort)data.Length);
        _ms.Write(data);
        return this;
    }

    /// Raw bytes with no length prefix, for trailing padding.
    public PacketWriter WriteRaw(ReadOnlySpan<byte> data)
    {
        _ms.Write(data);
        return this;
    }

    public byte[] ToArray() => _ms.ToArray();
    public int Length => (int)_ms.Length;
}

/// PlayerStats as the game server reads it, derived from
/// PlayerStats::ReadFrom_MasterServer_GameServerSide (RVA 0x12A2A70) and its five
/// sub-readers. The server blocks in WaitingStatsFromMasterServer for 6000ms waiting
/// for this, so the layout has to match field for field.
///
/// Every variable-length block is self-describing, which is what makes a minimal reply
/// safe: PlayerProgess writes a param count and the reader seeks past exactly that many
/// uints, and each collection writes a byte count the reader uses as a read limit.
public static class PlayerStats
{
    /// Count of uint fields PlayerProgess declares after SteamID (KillCount through
    /// WeeklyChallengeCompleted). The reader seeks to start + count*4 when it is done,
    /// so a short count is honoured rather than desyncing the stream.
    public const uint ProgressParamCount = (uint)ProgressField.Count;

    public static void Write(
        PacketWriter w,
        ulong steamId,
        string username,
        ProgressRecord progress,
        bool isBanned = false,
        bool isTimedout = false)
    {
        // PlayerProgess: uint64 steamId, then a uint32 COUNT, then that many uint32s.
        // Confirmed in the game's own reader (RVA 0x2413A80):
        //
        //   call <ReadInt64>        -> steamId
        //   call <ReadUInt32>       -> prefix
        //   lea  eax,[rdx+rax*4]    -> end = position + prefix*4
        //   loop { call <ReadUInt32> } while position < end
        //
        // The multiply by four is what makes the prefix a count rather than a byte
        // length. A ushort byte length is the ACHIEVEMENTS format - a different member,
        // read through [this+0x20] - and briefly got applied here by mistake.
        w.Write(steamId);
        w.Write((uint)progress.Params.Length);
        foreach (var value in progress.Params)
            w.Write(value);

        w.WriteBlob(progress.ToolsProgess);    // body 188 - dictionary, 10 bytes/entry
        w.WriteBlob(progress.Achievements);    // body 190 - HashSet<uint>, 4 bytes/entry
        w.WriteBlob(progress.Selections);      // body 192 - raw Data byte[]
        w.WriteBlob(progress.Inventory);       // body 194 - HashSet<string>

        w.Write(false);         // body 196 - Clan.hasClan; false stops the clan reader
        w.Write(isBanned);      // body 197
        w.Write(isTimedout);    // body 198
        w.Write((ushort)0);     // body 199 - weekly challenge block length
        w.Write(progress.Role); // body 201

        // body 209 - MasterServerUsername, the name shown in game. The
        // NameReadPosition patch forces the read cursor to buffer 218 (209 + the
        // 9-byte header), so this offset and that constant have to stay in step.
        //
        // MEASURED, not inferred. The game's stream buffer holds the WHOLE packet
        // including the 9-byte header (uint64 sequence + byte type) that this master
        // strips before parsing, so the two sides disagree about where offset zero is.
        // A labelled-slot probe read at forced buffer position 218 and returned the
        // label sitting at body 209, proving the prefix is exactly 9.
        //
        // The NameReadPosition patch in patch_auth.ps1 forces the read cursor to buffer
        // 218 immediately before this string is read, so this offset is authoritative
        // and no longer depends on the chain of field widths ahead of it.
        w.Write(username);
    }

    /// Parse the same layout back out of a packet the game server sent us, which is how
    /// progress gets saved. PlayerStats::WriteTo_MasterServer_GameServerSide (RVA
    /// 0x12A2F50) writes the fields in exactly the order the reader consumes them, so
    /// one layout serves both directions.
    ///
    /// Returns null if the packet does not parse, rather than throwing into the
    /// connection loop: a malformed save is worth logging and dropping, not worth
    /// tearing down a working registry connection over.
    public static ProgressRecord? TryRead(PacketReader r, out ulong steamId)
    {
        steamId = 0;
        try
        {
            steamId = r.ReadUInt64();

            var count = r.ReadUInt32();
            if (count > 4096) return null;          // nonsense length, bail out

            var record = new ProgressRecord { Params = new uint[count] };
            for (uint i = 0; i < count; i++)
                record.Params[i] = r.ReadUInt32();

            record.ToolsProgess = r.ReadBlob();
            record.Achievements = r.ReadBlob();
            record.Selections = r.ReadBlob();
            record.Inventory = r.ReadBlob();

            if (r.ReadBool())                       // Clan.hasClan
            {
                r.ReadBool();                       // clan fields we do not keep
                r.Skip(1);
            }

            r.ReadBool();                           // isBanned, decided by the master
            r.ReadBool();                           // isTimedout
            if (r.ReadUInt16() != 0) return record;  // a challenge body we do not parse
            record.Role = r.ReadUInt64();
            return record;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// Bounds-checked counterpart to PacketWriter. Every read past the end throws, which
/// callers turn into "this packet did not parse" rather than letting it escape.
public sealed class PacketReader(ReadOnlyMemory<byte> data)
{
    readonly ReadOnlyMemory<byte> _data = data;
    int _pos;

    public int Remaining => _data.Length - _pos;

    ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Remaining < count)
            throw new EndOfStreamException($"needed {count} bytes, {Remaining} left");
        var span = _data.Span.Slice(_pos, count);
        _pos += count;
        return span;
    }

    public void Skip(int count) => Take(count);
    public bool ReadBool() => Take(1)[0] != 0;
    public byte ReadByte() => Take(1)[0];
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// A ushort-length-prefixed blob, the shape every variable-length PlayerStats
    /// section uses.
    public byte[] ReadBlob() => Take(ReadUInt16()).ToArray();

    public string ReadString() => Encoding.UTF8.GetString(Take(ReadUInt16()));
}
