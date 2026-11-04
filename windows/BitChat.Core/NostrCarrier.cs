namespace BitChat.Core;
public sealed record NostrCarrier(byte Direction,string Cell,NostrEvent Event)
{
    public byte[] Encode()
    {var json=Bytes.Utf8(Event.Serialize());if(Direction is <1 or >4||!Geohash.Valid(Cell)||json.Length>16384)throw new InvalidDataException("Invalid Nostr carrier");return Bytes.Join(Tlv16.Put(1,[Direction]),Tlv16.Put(2,Bytes.Utf8(Cell)),Tlv16.Put(3,json));}
    public bool Valid()
    {
        string tag=Direction>=3?"r":"g";
        return Direction is >=1 and <=4&&Geohash.Valid(Cell)&&Event.kind is 20000 or 20001&&Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-Event.created_at)<=900&&Event.tags.Count(t=>t.Length==2&&t[0]==tag&&t[1]==Cell)==1&&(Event.kind!=20001||Event.content.Length==0)&&Event.Valid();
    }
    public static NostrCarrier Decode(byte[] bytes)
    {var f=Tlv16.Parse(bytes);var json=f.Require(3);if(json.Length>16384)throw new InvalidDataException("Carrier event too large");var carrier=new NostrCarrier(f.Require(1,1)[0],Bytes.Text(f.Require(2)),NostrEvent.Parse(Bytes.Text(json)));if(!carrier.Valid())throw new InvalidDataException("Invalid carried Nostr event");return carrier;}
}
