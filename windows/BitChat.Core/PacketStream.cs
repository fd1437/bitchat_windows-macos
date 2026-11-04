using System.Buffers.Binary;
namespace BitChat.Core;

public sealed class PacketStream
{
    private readonly List<byte> buffer=[];private long started;
    public IEnumerable<byte[]> Add(byte[] bytes)
    {
        if(buffer.Count>0&&BitChat.Core.Bytes.Now-started>15000)buffer.Clear();if(buffer.Count==0)started=BitChat.Core.Bytes.Now;buffer.AddRange(bytes);
        if(buffer.Count>MeshPacket.MaxFileBytes+4096){buffer.Clear();throw new InvalidDataException("BLE stream limit");}
        var frames=new List<byte[]>();
        while(buffer.Count>=22)
        {
            byte v=buffer[0];if(v is not (1 or 2))
            {int pad=v;if(pad is not (1 or 2)&&pad>0&&pad<=buffer.Count&&buffer.Take(pad).All(x=>x==v))buffer.RemoveRange(0,pad);else buffer.RemoveAt(0);continue;}
            int header=v==2?16:14;if(buffer.Count<header+8)break;var flags=buffer[11];
            uint length=v==2?BinaryPrimitives.ReadUInt32BigEndian(buffer.Skip(12).Take(4).ToArray()):BinaryPrimitives.ReadUInt16BigEndian(buffer.Skip(12).Take(2).ToArray());
            long frame=header+8L+length+((flags&1)!=0?8:0)+((flags&2)!=0?64:0);
            if(v==2&&(flags&8)!=0){int routeAt=header+8+((flags&1)!=0?8:0);if(buffer.Count<=routeAt)break;frame+=1+buffer[routeAt]*8;}
            if(frame>MeshPacket.MaxFileBytes+4096){buffer.Clear();throw new InvalidDataException("Oversized BLE frame");}if(buffer.Count<frame)break;
            frames.Add(buffer.Take((int)frame).ToArray());buffer.RemoveRange(0,(int)frame);started=BitChat.Core.Bytes.Now;
            if(buffer.Count>0){int pad=buffer[0];if(pad is not (1 or 2)&&pad>0&&pad<=buffer.Count&&buffer.Take(pad).All(x=>x==pad))buffer.RemoveRange(0,pad);}
        }
        return frames;
    }
}
