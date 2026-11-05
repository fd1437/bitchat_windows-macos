using System.Buffers.Binary;
namespace BitChat.Core;

public sealed record GossipFilter(int P,uint M,byte[] Data,int Included)
{
    public static byte[] PacketId(MeshPacket p)=>Crypto.Hash(Bytes.Join([p.Type],p.Sender,Bytes.U64(p.Timestamp),p.Payload))[..16];
    public static ulong Bucket(byte[] id,uint m)
    {if(m<=1)return 0;ulong hash=BinaryPrimitives.ReadUInt64BigEndian(Crypto.Hash(id))&0x7fffffffffffffff;ulong value=hash%m;return value==0?1:value;}
    public static GossipFilter Build(byte[][] ids,int maxBytes=1024,int p=10)
    {
        if(ids.Length==0)return new(p,1,[],0);int count=Math.Min(ids.Length,Math.Max(1,maxBytes*8/(p+2)));uint m=(uint)Math.Min(uint.MaxValue,(ulong)count<<p);
        byte[] Encode(int n)
        {
            var bits=new List<int>();ulong previous=0;foreach(var v in ids.Take(n).Select(id=>Bucket(id,m)).Distinct().Order())
            {ulong x=v-previous-1;previous=v;ulong q=x>>p;for(ulong i=0;i<q;i++)bits.Add(1);bits.Add(0);for(int i=p-1;i>=0;i--)bits.Add((int)((x>>i)&1));}
            var output=new byte[(bits.Count+7)/8];for(int i=0;i<bits.Count;i++)output[i/8]|=(byte)(bits[i]<<(7-i%8));return output;
        }
        var encoded=Encode(count);while(encoded.Length>maxBytes&&count>1){count=Math.Max(1,count*9/10);encoded=Encode(count);}return encoded.Length>maxBytes?new(p,m,[],0):new(p,m,encoded,count);
    }
    public HashSet<ulong> Values()
    {
        if(P is <1 or >32||M<=1||Data.Length>1024)return [];var values=new HashSet<ulong>();int offset=0;ulong acc=0;
        int? Bit(){if(offset>=Data.Length*8)return null;int v=(Data[offset/8]>>(7-offset%8))&1;offset++;return v;}
        while(offset<Data.Length*8)
        {
            ulong q=0;int? bit;while((bit=Bit())==1){if(++q>8192)return [];}
            if(bit==null)break;ulong remainder=0;bool complete=true;for(int i=0;i<P;i++){bit=Bit();if(bit==null){complete=false;break;}remainder=(remainder<<1)|(uint)bit.Value;}
            if(!complete)break;ulong delta=(q<<P)+remainder+1;if(delta>=M||acc>=M-delta)break;acc+=delta;values.Add(acc);
        }
        return values;
    }
    public byte[] Request(ulong? since=null)
    {var b=Bytes.Join(Tlv16.Put(1,[(byte)P]),Tlv16.Put(2,Bytes.U32(M)),Tlv16.Put(3,Data),Tlv16.Put(4,[0x03,0x07]));return since==null?b:Bytes.Join(b,Tlv16.Put(5,Bytes.U64(since.Value)));}
    public static (GossipFilter Filter,ulong Types,ulong? Since) Parse(byte[] bytes)
    {
        var f=Tlv16.Parse(bytes);int p=f.Require(1,1)[0];uint m=new WireReader(f.Require(2,4)).U32();var data=f.Require(3);if(p is <1 or >32||m==0||data.Length>1024)throw new InvalidDataException("Invalid gossip filter");
        ulong types=3;if(f.TryGetValue(4,out var t)){if(t.Length is <1 or >8)throw new InvalidDataException("Invalid sync type mask");types=0;for(int i=0;i<t.Length;i++)types|=(ulong)t[i]<<(8*i);}
        ulong? since=f.TryGetValue(5,out var time)?new WireReader(time).U64():null;return(new(p,m,data,0),types,since);
    }
    public static int? BitFor(byte type)=>type switch{1=>0,2=>1,3=>2,0x10=>3,0x11=>4,0x20=>5,0x21=>6,0x22=>7,0x23=>8,0x24=>9,0x25=>10,_=>null};
}
