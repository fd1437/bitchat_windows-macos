using System.Security.Cryptography;
namespace BitChat.Core;
public static class NostrEmbedded
{
    public static string Message(string text,string id,byte[]? sender=null,byte[]? recipient=null)
    {var payload=Bytes.Join([1],Announcement.Tlv(0,Bytes.Utf8(id)),Announcement.Tlv(1,Bytes.Utf8(text)));return Encode(payload,sender,recipient);}
    public static string Ack(string id,bool read=false)=>Encode(Bytes.Join([(byte)(read?2:3)],Bytes.Utf8(id)));
    private static string Encode(byte[] payload,byte[]? sender=null,byte[]? recipient=null)
    {var packet=new MeshPacket(0x11,sender??RandomNumberGenerator.GetBytes(8),payload,recipient,Timestamp:(ulong)Bytes.Now);return "bitchat1:"+Convert.ToBase64String(packet.Encode(true)).TrimEnd('=').Replace('+','-').Replace('/','_');}
    public static MeshPacket Decode(string content)
    {
        if(!content.StartsWith("bitchat1:")||content.Length>65536)throw new InvalidDataException("Invalid embedded BitChat packet");string encoded=content[9..].Replace('-','+').Replace('_','/');encoded=encoded.PadRight((encoded.Length+3)/4*4,'=');var packet=MeshPacket.Decode(Convert.FromBase64String(encoded));if(packet.Type!=0x11||packet.Payload.Length==0)throw new InvalidDataException("Unsupported embedded packet");return packet;
    }
    public static (string Id,string Text) PrivateMessage(byte[] payload)
    {var fields=Tlv8.Parse(payload);return(Bytes.Text(fields[0]),Bytes.Text(fields[1]));}
}
