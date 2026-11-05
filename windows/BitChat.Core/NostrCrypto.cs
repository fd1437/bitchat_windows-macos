using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace BitChat.Core;

/// Nostr BIP-340 and raw-point ECDH through NBitcoin's libsecp256k1 port.
public static class Schnorr
{
    public static byte[] GenerateKey()
    {
        while(true){var bytes=RandomNumberGenerator.GetBytes(32);if(NBitcoin.Secp256k1.ECPrivKey.TryCreate(bytes,out var key)){key.Dispose();return bytes;}CryptographicOperations.ZeroMemory(bytes);}
    }
    public static byte[] Public(byte[] sk){using var key=NBitcoin.Secp256k1.ECPrivKey.Create(sk);return key.CreateXOnlyPubKey().ToBytes();}
    public static byte[] Sign(byte[] sk,byte[] message,byte[]? aux=null)
    {using var key=NBitcoin.Secp256k1.ECPrivKey.Create(sk);return key.SignBIP340(message,aux??RandomNumberGenerator.GetBytes(32)).ToBytes();}
    public static bool Verify(byte[] pk,byte[] message,byte[] signature)
    {return message.Length==32&&NBitcoin.Secp256k1.ECXOnlyPubKey.TryCreate(pk,out var key)&&NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(signature,out var sig)&&key.SigVerifyBIP340(sig,message);}
    // Swift uses the serialized compressed shared point as HKDF input.
    public static byte[] Ecdh(byte[] sk,byte[] remote,bool odd=false)
    {
        using var key=NBitcoin.Secp256k1.ECPrivKey.Create(sk);var point=NBitcoin.Secp256k1.ECPubKey.Create(remote.Length==32?Bytes.Join([(byte)(odd?3:2)],remote):remote);return point.GetSharedPubkey(key).ToBytes();
    }
}

