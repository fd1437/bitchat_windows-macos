using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace BitChat.Core;

public static class Crypto
{
    public static byte[] Hash(byte[] data) => SHA256.HashData(data);
    public static byte[] Hmac(byte[] key,byte[] data) => HMACSHA256.HashData(key,data);
    public static byte[][] NoiseKdf(byte[] salt,byte[] ikm,int count=2)
    {
        var prk=Hmac(salt,ikm);var result=new byte[count][];byte[] previous=[];
        for(int i=0;i<count;i++) result[i]=previous=Hmac(prk,Bytes.Join(previous,[(byte)(i+1)]));
        CryptographicOperations.ZeroMemory(prk);return result;
    }
    public static byte[] PublicX(byte[] key) => new X25519PrivateKeyParameters(key,0).GeneratePublicKey().GetEncoded();
    public static byte[] Dh(byte[] key,byte[] remote)
    {
        if(remote.Length!=32)throw new CryptographicException("Invalid X25519 public key");
        var b=new byte[32];new X25519PrivateKeyParameters(key,0).GenerateSecret(new X25519PublicKeyParameters(remote,0),b,0);
        if(b.All(x=>x==0))throw new CryptographicException("Invalid shared secret");return b;
    }
    public static byte[] PublicEd(byte[] seed) => new Ed25519PrivateKeyParameters(seed,0).GeneratePublicKey().GetEncoded();
    public static byte[] SignEd(byte[] seed,byte[] data)
    {var s=new Ed25519Signer();s.Init(true,new Ed25519PrivateKeyParameters(seed,0));s.BlockUpdate(data,0,data.Length);return s.GenerateSignature();}
    public static bool VerifyEd(byte[] key,byte[] data,byte[] signature)
    {
        if(key.Length!=32||signature.Length!=64)return false;
        try{var s=new Ed25519Signer();s.Init(false,new Ed25519PublicKeyParameters(key,0));s.BlockUpdate(data,0,data.Length);return s.VerifySignature(signature);}catch{return false;}
    }
    public static byte[] Aead(byte[] key,ulong nonce,byte[] data,byte[] ad,bool decrypt=false)
    {
        var iv=new byte[12];BinaryPrimitives.WriteUInt64LittleEndian(iv.AsSpan(4),nonce);
        return AeadNonce(key,iv,data,ad,decrypt);
    }
    public static byte[] AeadNonce(byte[] key,byte[] iv,byte[] data,byte[] ad,bool decrypt=false)
    {
        var cipher=new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(!decrypt,new Org.BouncyCastle.Crypto.Parameters.AeadParameters(new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key),128,iv,ad));
        var output=new byte[cipher.GetOutputSize(data.Length)];
        try {int n=cipher.ProcessBytes(data,0,data.Length,output,0);n+=cipher.DoFinal(output,n);return output[..n];}
        catch(Org.BouncyCastle.Crypto.InvalidCipherTextException e){throw new CryptographicException("Invalid authentication tag",e);}
    }
}

public sealed class NoiseCipher(byte[] key,bool explicitNonce=false):IDisposable
{
    private ulong counter,highest;
    private readonly HashSet<ulong> received=[];
    public byte[] Encrypt(byte[] plain,byte[]? ad=null)
    {
        if(counter>=uint.MaxValue)throw new CryptographicException("Noise rekey required");
        var c=Crypto.Aead(key,counter,plain,ad??[]);return explicitNonce?Bytes.Join(Bytes.U32((uint)counter++),c):Increment(c);
    }
    private byte[] Increment(byte[] data){counter++;return data;}
    public byte[] Decrypt(byte[] data,byte[]? ad=null)
    {
        ulong n=counter;
        if(explicitNonce){if(data.Length<20)throw new CryptographicException("Truncated Noise frame");n=BinaryPrimitives.ReadUInt32BigEndian(data);data=data[4..];
            if(received.Contains(n)||(highest>=1024&&n<=highest-1024))throw new CryptographicException("Replayed Noise frame");}
        var p=Crypto.Aead(key,n,data,ad??[],true);
        if(explicitNonce){highest=Math.Max(highest,n);received.Add(n);received.RemoveWhere(x=>highest>=1024&&x<=highest-1024);}counter++;return p;
    }
    public void Dispose(){CryptographicOperations.ZeroMemory(key);received.Clear();}
}

