namespace BitChat.Core;

public sealed partial class MeshEngine
{
    public async Task ProveDirectLink(string peer,Guid epoch,IReadOnlyCollection<Guid>? authenticatedEpochs=null)
    {
        await gate.WaitAsync();
        try
        {
            if(stopping||IsBlocked(peer)||!peers.ContainsKey(peer))return;
            if(sessions.TryGetValue(peer,out var previous))
            {
                if(previous.Send!=null&&(previous.AuthenticatedEpoch==epoch||authenticatedEpochs?.Contains(previous.AuthenticatedEpoch??Guid.Empty)==true))return;
                if(previous.ProvenCapabilities==null&&Bytes.Now-previous.Started<30000)return;
            }
            if(sessions.Count>=128&&!sessions.ContainsKey(peer))return;
            var prior=previous?.Send!=null?previous:previous?.Previous;
            previous?.Proof.TrySetResult(false);previous?.Handshake.Dispose();
            sessions[peer]=new Session(true,noiseKey){Previous=prior,DesiredEpoch=epoch};
            await send(Packet(0x10,sessions[peer].Handshake.Write(),peer,signed:false));
        }
        finally{gate.Release();}
    }
    public async Task<bool> DeliverCourierDirect(CourierEnvelope envelope,AuthenticatedLink lease,Func<MeshPacket,AuthenticatedLink,Task<int>> directSend)
    {
        await gate.WaitAsync();
        try
        {
            if(stopping||IsBlocked(lease.Peer)||!envelope.Valid||!sessions.TryGetValue(lease.Peer,out var session)
                ||session.Send==null||session.ProvenCapabilities==null||session.Generation!=lease.Generation||session.AuthenticatedEpoch!=lease.Epoch)return false;
            // This delegate may use only the bound physical connection; never
            // the flood transport, a stale reachability hint or a RAM spool.
            return await directSend(Packet(4,envelope.Encode(),lease.Peer),lease)>0;
        }
        finally{gate.Release();}
    }
}
