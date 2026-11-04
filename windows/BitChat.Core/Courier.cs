using System.Security.Cryptography;
namespace BitChat.Core;
public sealed record Prekey(uint Id,byte[] PublicKey);
public sealed record PrekeyBundle(byte[] Owner,Prekey[] Prekeys,ulong Generated,byte[] Signature)
{
    public byte[] SigningBytes()=>Bytes.Join(Bytes.ShortString("bitchat-prekey-bundle-v1"),Owner,[(byte)Prekeys.Length],Bytes.Join(Prekeys.Select(p=>Bytes.Join(Bytes.U32(p.Id),p.PublicKey)).ToArray()),Bytes.U64(Generated));
    public byte[] Encode()=>Bytes.Join(Tlv16.Put(1,Owner),Tlv16.Put(2,Bytes.Join(Prekeys.Select(p=>Bytes.Join(Bytes.U32(p.Id),p.PublicKey)).ToArray())),Tlv16.Put(3,Bytes.U64(Generated)),Tlv16.Put(4,Signature));
    public bool Valid(byte[] signingKey)=>Owner.Length==32&&Prekeys.Length is >=1 and <=8&&Prekeys.All(p=>p.PublicKey.Length==32)&&Prekeys.Select(p=>p.Id).Distinct().Count()==Prekeys.Length&&Generated<=(ulong)(Bytes.Now+3600000)&&Generated>=(ulong)(Bytes.Now-7L*86400000)&&Crypto.VerifyEd(signingKey,SigningBytes(),Signature);
    public static PrekeyBundle Create(byte[] owner,Prekey[] keys,byte[] seed){var bundle=new PrekeyBundle(owner,keys,(ulong)Bytes.Now,[]);return bundle with{Signature=Crypto.SignEd(seed,bundle.SigningBytes())};}
    public static PrekeyBundle Decode(byte[] bytes)
    {var f=Tlv16.Parse(bytes);var r=new WireReader(f.Require(2));if(r.Remaining==0||r.Remaining%36!=0||r.Remaining>8*36)throw new InvalidDataException("Invalid prekey bundle");var keys=new List<Prekey>();while(r.Remaining>0)keys.Add(new(r.U32(),r.Read(32)));return new(f.Require(1,32),keys.ToArray(),new WireReader(f.Require(3,8)).U64(),f.Require(4,64));}
}
public sealed record CourierEnvelope(byte[] Tag,ulong Expires,byte[] Ciphertext,byte Copies=1,uint? PrekeyId=null)
{
    public string Id=>Bytes.Hex(Crypto.Hash(Ciphertext));
    public bool Valid=>ValidAt(Bytes.Now);
    public bool ValidAt(long now)=>Tag.Length==16&&Ciphertext.Length is >0 and <=16384&&Expires>(ulong)now&&Expires<=(ulong)(now+86400000)&&Copies is >=1 and <=8;
    public static byte[] RecipientTag(byte[] noisePublic,long? day=null)=>Crypto.Hmac(noisePublic,Bytes.Join(Bytes.Utf8("bitchat-courier-tag-v1"),Bytes.U32(checked((uint)(day??Bytes.Now/86400000)))))[..16];
    public bool For(byte[] publicKey)=>Enumerable.Range(-1,3).Any(offset=>RecipientTag(publicKey,Bytes.Now/86400000+offset).SequenceEqual(Tag));
    public byte[] Encode()
    {var b=Bytes.Join(Tlv16.Put(1,Tag),Tlv16.Put(2,Bytes.U64(Expires)),Tlv16.Put(3,Ciphertext));if(Copies>1)b=Bytes.Join(b,Tlv16.Put(4,[Copies]));if(PrekeyId!=null)b=Bytes.Join(b,Tlv16.Put(5,Bytes.U32(PrekeyId.Value)));return b;}
    public static CourierEnvelope Decode(byte[] bytes,long? now=null)
    {var f=Tlv16.Parse(bytes);var envelope=new CourierEnvelope(f.Require(1,16),new WireReader(f.Require(2,8)).U64(),f.Require(3),f.TryGetValue(4,out var copies)&&copies.Length==1?copies[0]:(byte)1,f.TryGetValue(5,out var id)?new WireReader(id).U32():null);if(!envelope.ValidAt(now??Bytes.Now))throw new InvalidDataException("Invalid or expired courier envelope");return envelope;}
    private static byte[] Prologue(uint? id)=>id==null?Bytes.Utf8("bitchat-courier-v1"):Bytes.Join(Bytes.Utf8("bitchat-prekey-v1"),Bytes.U32(id.Value));
    public static CourierEnvelope Seal(byte[] payload,byte[] senderSecret,byte[] recipientPublic,Prekey? prekey=null)
    {using var handshake=new NoiseHandshake(true,senderSecret,Prologue(prekey?.Id),oneWay:true,remoteStatic:prekey?.PublicKey??recipientPublic);return new(RecipientTag(recipientPublic),(ulong)(Bytes.Now+86400000),handshake.Write(payload),8,prekey?.Id);}
    public (byte[] Payload,byte[] SenderPublic) Open(byte[] recipientSecret)
    {using var handshake=new NoiseHandshake(false,recipientSecret,Prologue(PrekeyId),oneWay:true);var payload=handshake.Read(Ciphertext);return(payload,handshake.RemoteStatic!);}
}
