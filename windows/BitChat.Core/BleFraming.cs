namespace BitChat.Core;

public static class BleFraming
{
    public static IEnumerable<byte[]> Chunks(byte[] encoded,int maximum)
    {
        if(maximum<20||maximum>512)throw new ArgumentOutOfRangeException(nameof(maximum));
        for(int offset=0;offset<encoded.Length;offset+=maximum)
            yield return encoded.AsSpan(offset,Math.Min(maximum,encoded.Length-offset)).ToArray();
    }
}
