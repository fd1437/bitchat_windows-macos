using System.Security.Cryptography;
namespace BitChat.Core;

public sealed record BoardPost(byte[] Id,string Cell,string Content,byte[] AuthorKey,string Nickname,ulong Created,ulong Expires,byte Flags,byte[] Signature)
{
    public string Key=>Bytes.Hex(AuthorKey)+":"+Bytes.Hex(Id);
    public byte[] SigningBytes()
    {byte[] Text(string s){var b=Bytes.Utf8(s);return Bytes.Join(Bytes.U16(checked((ushort)b.Length)),b);}return Bytes.Join(Bytes.ShortString("bitchat-board-v1"),Id,Text(Cell),Text(Content),AuthorKey,Text(Nickname),Bytes.U64(Created),Bytes.U64(Expires),[Flags]);}
    public bool Valid(long? now=null)
    {
        long time=now??Bytes.Now;
        return Id.Length==16&&AuthorKey.Length==32&&Bytes.Utf8(Content).Length is >=1 and <=512&&Bytes.Utf8(Nickname).Length<=64&&(Cell.Length==0||Geohash.Valid(Cell))&&Expires>Created&&Expires-Created<=7UL*24*60*60*1000&&Created<=(ulong)(time+3600000)&&Expires>(ulong)time&&(Flags&~1)==0&&Crypto.VerifyEd(AuthorKey,SigningBytes(),Signature);
    }
    public byte[] Encode()=>Bytes.Join(Tlv16.Put(1,[1]),Tlv16.Put(2,Id),Tlv16.Put(3,Bytes.Utf8(Cell)),Tlv16.Put(4,Bytes.Utf8(Content)),Tlv16.Put(5,AuthorKey),Tlv16.Put(6,Bytes.Utf8(Nickname)),Tlv16.Put(7,Bytes.U64(Created)),Tlv16.Put(8,Bytes.U64(Expires)),Tlv16.Put(9,[Flags]),Tlv16.Put(10,Signature));
    public static BoardPost Create(string text,string cell,string nickname,byte[] seed,bool urgent=false)
    {ulong now=(ulong)Bytes.Now;var p=new BoardPost(RandomNumberGenerator.GetBytes(16),cell,text,Crypto.PublicEd(seed),nickname,now,now+86400000,(byte)(urgent?1:0),[]);p=p with{Signature=Crypto.SignEd(seed,p.SigningBytes())};if(!p.Valid())throw new ArgumentException("Invalid board note: max 512 bytes, nickname 64 bytes, geohash 12 characters");return p;}
    public static BoardPost Decode(byte[] bytes,long? now=null)
    {
        var f=Tlv16.Parse(bytes);if(f.Require(1,1)[0]!=1)throw new InvalidDataException("Not a board post");
        var p=new BoardPost(f.Require(2,16),Bytes.Text(f.Require(3)),Bytes.Text(f.Require(4)),f.Require(5,32),Bytes.Text(f.Require(6)),new WireReader(f.Require(7,8)).U64(),new WireReader(f.Require(8,8)).U64(),f.Require(9,1)[0],f.Require(10,64));if(!p.Valid(now))throw new CryptographicException("Invalid or expired board post");return p;
    }
}
public sealed record BoardDeletion(byte[] Id,byte[] AuthorKey,ulong Deleted,byte[] Signature)
{
    public string Key=>Bytes.Hex(AuthorKey)+":"+Bytes.Hex(Id);
    public byte[] SigningBytes()=>Bytes.Join(Bytes.ShortString("bitchat-board-del-v1"),Id,Bytes.U64(Deleted));
    public byte[] Encode()=>Bytes.Join(Tlv16.Put(1,[2]),Tlv16.Put(2,Id),Tlv16.Put(5,AuthorKey),Tlv16.Put(11,Bytes.U64(Deleted)),Tlv16.Put(10,Signature));
    public static BoardDeletion Create(BoardPost post,byte[] seed)
    {if(!Crypto.PublicEd(seed).SequenceEqual(post.AuthorKey))throw new CryptographicException("Only the author can delete this post");var p=new BoardDeletion(post.Id,post.AuthorKey,(ulong)Bytes.Now,[]);return p with{Signature=Crypto.SignEd(seed,p.SigningBytes())};}
    public static BoardDeletion Decode(byte[] bytes)
    {
        var f=Tlv16.Parse(bytes);if(f.Require(1,1)[0]!=2)throw new InvalidDataException("Not a board deletion");var p=new BoardDeletion(f.Require(2,16),f.Require(5,32),new WireReader(f.Require(11,8)).U64(),f.Require(10,64));
        if(p.Deleted>(ulong)(Bytes.Now+3600000)||!Crypto.VerifyEd(p.AuthorKey,p.SigningBytes(),p.Signature))throw new CryptographicException("Invalid board deletion");return p;
    }
}
