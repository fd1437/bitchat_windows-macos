using System.Formats.Cbor;
using System.Text.Json;
namespace BitChat.Core;
public sealed record CashuInfo(string Token,long Amount,string Unit,string? Mint);
public static class Cashu
{
    private const long Max=2100000000000000;
    public static CashuInfo Decode(string input)
    {
        string token=input.Trim();if(token.StartsWith("cashu://",StringComparison.OrdinalIgnoreCase))token=token[8..];else if(token.StartsWith("cashu:",StringComparison.OrdinalIgnoreCase))token=token[6..];token=Uri.UnescapeDataString(token);
        if(token.Length is <12 or >60000||(!token.StartsWith("cashuA")&&!token.StartsWith("cashuB")))throw new FormatException("Expected a cashuA or cashuB token");
        string encoded=token[6..].Replace('-','+').Replace('_','/');encoded=encoded.PadRight((encoded.Length+3)/4*4,'=');var bytes=Convert.FromBase64String(encoded);long total=0;string unit="sat";string? mint=null;int proofs=0;
        void Add(long amount){if(amount<=0||amount>Max||total>Max-amount||++proofs>4096)throw new FormatException("Invalid Cashu amount");total+=amount;}
        if(token[5]=='A')
        {
            using var json=JsonDocument.Parse(bytes,new(){MaxDepth=16});var root=json.RootElement;if(root.TryGetProperty("unit",out var u))unit=u.GetString()??"sat";
            foreach(var group in root.GetProperty("token").EnumerateArray()){mint??=group.TryGetProperty("mint",out var m)?m.GetString():null;foreach(var proof in group.GetProperty("proofs").EnumerateArray())Add(proof.GetProperty("amount").GetInt64());}
        }
        else
        {
            var reader=new CborReader(bytes,CborConformanceMode.Lax);int nodes=0;
            object? Read(int depth)
            {
                if(depth>16||++nodes>20000)throw new FormatException("Cashu CBOR limit");
                switch(reader.PeekState())
                {
                    case CborReaderState.StartMap:
                        reader.ReadStartMap();var map=new Dictionary<string,object?>();while(reader.PeekState()!=CborReaderState.EndMap){var key=reader.ReadTextString();if(!map.TryAdd(key,Read(depth+1)))throw new FormatException("Duplicate Cashu CBOR key");}reader.ReadEndMap();return map;
                    case CborReaderState.StartArray:
                        reader.ReadStartArray();var list=new List<object?>();while(reader.PeekState()!=CborReaderState.EndArray)list.Add(Read(depth+1));reader.ReadEndArray();return list;
                    case CborReaderState.UnsignedInteger:return checked((long)reader.ReadUInt64());
                    case CborReaderState.NegativeInteger:return reader.ReadInt64();
                    case CborReaderState.TextString:return reader.ReadTextString();
                    case CborReaderState.ByteString:return reader.ReadByteString();
                    case CborReaderState.Null:reader.ReadNull();return null;
                    case CborReaderState.Boolean:return reader.ReadBoolean();
                    default:throw new FormatException("Unsupported Cashu CBOR value");
                }
            }
            var root=Read(0) as Dictionary<string,object?>??throw new FormatException("Invalid Cashu v4 map");if(reader.BytesRemaining!=0)throw new FormatException("Trailing Cashu bytes");
            mint=root.GetValueOrDefault("m") as string;unit=root.GetValueOrDefault("u") as string??"sat";
            foreach(var groupObject in root["t"] as List<object?>??throw new FormatException("Missing Cashu proofs"))
            {var group=groupObject as Dictionary<string,object?>??throw new FormatException();foreach(var proofObject in group["p"] as List<object?>??throw new FormatException()){var proof=proofObject as Dictionary<string,object?>??throw new FormatException();Add(proof["a"] is long amount?amount:throw new FormatException("Invalid Cashu amount"));}}
        }
        if(total<=0||mint==null||!Uri.TryCreate(mint,UriKind.Absolute,out var uri)||uri.Scheme is not("https" or "http"))throw new FormatException("Invalid Cashu token");return new(token,total,unit,uri.Host);
    }
}
