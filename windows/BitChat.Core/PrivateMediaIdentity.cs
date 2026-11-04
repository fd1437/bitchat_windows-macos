namespace BitChat.Core;

public static class PrivateMediaIdentity
{
    public static bool IsStable(string id) => id.Length == 38 && id.StartsWith("media-", StringComparison.Ordinal)
        && id.AsSpan(6).IndexOfAnyExcept("0123456789abcdef") < 0;

    // Matches PrivateMediaMessageIdentity in the Swift app. No new wire TLV:
    // older Android clients reject unknown file tags.
    public static string? StableId(string sender, string recipient, string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(['/', '\\']) >= 0) return null;
        int dot = name.LastIndexOf('.');
        if (dot < 1) return null;
        string stem = name[..dot], extension = name[(dot + 1)..].ToLowerInvariant();
        bool image = stem.StartsWith("img_", StringComparison.Ordinal) && extension is "jpg" or "jpeg";
        bool voice = stem.StartsWith("voice_", StringComparison.Ordinal) && extension == "m4a";
        if (!image && !voice) return null;
        string entropy = stem[(stem.LastIndexOf('_') + 1)..];
        bool uuid = Guid.TryParseExact(entropy, "D", out _);
        string burst = voice ? stem[6..] : "";
        bool burstId = burst.Length == 16 && burst.All(Uri.IsHexDigit);
        if (!uuid && !burstId) return null;
        static string Short(string peer)
        {
            if (peer.StartsWith("mesh:", StringComparison.Ordinal)) peer = peer[5..];
            else if (peer.StartsWith("noise:", StringComparison.Ordinal)) peer = peer[6..];
            if (peer.Length == 64 && peer.All(Uri.IsHexDigit)) return Bytes.Hex(Crypto.Hash(Convert.FromHexString(peer)))[..16];
            if (peer.Length != 16 || !peer.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid mesh peer ID");
            return peer[..16].ToLowerInvariant();
        }
        var fields = new[] { Short(sender), Short(recipient), name }.Select(Bytes.Utf8);
        var input = Bytes.Join(new[] { Bytes.Utf8("bitchat-private-media-message-v1") }
            .Concat(fields.Select(field => Bytes.Join(Bytes.U32((uint)field.Length), field))).ToArray());
        return "media-" + Bytes.Hex(Crypto.Hash(input))[..32];
    }
}
