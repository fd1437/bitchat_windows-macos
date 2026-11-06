using System.Globalization;
using System.Text;

namespace BitChat.Core;

public static class IdentityNames
{
    // Nicknames are labels, never a cryptographic identity or unique address.
    public static string Display(string name)=>string.Concat(name.Normalize(NormalizationForm.FormKC).EnumerateRunes()
        .Where(r=>Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
        .Take(64).Select(r=>r.ToString())).Trim();
    public static string Resolve(string value,IEnumerable<(string Id,string Name)> contacts)
    {
        var peers=contacts.DistinctBy(p=>p.Id).ToArray();
        var exact=peers.Where(p=>p.Id.Equals(value,StringComparison.OrdinalIgnoreCase)).ToArray();if(exact.Length==1)return exact[0].Id;
        string name=Display(value);var matches=peers.Where(p=>Display(p.Name).Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length>1)throw new ArgumentException("Several identities use this nickname. Use the full peer ID from the sidebar or /who.");
        return matches.Length==1?matches[0].Id:throw new ArgumentException("Unknown peer: "+name);
    }
    public static string SenderLabel(string nickname,string? id)=>id==null?Display(nickname):"["+id[..Math.Min(16,id.Length)]+"] "+Display(nickname);
}
