namespace BitChat.Core;

public sealed record AuthenticatedLink(string Ingress,Guid Epoch,string Peer,Guid Generation);

// An announcement is not a physical-link authentication. Leases are created
// only after a Noise handshake observed on this exact connection epoch.
public sealed class AuthenticatedLinks
{
    private sealed record Entry(Guid Epoch,string? Peer=null,Guid? Generation=null);
    private readonly object sync=new();
    private readonly Dictionary<string,Entry> links=[];
    public Guid Open(string ingress){lock(sync){var epoch=Guid.NewGuid();links[ingress]=new(epoch);return epoch;}}
    public Guid EnsureOpen(string ingress){lock(sync)return links.TryGetValue(ingress,out var link)?link.Epoch:Open(ingress);}
    public Guid? Epoch(string ingress){lock(sync)return links.GetValueOrDefault(ingress)?.Epoch;}
    public void Close(string ingress,Guid? expected=null)
    {lock(sync)if(links.TryGetValue(ingress,out var link)&&(expected==null||link.Epoch==expected))links.Remove(ingress);}
    public bool Authenticate(string ingress,Guid epoch,string peer,Guid generation)
    {
        lock(sync)
        {
            if(!links.TryGetValue(ingress,out var link)||link.Epoch!=epoch)return false;
            links[ingress]=new(epoch,peer,generation);return true;
        }
    }
    public AuthenticatedLink[] For(string peer,Guid? generation=null)
    {
        lock(sync)return links.Where(p=>p.Value.Peer==peer&&p.Value.Generation!=null&&(generation==null||p.Value.Generation==generation))
            .Select(p=>new AuthenticatedLink(p.Key,p.Value.Epoch,peer,p.Value.Generation!.Value)).ToArray();
    }
    public bool Current(AuthenticatedLink lease)
    {lock(sync)return links.TryGetValue(lease.Ingress,out var entry)&&entry.Epoch==lease.Epoch&&entry.Peer==lease.Peer&&entry.Generation==lease.Generation;}
}
