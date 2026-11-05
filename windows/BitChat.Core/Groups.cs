using System.Security.Cryptography;

namespace BitChat.Core;

public static class Tlv16
{
    public static byte[] Put(byte type,byte[] value)=>Bytes.Join([type],Bytes.U16(checked((ushort)value.Length)),value);
    public static Dictionary<byte,byte[]> Parse(byte[] bytes)
    {
        var r=new WireReader(bytes);var fields=new Dictionary<byte,byte[]>();while(r.Remaining>0){var t=r.Byte();var b=r.Read(r.U16());if(!fields.TryAdd(t,b))throw new InvalidDataException("Duplicate TLV field");}return fields;
    }
    public static byte[] Require(this Dictionary<byte,byte[]> f,byte t,int? n=null)
    {if(!f.TryGetValue(t,out var b)||(n!=null&&b.Length!=n))throw new InvalidDataException("Missing or invalid TLV field");return b;}
}
public sealed record GroupMember(string Fingerprint,byte[] SigningKey,string Nickname);
public sealed record PrivateGroup(byte[] Id,string Name,byte[] Key,uint Epoch,GroupMember[] Members,string Creator,byte[] Signature)
{
    public string Conversation=>"group:"+Bytes.Hex(Id);
    public byte[] Roster()
    {
        if(Members.Length is <1 or >16)throw new InvalidDataException("A group must have 1–16 members");
        if(Members.Select(m=>m.Fingerprint).Distinct().Count()!=Members.Length)throw new InvalidDataException("Duplicate group member");
        var parts=new List<byte[]>{new byte[]{(byte)Members.Length}};
        foreach(var member in Members)
        {var fp=Convert.FromHexString(member.Fingerprint);if(fp.Length!=32||member.SigningKey.Length!=32||Bytes.Utf8(member.Nickname).Length>64)throw new InvalidDataException("Invalid group member");parts.Add(Bytes.Join(fp,member.SigningKey,Bytes.ShortString(member.Nickname)));}
        return Bytes.Join(parts.ToArray());
    }
    public byte[] SigningBytes()=>Bytes.Join(Bytes.Utf8("bitchat-group-v1"),Id,Bytes.U32(Epoch),Crypto.Hash(Key),Crypto.Hash(Roster()),Crypto.Hash(Bytes.Utf8(Name)));
    public PrivateGroup Sign(byte[] signingSeed)=>this with{Signature=Crypto.SignEd(signingSeed,SigningBytes())};
    public bool Valid()
    {
        try{var creator=Members.SingleOrDefault(m=>m.Fingerprint==Creator);return Id.Length==16&&Key.Length==32&&creator!=null&&Crypto.VerifyEd(creator.SigningKey,SigningBytes(),Signature);}catch{return false;}
    }
    public byte[] Encode()=>Bytes.Join(Tlv16.Put(1,Id),Tlv16.Put(2,Bytes.Utf8(Name)),Tlv16.Put(3,Key),Tlv16.Put(4,Bytes.U32(Epoch)),Tlv16.Put(5,Roster()),Tlv16.Put(6,Convert.FromHexString(Creator)),Tlv16.Put(7,Signature));
    public static PrivateGroup Decode(byte[] bytes)
    {
        var f=Tlv16.Parse(bytes);var r=new WireReader(f.Require(5));int count=r.Byte();if(count is <1 or >16)throw new InvalidDataException("Invalid group roster");
        var members=new GroupMember[count];for(int i=0;i<count;i++)members[i]=new(Bytes.Hex(r.Read(32)),r.Read(32),r.ShortString());if(r.Remaining>0)throw new InvalidDataException("Trailing group roster data");
        var group=new PrivateGroup(f.Require(1,16),Bytes.Text(f.Require(2)),f.Require(3,32),new WireReader(f.Require(4,4)).U32(),members,Bytes.Hex(f.Require(6,32)),f.Require(7,64));if(!group.Valid())throw new CryptographicException("Invalid group creator signature");return group;
    }
    public byte[] Seal(string text,string messageId,string nickname,byte[] signingSeed,ulong timestamp)
    {
        if(!Members.Any(m=>m.SigningKey.SequenceEqual(Crypto.PublicEd(signingSeed))))throw new CryptographicException("Sender is not a group member");
        var content=SigningContent(Id,Epoch,messageId,timestamp,text);var signature=Crypto.SignEd(signingSeed,content);
        var inner=Bytes.Join(Tlv16.Put(1,Bytes.Utf8(messageId)),Tlv16.Put(2,Crypto.PublicEd(signingSeed)),Tlv16.Put(3,Bytes.Utf8(nickname)),Tlv16.Put(4,Bytes.U64(timestamp)),Tlv16.Put(5,Bytes.Utf8(text)),Tlv16.Put(6,signature));
        var nonce=RandomNumberGenerator.GetBytes(12);var ciphertext=Crypto.AeadNonce(Key,nonce,inner,Bytes.Join(Id,Bytes.U32(Epoch)));
        return Bytes.Join(Tlv16.Put(1,Id),Tlv16.Put(2,Bytes.U32(Epoch)),Tlv16.Put(3,nonce),Tlv16.Put(4,ciphertext));
    }
    public ChatMessage Open(byte[] encoded)
    {
        var e=Tlv16.Parse(encoded);if(!e.Require(1,16).SequenceEqual(Id)||new WireReader(e.Require(2,4)).U32()!=Epoch)throw new CryptographicException("Group epoch mismatch");
        var plaintext=Crypto.AeadNonce(Key,e.Require(3,12),e.Require(4),Bytes.Join(Id,Bytes.U32(Epoch)),true);var f=Tlv16.Parse(plaintext);
        string id=Bytes.Text(f.Require(1));var signingKey=f.Require(2,32);string name=Bytes.Text(f.Require(3));ulong time=new WireReader(f.Require(4,8)).U64();string text=Bytes.Text(f.Require(5));
        var member=Members.SingleOrDefault(m=>m.SigningKey.SequenceEqual(signingKey));if(member==null||!Crypto.VerifyEd(signingKey,SigningContent(Id,Epoch,id,time,text),f.Require(6,64)))throw new CryptographicException("Invalid group author");
        return new(id,name,text,checked((long)time),true,member.Fingerprint[..16]);
    }
    private static byte[] SigningContent(byte[] id,uint epoch,string messageId,ulong time,string text)=>Bytes.Join(Bytes.Utf8("bitchat-group-msg-v1"),id,Bytes.U32(epoch),Bytes.Utf8(messageId),Bytes.U64(time),Bytes.Utf8(text));
}
