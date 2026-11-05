using System.Security.Cryptography;
using System.Globalization;
namespace BitChat.Core;

public static class Tlv8
{
    public static Dictionary<byte,byte[]> Parse(byte[] bytes)
    {var r=new WireReader(bytes);var fields=new Dictionary<byte,byte[]>();while(r.Remaining>0){byte t=r.Byte();if(!fields.TryAdd(t,r.Read(r.Byte())))throw new InvalidDataException("Duplicate TLV");}return fields;}
}
public sealed record VerificationQr(int Version,string Noise,string Signing,string Nickname,long Timestamp,string Nonce,string Signature,string? Npub=null)
{
    public byte[] Canonical()=>Bytes.Join(new[]{"bitchat-verify-v1",Version.ToString(CultureInfo.InvariantCulture),Noise.ToLowerInvariant(),Signing.ToLowerInvariant(),Npub??"",Nickname,Timestamp.ToString(CultureInfo.InvariantCulture),Nonce}.Select(Bytes.ShortString).ToArray());
    public string Url()
    {
        var fields=new Dictionary<string,string>{{"v",Version.ToString()},{"noise",Noise},{"sign",Signing},{"nick",Nickname},{"ts",Timestamp.ToString()},{"nonce",Nonce},{"sig",Signature}};if(Npub!=null)fields["npub"]=Npub;
        return "bitchat://verify?"+string.Join("&",fields.Select(p=>p.Key+"="+Uri.EscapeDataString(p.Value)));
    }
    public static VerificationQr Create(byte[] noiseKey,byte[] signingSeed,string nickname,string? npub=null)
    {
        var nonce=Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+','-').Replace('/','_');
        var qr=new VerificationQr(1,Bytes.Hex(Crypto.PublicX(noiseKey)),Bytes.Hex(Crypto.PublicEd(signingSeed)),nickname,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),nonce,"",npub);return qr with{Signature=Bytes.Hex(Crypto.SignEd(signingSeed,qr.Canonical()))};
    }
    public static VerificationQr Parse(string url,int maxAge=300)
    {
        var uri=new Uri(url);if(uri.Scheme!="bitchat"||uri.Host!="verify"||url.Length>4096)throw new InvalidDataException("Invalid BitChat verification URL");
        var fields=uri.Query.TrimStart('?').Split('&').Select(p=>p.Split('=',2)).ToDictionary(p=>Uri.UnescapeDataString(p[0]),p=>Uri.UnescapeDataString(p.Length==2?p[1]:""));
        var qr=new VerificationQr(int.Parse(fields["v"],CultureInfo.InvariantCulture),fields["noise"],fields["sign"],fields["nick"],long.Parse(fields["ts"],CultureInfo.InvariantCulture),fields["nonce"],fields["sig"],fields.GetValueOrDefault("npub"));
        if(qr.Version!=1||Convert.FromHexString(qr.Noise).Length!=32||Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-qr.Timestamp)>maxAge||!Crypto.VerifyEd(Convert.FromHexString(qr.Signing),qr.Canonical(),Convert.FromHexString(qr.Signature)))throw new CryptographicException("Invalid or expired verification QR");return qr;
    }
    public byte[] Challenge(byte[] nonce)=>Bytes.Join([0x10],Announcement.Tlv(1,Bytes.Utf8(Noise)),Announcement.Tlv(2,nonce));
    public static byte[] ResponseSigning(string noise,byte[] nonce)=>Bytes.Join(Bytes.Utf8("bitchat-verify-resp-v1"),Bytes.ShortString(noise),nonce);
}
public sealed record Vouch(byte[] Fingerprint,byte[] SigningKey,ulong Timestamp,byte[] Signature)
{
    private byte[] Signing()=>Bytes.Join(Bytes.Utf8("bitchat-vouch-v1"),Fingerprint,SigningKey,Bytes.U64(Timestamp));
    public bool Valid(byte[] voucherKey)=>Fingerprint.Length==32&&SigningKey.Length==32&&Timestamp>=(ulong)(Bytes.Now-30L*86400000)&&Timestamp<=(ulong)(Bytes.Now+3600000)&&Crypto.VerifyEd(voucherKey,Signing(),Signature);
    public static Vouch Create(byte[] fingerprint,byte[] signingKey,byte[] seed){var v=new Vouch(fingerprint,signingKey,(ulong)Bytes.Now,[]);return v with{Signature=Crypto.SignEd(seed,v.Signing())};}
    public byte[] Encode()=>Bytes.Join(Announcement.Tlv(1,Fingerprint),Announcement.Tlv(2,SigningKey),Announcement.Tlv(3,Bytes.U64(Timestamp)),Announcement.Tlv(4,Signature));
    public static Vouch Decode(byte[] bytes){var f=Tlv8.Parse(bytes);return new(f[1],f[2],new WireReader(f[3]).U64(),f[4]);}
    public static byte[] Batch(IEnumerable<Vouch> items){var list=items.Take(16).ToArray();return Bytes.Join(new byte[]{(byte)list.Length},Bytes.Join(list.Select(v=>{var b=v.Encode();return Bytes.Join(Bytes.U16((ushort)b.Length),b);}).ToArray()));}
    public static Vouch[] ParseBatch(byte[] bytes){var r=new WireReader(bytes);int n=r.Byte();if(n>16)throw new InvalidDataException("Vouch batch limit");var result=new Vouch[n];for(int i=0;i<n;i++)result[i]=Decode(r.Read(r.U16()));if(r.Remaining>0)throw new InvalidDataException("Trailing vouch bytes");return result;}
}
public static class Bech32
{
    private const string Alphabet="qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static uint Poly(IEnumerable<int> values)
    {uint check=1;uint[] generator=[0x3b6a57b2,0x26508e6d,0x1ea119fa,0x3d4233dd,0x2a1462b3];foreach(int v in values){uint top=check>>25;check=((check&0x1ffffff)<<5)^(uint)v;for(int i=0;i<5;i++)if(((top>>i)&1)!=0)check^=generator[i];}return check;}
    private static IEnumerable<int> Expand(string hrp)=>hrp.Select(c=>(int)c>>5).Concat(new[]{0}).Concat(hrp.Select(c=>(int)c&31));
    public static string Encode(string hrp,byte[] bytes)
    {
        var data=new List<int>();int acc=0,bits=0;foreach(byte b in bytes){acc=(acc<<8)|b;bits+=8;while(bits>=5){bits-=5;data.Add((acc>>bits)&31);}}if(bits>0)data.Add((acc<<(5-bits))&31);
        uint sum=Poly(Expand(hrp).Concat(data).Concat(new int[6]))^1;for(int i=0;i<6;i++)data.Add((int)((sum>>(5*(5-i)))&31));return hrp+"1"+new string(data.Select(v=>Alphabet[v]).ToArray());
    }
    public static byte[] Decode(string value,string expectedHrp)
    {
        if(value!=value.ToLowerInvariant()&&value!=value.ToUpperInvariant())throw new FormatException("Mixed-case bech32");value=value.ToLowerInvariant();int sep=value.LastIndexOf('1');if(sep<1||value[..sep]!=expectedHrp)throw new FormatException("Wrong bech32 prefix");
        int[] data=value[(sep+1)..].Select(c=>Alphabet.IndexOf(c)).ToArray();if(data.Length<6||data.Any(x=>x<0)||Poly(Expand(expectedHrp).Concat(data))!=1)throw new FormatException("Invalid bech32 checksum");
        var bytes=new List<byte>();int acc=0,bits=0;foreach(int n in data[..^6]){acc=(acc<<5)|n;bits+=5;while(bits>=8){bits-=8;bytes.Add((byte)(acc>>bits));}}if(bits>=5||((acc<<(8-bits))&255)!=0)throw new FormatException("Invalid bech32 padding");return bytes.ToArray();
    }
}