/// Noise XX wire-compatible with NoiseProtocol.swift. Callers must pin RemoteStatic
/// to the expected identity before delivering application data.
public sealed class NoiseHandshake:IDisposable
{
    private readonly bool initiator;private readonly bool oneWay;
    private readonly byte[] localStatic,localEphemeral;
    private byte[]? remoteEphemeral;
    public byte[]? RemoteStatic {get;private set;}
    private byte[] hash,chaining;
    private NoiseCipher? cipher;
    private int step;
    public bool Complete=>step==3;
    public byte[] HandshakeHash=>hash.ToArray();
    public NoiseHandshake(bool initiator,byte[] localStatic,byte[]? prologue=null,byte[]? ephemeral=null,bool oneWay=false,byte[]? remoteStatic=null)
    {
        this.oneWay=oneWay;RemoteStatic=remoteStatic;
        this.initiator=initiator;this.localStatic=localStatic.ToArray();localEphemeral=ephemeral?.ToArray()??RandomNumberGenerator.GetBytes(32);
        var name=Bytes.Utf8(oneWay?"Noise_X_25519_ChaChaPoly_SHA256":"Noise_XX_25519_ChaChaPoly_SHA256");hash=name.Length<=32?Bytes.Join(name,new byte[32-name.Length]):Crypto.Hash(name);chaining=hash.ToArray();MixHash(prologue??[]);if(oneWay)MixHash(initiator?remoteStatic??throw new ArgumentException("Noise X requires a recipient key"):Crypto.PublicX(localStatic));
    }
    private void MixHash(byte[] data)=>hash=Crypto.Hash(Bytes.Join(hash,data));
    private void MixKey(byte[] dh){var k=Crypto.NoiseKdf(chaining,dh);CryptographicOperations.ZeroMemory(chaining);chaining=k[0];cipher?.Dispose();cipher=new(k[1]);CryptographicOperations.ZeroMemory(dh);}
    private byte[] EncryptHash(byte[] data){var c=cipher?.Encrypt(data,hash)??data;MixHash(c);return c;}
    private byte[] DecryptHash(byte[] data){var p=cipher?.Decrypt(data,hash)??data;MixHash(data);return p;}
    public byte[] Write(byte[]? payload=null)
    {
        payload??=[];byte[] result;
        if(oneWay)
        {
            if(step!=0||!initiator)throw new InvalidOperationException("Unexpected Noise X write");var e=Crypto.PublicX(localEphemeral);MixHash(e);MixKey(Crypto.Dh(localEphemeral,RemoteStatic!));var encryptedStatic=EncryptHash(Crypto.PublicX(localStatic));MixKey(Crypto.Dh(localStatic,RemoteStatic!));step=3;return Bytes.Join(e,encryptedStatic,EncryptHash(payload));
        }
        if(step==0&&initiator){var e=Crypto.PublicX(localEphemeral);MixHash(e);result=Bytes.Join(e,EncryptHash(payload));}
        else if(step==1&&!initiator)
        {
            var e=Crypto.PublicX(localEphemeral);MixHash(e);MixKey(Crypto.Dh(localEphemeral,remoteEphemeral!));
            var s=EncryptHash(Crypto.PublicX(localStatic));MixKey(Crypto.Dh(localStatic,remoteEphemeral!));result=Bytes.Join(e,s,EncryptHash(payload));
        }
        else if(step==2&&initiator){var s=EncryptHash(Crypto.PublicX(localStatic));MixKey(Crypto.Dh(localStatic,remoteEphemeral!));result=Bytes.Join(s,EncryptHash(payload));}
        else throw new InvalidOperationException("Unexpected Noise write");step++;return result;
    }
    public byte[] Read(byte[] data)
    {
        var r=new WireReader(data);byte[] payload;
        if(oneWay)
        {
            if(step!=0||initiator)throw new InvalidOperationException("Unexpected Noise X read");remoteEphemeral=r.Read(32);MixHash(remoteEphemeral);MixKey(Crypto.Dh(localStatic,remoteEphemeral));RemoteStatic=DecryptHash(r.Read(48));MixKey(Crypto.Dh(localStatic,RemoteStatic));step=3;return DecryptHash(r.Read(r.Remaining));
        }
        if(step==0&&!initiator){remoteEphemeral=r.Read(32);MixHash(remoteEphemeral);payload=DecryptHash(r.Read(r.Remaining));}
        else if(step==1&&initiator)
        {
            remoteEphemeral=r.Read(32);MixHash(remoteEphemeral);MixKey(Crypto.Dh(localEphemeral,remoteEphemeral));RemoteStatic=DecryptHash(r.Read(48));MixKey(Crypto.Dh(localEphemeral,RemoteStatic));payload=DecryptHash(r.Read(r.Remaining));
        }
        else if(step==2&&!initiator){RemoteStatic=DecryptHash(r.Read(48));MixKey(Crypto.Dh(localEphemeral,RemoteStatic));payload=DecryptHash(r.Read(r.Remaining));}
        else throw new InvalidOperationException("Unexpected Noise read");step++;return payload;
    }
    public (NoiseCipher Send,NoiseCipher Receive) Split(bool explicitNonce=true)
    {
        if(!Complete)throw new InvalidOperationException("Handshake incomplete");var k=Crypto.NoiseKdf(chaining,[]);var a=new NoiseCipher(k[0],explicitNonce);var b=new NoiseCipher(k[1],explicitNonce);return initiator?(a,b):(b,a);
    }
    public void Dispose(){CryptographicOperations.ZeroMemory(localStatic);CryptographicOperations.ZeroMemory(localEphemeral);CryptographicOperations.ZeroMemory(chaining);cipher?.Dispose();}
}
