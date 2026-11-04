namespace BitChat.Core;

public sealed record MediaPacket(string Name,string Mime,byte[] Content)
{
    public const int MaxBytes=1024*1024;
    public byte[] Encode()
    {
        if(Content.Length>MaxBytes)throw new InvalidDataException("Maximum attachment size is 1 MiB");
        byte[] Tlv(byte t,byte[] b)=>Bytes.Join([t],Bytes.U16(checked((ushort)b.Length)),b);
        return Bytes.Join(Tlv(1,Bytes.Utf8(Name)),Tlv(2,Bytes.U32((uint)Content.Length)),Tlv(3,Bytes.Utf8(Mime)),[4],Bytes.U32((uint)Content.Length),Content);
    }
    public static MediaPacket Decode(byte[] bytes)
    {
        var r=new WireReader(bytes);string name="attachment",mime="application/octet-stream";byte[]? content=null;ulong? declared=null;
        while(r.Remaining>0)
        {
            byte t=r.Byte();uint n=t==4?r.U32():r.U16();if(n>MaxBytes)throw new InvalidDataException("Oversized media");var b=r.Read((int)n);
            switch(t){case 1:name=Bytes.Text(b);break;case 2:var size=new WireReader(b);declared=b.Length==4?size.U32():b.Length==8?size.U64():throw new InvalidDataException("Invalid file size");break;case 3:mime=Bytes.Text(b);break;case 4:content=b;break;}
        }
        if(content==null||content.Length>MaxBytes||(declared!=null&&declared!=(ulong)content.Length))throw new InvalidDataException("Attachment length mismatch");return new(name,mime,content);
    }
}

public sealed class FragmentAssembler
{
    private readonly Func<long> clock;
    public FragmentAssembler(Func<long>? clock=null){this.clock=clock??(()=>Bytes.Now);}
    private sealed class Assembly(int total,byte type,long started){public int Total=total;public byte Type=type;public long Started=started;public long LastProgress=started;public Dictionary<int,byte[]> Chunks=[];public int Size;public string? Ingress;public Guid? Epoch;public bool Direct;}
    private readonly Dictionary<string,Assembly> assemblies=[];
    public MeshPacket? Add(MeshPacket fragment)=>Add(fragment,null,null,out _);
    public MeshPacket? Add(MeshPacket fragment,string? ingress,Guid? epoch,out bool direct)
    {
        direct=false;
        foreach(var old in assemblies.Where(x=>clock()-x.Value.LastProgress>60000||clock()-x.Value.Started>600000).Select(x=>x.Key).ToArray())assemblies.Remove(old);
        var r=new WireReader(fragment.Payload);string id=Bytes.Hex(fragment.Sender)+Bytes.Hex(r.Read(8));int index=r.U16(),total=r.U16();byte type=r.Byte();
        if(total==0||total>8192||index>=total||r.Remaining>1280)throw new InvalidDataException("Invalid fragment");
        if(!assemblies.TryGetValue(id,out var a)){if(assemblies.Count>=64)throw new InvalidDataException("Too many fragment assemblies");assemblies[id]=a=new(total,type,clock()){Ingress=ingress,Epoch=epoch,Direct=ingress!=null&&epoch!=null&&fragment.Ttl==7};}
        a.Direct&=a.Ingress==ingress&&a.Epoch==epoch&&fragment.Ttl==7;
        if(a.Total!=total||a.Type!=type){assemblies.Remove(id);throw new InvalidDataException("Conflicting fragment metadata");}
        var chunk=r.Read(r.Remaining);if(a.Chunks.TryGetValue(index,out var oldChunk)){if(!oldChunk.SequenceEqual(chunk))throw new InvalidDataException("Conflicting fragment");return null;}
        if(a.Size+chunk.Length>MeshPacket.MaxFileBytes||assemblies.Values.Sum(x=>x.Size)+chunk.Length>16*1024*1024){assemblies.Remove(id);throw new InvalidDataException("Fragment memory limit");}
        a.Chunks[index]=chunk;a.Size+=chunk.Length;a.LastProgress=clock();
        if(a.Chunks.Count!=total)return null;
        assemblies.Remove(id);var packet=MeshPacket.Decode(Bytes.Join(Enumerable.Range(0,total).Select(i=>a.Chunks[i]).ToArray()));
        if(packet.Type!=type||!packet.Sender.SequenceEqual(fragment.Sender))throw new InvalidDataException("Reassembled packet identity mismatch");direct=a.Direct;return packet;
    }
    public static IEnumerable<MeshPacket> Split(MeshPacket packet,int maxFrame=512)
    {
        var bytes=packet.Encode(packet.Type is 0x10 or 0x11);if(bytes.Length<=maxFrame){yield return packet;yield break;}
        int chunkSize=maxFrame-(packet.Recipient==null?35:43);if(chunkSize<20)throw new ArgumentException("MTU too small");
        int total=(bytes.Length+chunkSize-1)/chunkSize;if(total>65535)throw new InvalidDataException("Too many fragments");
        var id=System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        for(int i=0;i<total;i++){var payload=Bytes.Join(id,Bytes.U16((ushort)i),Bytes.U16((ushort)total),[packet.Type],bytes.Skip(i*chunkSize).Take(chunkSize).ToArray());yield return new(0x20,packet.Sender,payload,packet.Recipient,Ttl:packet.Ttl,Timestamp:packet.Timestamp);}
    }
}
