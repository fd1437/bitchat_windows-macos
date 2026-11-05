namespace BitChat.Core;

public static class BleFraming
{
    // Below this size a 1 MiB file would exceed the receiver's 8192-fragment
    // budget. Use 512-byte protocol fragments with GATT long writes instead.
    public static int CentralFrameLimit(int attPayload)=>attPayload>=182?Math.Min(attPayload,512):512;
    public static IEnumerable<byte[]> Chunks(byte[] encoded,int maximum)
    {
        if(maximum<20||maximum>512)throw new ArgumentOutOfRangeException(nameof(maximum));
        for(int offset=0;offset<encoded.Length;offset+=maximum)
            yield return encoded.AsSpan(offset,Math.Min(maximum,encoded.Length-offset)).ToArray();
    }
}
