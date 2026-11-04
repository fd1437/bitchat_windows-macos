namespace BitChat.Core;
public sealed record VoicePacket(byte[] Burst,ushort Sequence,byte Flags,byte[] Payload)
{
    public byte[] Encode()=>Bytes.Join(Burst,Bytes.U16(Sequence),[Flags],Payload);
    public static VoicePacket Decode(byte[] data)
    {
        var r=new WireReader(data);var id=r.Read(8);ushort seq=r.U16();byte flags=r.Byte();var body=r.Read(r.Remaining);
        if(flags==1&&(body.Length!=1||body[0]!=1)||flags==2&&body.Length<6||flags==4&&body.Length!=0||flags is not(0 or 1 or 2 or 4))throw new InvalidDataException("Invalid voice frame");
        var packet=new VoicePacket(id,seq,flags,body);if(flags==0)_=packet.Frames();return packet;
    }
    public byte[][] Frames()
    {
        if(Flags!=0)return [];var r=new WireReader(Payload);var frames=new List<byte[]>();while(r.Remaining>0){int n=r.U16();if(n==0||n>8192||frames.Count>=8)throw new InvalidDataException("Invalid AAC frame");frames.Add(r.Read(n));}if(frames.Count==0)throw new InvalidDataException("Empty audio packet");return frames.ToArray();
    }
    public static byte[] Adts(byte[] raw)
    {int n=raw.Length+7;if(n>8191)throw new InvalidDataException("AAC frame too large");return Bytes.Join([0xff,0xf1,0x60,(byte)(0x40|((n>>11)&3)),(byte)(n>>3),(byte)(((n&7)<<5)|0x1f),0xfc],raw);}
}