public sealed record NostrEvent(
    string id,string pubkey,long created_at,int kind,string[][] tags,string content,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? sig=null)
{
    public static readonly JsonSerializerOptions Json=new(){Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public byte[] Hash()=>Crypto.Hash(JsonSerializer.SerializeToUtf8Bytes(new object[]{0,pubkey,created_at,kind,tags,content},Json));
    public NostrEvent Sign(byte[] key){var hash=Hash();return this with{id=Bytes.Hex(hash),sig=Bytes.Hex(Schnorr.Sign(key,hash))};}
    public bool Valid()
    {
        try{return tags.Length<=1024&&tags.All(t=>t.Length<=16&&t.All(x=>Bytes.Utf8(x).Length<=4096))&&content.Length<=65536&&id==Bytes.Hex(Hash())&&sig!=null&&Schnorr.Verify(Convert.FromHexString(pubkey),Hash(),Convert.FromHexString(sig));}catch{return false;}
    }
    public string Serialize()=>JsonSerializer.Serialize(this,Json);
    public static NostrEvent Parse(string json){if(Bytes.Utf8(json).Length>128*1024)throw new InvalidDataException("Event too large");return JsonSerializer.Deserialize<NostrEvent>(json,Json)??throw new InvalidDataException("Invalid event");}
    public static NostrEvent Create(byte[] key,int kind,string content,string[][] tags,long? timestamp=null,bool sign=true)
    {var e=new NostrEvent("",Bytes.Hex(Schnorr.Public(key)),timestamp??DateTimeOffset.UtcNow.ToUnixTimeSeconds(),kind,tags,content);return sign?e.Sign(key):e;}
}

public static class PrivateEnvelope
{
    private static uint Rotate(uint v,int n)=>(v<<n)|(v>>(32-n));
    private static void Quarter(uint[] x,int a,int b,int c,int d)
    {unchecked{x[a]+=x[b];x[d]=Rotate(x[d]^x[a],16);x[c]+=x[d];x[b]=Rotate(x[b]^x[c],12);x[a]+=x[b];x[d]=Rotate(x[d]^x[a],8);x[c]+=x[d];x[b]=Rotate(x[b]^x[c],7);}}
    private static (byte[] Key,byte[] Nonce) XKey(byte[] key,byte[] nonce)
    {
        var input=Bytes.Join(Bytes.Utf8("expand 32-byte k"),key,nonce[..16]);var state=new uint[16];for(int i=0;i<16;i++)state[i]=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(i*4));
        for(int i=0;i<10;i++){Quarter(state,0,4,8,12);Quarter(state,1,5,9,13);Quarter(state,2,6,10,14);Quarter(state,3,7,11,15);Quarter(state,0,5,10,15);Quarter(state,1,6,11,12);Quarter(state,2,7,8,13);Quarter(state,3,4,9,14);}
        var sub=new byte[32];var indices=new[]{0,1,2,3,12,13,14,15};for(int i=0;i<8;i++)System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sub.AsSpan(i*4),state[indices[i]]);
        return(sub,Bytes.Join(new byte[4],nonce[16..]));
    }
    private static byte[] Key(byte[] sk,byte[] remote,bool odd=false)=>HKDF.DeriveKey(HashAlgorithmName.SHA256,Schnorr.Ecdh(sk,remote,odd),32,[],Bytes.Utf8("nip44-v2"));
    public static string Encrypt(string text,byte[] sk,string recipient)
    {
        var nonce=RandomNumberGenerator.GetBytes(24);var (key,iv)=XKey(Key(sk,Convert.FromHexString(recipient)),nonce);var p=Bytes.Utf8(text);var c=Crypto.AeadNonce(key,iv,p,[]);CryptographicOperations.ZeroMemory(key);
        return "v2:"+Convert.ToBase64String(Bytes.Join(nonce,c)).TrimEnd('=').Replace('+','-').Replace('/','_');
    }
    public static string Decrypt(string encoded,byte[] sk,string sender)
    {
        if(!encoded.StartsWith("v2:")||encoded.Length>65536)throw new CryptographicException("Invalid private envelope");
        var base64=encoded[3..].Replace('-','+').Replace('_','/');base64=base64.PadRight((base64.Length+3)/4*4,'=');var b=Convert.FromBase64String(base64);if(b.Length<=40)throw new CryptographicException("Truncated private envelope");
        for(int parity=0;parity<2;parity++)
        {
            var (key,iv)=XKey(Key(sk,Convert.FromHexString(sender),parity==1),b[..24]);
            try{var p=Crypto.AeadNonce(key,iv,b[24..],[],true);return Bytes.Text(p);}
            catch(CryptographicException)when(parity==0){}finally{CryptographicOperations.ZeroMemory(key);}
        }
        throw new CryptographicException("Cannot decrypt envelope");
    }
    public static NostrEvent Wrap(string text,byte[] key,string recipient)
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();long RandomTime()=>now+RandomNumberGenerator.GetInt32(-900,901);
        var rumor=NostrEvent.Create(key,14,text,[],now,false);
        var seal=NostrEvent.Create(key,13,Encrypt(rumor.Serialize(),key,recipient),[],RandomTime());
        var ephemeral=Schnorr.GenerateKey();try{return NostrEvent.Create(ephemeral,1059,Encrypt(seal.Serialize(),ephemeral,recipient),[["p",recipient]],RandomTime());}finally{CryptographicOperations.ZeroMemory(ephemeral);}
    }
    public static NostrEvent Open(NostrEvent wrap,byte[] key)
    {
        var pk=Bytes.Hex(Schnorr.Public(key));
        bool Recipient(string[][] tags)=>tags.Length==1&&tags[0].SequenceEqual(new[]{"p",pk});
        if(wrap.kind!=1059||!Recipient(wrap.tags)||!wrap.Valid())throw new CryptographicException("Invalid outer envelope");
        var seal=NostrEvent.Parse(Decrypt(wrap.content,key,wrap.pubkey));if(seal.kind!=13||seal.tags.Length!=0||!seal.Valid())throw new CryptographicException("Invalid sender seal");
        var rumor=NostrEvent.Parse(Decrypt(seal.content,key,seal.pubkey));if(rumor.kind!=14||rumor.sig!=null||rumor.pubkey!=seal.pubkey||(rumor.tags.Length!=0&&!Recipient(rumor.tags)))throw new CryptographicException("Unauthenticated private message");return rumor;
    }
}
