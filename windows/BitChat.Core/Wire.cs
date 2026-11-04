using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BitChat.Core;

public static class Bytes
{
    public static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    public static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    public static string Text(byte[] value) => new UTF8Encoding(false, true).GetString(value);
    public static byte[] U64(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); return b; }
    public static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    public static byte[] U16(ushort value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); return b; }
    public static byte[] ShortString(string value) { var b = Utf8(value); if (b.Length > 255) throw new ArgumentException("Text exceeds 255 bytes"); return Join([(byte)b.Length], b); }
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class WireReader(byte[] bytes)
{
    private int offset;
    public int Remaining => bytes.Length - offset;
    public byte[] Read(int count) { if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated packet"); var b=bytes.AsSpan(offset,count).ToArray(); offset+=count; return b; }
    public byte Byte() => Read(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Read(2));
    public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Read(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Read(8));
    public string ShortString() => Bytes.Text(Read(Byte()));
}

public sealed record MeshPacket(byte Type, byte[] Sender, byte[] Payload, byte[]? Recipient=null,
    byte[]? Signature=null, byte Ttl=7, byte Version=1, ulong Timestamp=0, byte[][]? Route=null, bool IsRsr=false)
{
    public const int MaxFileBytes = 1024*1024 + 18 + 65535*2 + 16 + 8 + 8 + 64;
    public byte[] Encode(bool pad=false)
    {
        if (Version is not (1 or 2) || Sender.Length!=8 || Recipient is {Length: not 8} || Signature is {Length: not 64}) throw new InvalidDataException("Invalid packet fields");
        if (Payload.Length > MaxFileBytes || (Version==1 && Payload.Length>65535)) throw new InvalidDataException("Packet too large");
        bool route=Version==2 && Route is {Length:>0};
        if (route && (Route!.Length>255 || Route.Any(x=>x.Length!=8))) throw new InvalidDataException("Invalid route");
        var payload=Payload; bool compressed=false; int unique=Payload.Distinct().Count();
        if(Payload.Length>=100 && (Version==2||Payload.Length<=65535) && unique/(double)Math.Min(Payload.Length,256)<0.9)
        { var c=CanonicalDeflate.Encode(Payload);if(c.Length<Payload.Length){payload=Bytes.Join(Version==2?Bytes.U32((uint)Payload.Length):Bytes.U16(checked((ushort)Payload.Length)),c);compressed=true;} }
        byte flags=(byte)((Recipient!=null?1:0)|(Signature!=null?2:0)|(route?8:0)|(IsRsr?16:0)|(compressed?4:0));
        var parts = new List<byte[]> { new byte[]{Version,Type,Ttl}, Bytes.U64(Timestamp), new byte[]{flags}, Version==2?Bytes.U32((uint)payload.Length):Bytes.U16(checked((ushort)payload.Length)), Sender };
        if(Recipient!=null) parts.Add(Recipient);
        if(route) { parts.Add([(byte)Route!.Length]); parts.AddRange(Route); }
        parts.Add(payload); if(Signature!=null) parts.Add(Signature);
        var result=Bytes.Join(parts.ToArray());
        return pad ? Padding.Pad(result) : result;
    }
    public static MeshPacket Decode(byte[] data)
    {
        var r=new WireReader(data); var v=r.Byte(); if(v is not (1 or 2)) throw new InvalidDataException("Unsupported packet version");
        var t=r.Byte(); var ttl=r.Byte(); var time=r.U64(); var f=r.Byte();
        uint n=v==2?r.U32():r.U16(); if(n>MaxFileBytes) throw new InvalidDataException("Oversized packet");
        var sender=r.Read(8); var recipient=(f&1)!=0?r.Read(8):null;
        byte[][]? route=null; if(v==2 && (f&8)!=0) { route=new byte[r.Byte()][]; for(int i=0;i<route.Length;i++) route[i]=r.Read(8); }
        byte[] payload;
        int cap=t switch {0x22 or 0x11=>MaxFileBytes,0x04 or 0x28=>65536,0x20=>1280,0x21=>8192,1 or 3 or 0x10 or 0x23 or 0x24 or 0x26 or 0x27 or 0x29=>4096,_=>128*1024};
        if((f&4)!=0)
        {
            uint original=v==2?r.U32():r.U16(); int preamble=v==2?4:2;
            if(n<=preamble || original>cap || original>(n-preamble)*1032L) throw new InvalidDataException("Invalid compression size");
            var compressed=r.Read(checked((int)n-preamble));
            using var z=new DeflateStream(new MemoryStream(compressed),CompressionMode.Decompress);
            payload=new byte[original]; z.ReadExactly(payload); if(z.ReadByte()!=-1) throw new InvalidDataException("Decompression limit exceeded");
        }
        else { if(n>cap) throw new InvalidDataException("Payload exceeds limit"); payload=r.Read((int)n); }
        var signature=(f&2)!=0?r.Read(64):null;
        return new(t,sender,payload,recipient,signature,ttl,v,time,route,(f&16)!=0);
    }
    // Swift BitchatPacket.toBinaryDataForSigning normalizes mutable relay TTL.
    public byte[] SigningBytes() => (this with {Signature=null,Ttl=0,IsRsr=false}).Encode(true);
}

public static class Padding
{
    public static byte[] Pad(byte[] data)
    {
        int target=new[]{256,512,1024,2048}.FirstOrDefault(x=>x>=data.Length+16);
        if(target==0) return data;
        int n=target-data.Length; if(n>255) return data;
        return Bytes.Join(data,Enumerable.Repeat((byte)n,n).ToArray());
    }
}

public sealed record ChatMessage(string Id,string Sender,string Content,long Timestamp,bool IsPrivate=false,string? PeerId=null,string? Recipient=null,bool IsRelay=false,string? OriginalSender=null,string[]? Mentions=null)
{
    public static ChatMessage Create(string sender,string text,bool isPrivate=false,string? peer=null,string? recipient=null) => new(Guid.NewGuid().ToString().ToUpperInvariant(),sender,text,Bytes.Now,isPrivate,peer,recipient);
    public byte[] Encode()
    {
        byte f=(byte)((IsRelay?1:0)|(IsPrivate?2:0)|(OriginalSender!=null?4:0)|(Recipient!=null?8:0)|(PeerId!=null?16:0)|(Mentions is {Length:>0}?32:0));
        var text=Bytes.Utf8(Content); if(text.Length>65535) throw new InvalidDataException("Message too long");
        var p=new List<byte[]>{new byte[]{f},Bytes.U64((ulong)Timestamp),Bytes.ShortString(Id),Bytes.ShortString(Sender),Bytes.U16((ushort)text.Length),text};
        if(OriginalSender!=null)p.Add(Bytes.ShortString(OriginalSender)); if(Recipient!=null)p.Add(Bytes.ShortString(Recipient)); if(PeerId!=null)p.Add(Bytes.ShortString(PeerId));
        if(Mentions is {Length:>0}) {p.Add([(byte)Mentions.Length]);p.AddRange(Mentions.Select(Bytes.ShortString));}
        return Bytes.Join(p.ToArray());
    }
    public static ChatMessage Decode(byte[] bytes)
    {
        var r=new WireReader(bytes);var f=r.Byte();long t=checked((long)r.U64());var id=r.ShortString();var sender=r.ShortString();var content=Bytes.Text(r.Read(r.U16()));
        string? original=(f&4)!=0?r.ShortString():null;string? recipient=(f&8)!=0?r.ShortString():null;string? peer=(f&16)!=0?r.ShortString():null;
        string[]? mentions=null;if((f&32)!=0){mentions=new string[r.Byte()];for(int i=0;i<mentions.Length;i++)mentions[i]=r.ShortString();}
        return new(id,sender,content,t,(f&2)!=0,peer,recipient,(f&1)!=0,original,mentions);
    }
}

public sealed record Announcement(string Nickname,byte[] NoiseKey,byte[] SigningKey,byte[]? Capabilities=null,string? BridgeCell=null,byte[][]? Neighbors=null)
{
    public byte[] Encode()
    {
        var fields=new List<byte[]>{Tlv(1,Bytes.Utf8(Nickname)),Tlv(2,NoiseKey),Tlv(3,SigningKey)};
        if(Neighbors is {Length:>0})fields.Add(Tlv(4,Bytes.Join(Neighbors.Take(10).ToArray())));
        if(Capabilities!=null) fields.Add(Tlv(5,Capabilities)); if(BridgeCell!=null) fields.Add(Tlv(6,Bytes.Utf8(BridgeCell)));
        return Bytes.Join(fields.ToArray());
    }
    public static byte[] Tlv(byte type,byte[] value) {if(value.Length>255)throw new InvalidDataException();return Bytes.Join([type,(byte)value.Length],value);}
    public static Announcement Decode(byte[] data)
    {
        var fields=new Dictionary<byte,byte[]>();var r=new WireReader(data);
        while(r.Remaining>0){var t=r.Byte();if(!fields.TryAdd(t,r.Read(r.Byte())))throw new InvalidDataException("Duplicate TLV");}
        if(!fields.TryGetValue(1,out var name)||!fields.TryGetValue(2,out var noise)||!fields.TryGetValue(3,out var signing)||noise.Length!=32||signing.Length!=32)throw new InvalidDataException("Invalid announcement");
        return new(Bytes.Text(name),noise,signing,fields.GetValueOrDefault((byte)5),fields.TryGetValue(6,out var cell)?Bytes.Text(cell):null,fields.TryGetValue(4,out var neighbors)&&neighbors.Length%8==0?Enumerable.Range(0,neighbors.Length/8).Select(i=>neighbors.AsSpan(i*8,8).ToArray()).ToArray():null);
    }
}
