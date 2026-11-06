using System.Security.Cryptography;
using BitChat.Core;

internal static class IdentityAndTransferTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var contacts=new[]{("1111111111111111","Alice"),("2222222222222222","Alice")};
        bool ambiguous=false;try{IdentityNames.Resolve("Alice",contacts);}catch(ArgumentException){ambiguous=true;}
        check(ambiguous,"Duplicate nicknames cannot silently address the first matching identity");
        check(IdentityNames.Resolve("2222222222222222",contacts)=="2222222222222222","An explicit peer ID disambiguates identical nicknames");
        check(!IdentityNames.Display("Alice\u202E\nBob").Contains('\u202E')&&!IdentityNames.Display("Alice\nBob").Contains('\n'),"Display names cannot inject bidi overrides or extra identity rows");
        var attempted=new List<int>();var outcomes=new List<bool>();
        await AttachmentBatch.Run(new[]{1,2,3},item=>{attempted.Add(item);if(item==1)throw new IOException("simulated first-file failure");return Task.CompletedTask;},(_,error)=>outcomes.Add(error==null));
        check(attempted.SequenceEqual(new[]{1,2,3})&&outcomes.SequenceEqual(new[]{false,true,true}),"A failed first file does not prevent the remaining selected files from sending");

        var transit=new Queue<(bool ToPc,MeshPacket Packet)>();
        var radioEntered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var radioRelease=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);bool blockFile=false;
        async Task<int> Send(bool toPc,MeshPacket packet)
        {
            if(!toPc&&blockFile&&packet.Type==0x11&&packet.Payload.Length>1000){radioEntered.TrySetResult(true);await radioRelease.Task;}
            transit.Enqueue((toPc,packet));return 1;
        }
        byte[] victimNoise=RandomNumberGenerator.GetBytes(32),victimSigning=RandomNumberGenerator.GetBytes(32),attackerSigning=RandomNumberGenerator.GetBytes(32);
        using var victim=new MeshEngine(victimNoise,victimSigning,"Alice",packet=>Send(true,packet)){Capabilities=[0x79,0x03]};
        using var pc=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"Windows",packet=>Send(false,packet)){Capabilities=[0x79,0x03]};
        async Task Pump(){int budget=1000;while(transit.Count>0){if(--budget==0)throw new Exception("Identity test relay loop");var item=transit.Dequeue();await(item.ToPc?pc:victim).Receive(item.Packet.Encode());}}
        var messages=new List<ChatMessage>();pc.Message+=messages.Add;
        MeshPacket Signed(byte type,byte[] payload){var packet=new MeshPacket(type,victim.PeerId,payload,Timestamp:(ulong)Bytes.Now);return packet with{Signature=Crypto.SignEd(attackerSigning,packet.SigningBytes())};}
        // The attacker knows Alice's PUBLIC Noise key, but has neither private key.
        var fakeAnn=Signed(1,new Announcement("Alice",Crypto.PublicX(victimNoise),Crypto.PublicEd(attackerSigning),[0x79,0x03]).Encode());
        await pc.Receive(fakeAnn.Encode());await pc.Receive(Signed(2,Bytes.Utf8("forged Alice message")).Encode());
        check(messages.Count==0,"Copied public Noise key plus an attacker's self-signature cannot impersonate a fresh contact");
        await pc.Announce();await Pump();
        check(messages.Count==0,"A forged public message stays rejected after the real Noise owner authenticates");
        await victim.Announce();await victim.SendPublic("real Alice message");await Pump();
        check(messages.Count==1&&messages[0].Content=="real Alice message","The actual Noise owner recovers from a poisoned provisional announcement");
        await pc.Receive(fakeAnn.Encode());await pc.Receive(Signed(2,Bytes.Utf8("forged after pinning")).Encode());
        check(messages.Count==1,"Authenticated signing-key pins reject subsequent identity substitution");

        // A fragmented public file can finish before its author's first announce.
        var mediaTransit=new Queue<MeshPacket>();using var lateReceiver=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"late",packet=>{mediaTransit.Enqueue(packet);return Task.FromResult(1);});
        byte[] bytes=RandomNumberGenerator.GetBytes(4096);var file=new MediaPacket("public.bin","application/octet-stream",bytes);var unsigned=new MeshPacket(0x22,victim.PeerId,file.Encode(),Version:2,Timestamp:(ulong)Bytes.Now);var signed=unsigned with{Signature=Crypto.SignEd(victimSigning,unsigned.SigningBytes())};
        MediaPacket? received=null;lateReceiver.Media+=(_,media,_)=>received=media;
        foreach(var fragment in FragmentAssembler.Split(signed,128))await lateReceiver.Receive(fragment.Encode());
        lateReceiver.RestorePeer(new(victim.Id,"Alice",Crypto.PublicX(victimNoise),Crypto.PublicEd(victimSigning),[0],0));lateReceiver.RestorePinnedKey(victim.Id,Crypto.PublicEd(victimSigning));
        var announce=new MeshPacket(1,victim.PeerId,new Announcement("Alice",Crypto.PublicX(victimNoise),Crypto.PublicEd(victimSigning),[0]).Encode(),Timestamp:(ulong)Bytes.Now);
        await lateReceiver.Receive((announce with{Signature=Crypto.SignEd(victimSigning,announce.SigningBytes())}).Encode());
        check(received?.Content.SequenceEqual(bytes)==true,"Early fragmented files retain the complete reconstructed packet until author proof");

        Guid firstLink=Guid.NewGuid(),secondLink=Guid.NewGuid();await pc.PeerLinkReady(victim.Id,firstLink);await pc.PeerLinkReady(victim.Id,secondLink);
        int expired=0;pc.MediaTransferStatus+=(_,_,status)=>{if(status=="unconfirmed")expired++;};long mediaNow=Bytes.Now;pc.MediaClock=()=>mediaNow;
        blockFile=true;var sending=pc.SendMedia(new("img_"+Guid.NewGuid().ToString("D")+".jpg","application/octet-stream",RandomNumberGenerator.GetBytes(8192)),victim.Id);
        await radioEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));bool controlResponsive=false;
        try{await pc.Announce().WaitAsync(TimeSpan.FromSeconds(2));mediaNow+=180000;await pc.MaintainPrivateMedia().WaitAsync(TimeSpan.FromSeconds(2));controlResponsive=true;}
        finally{radioRelease.TrySetResult(true);await sending;}
        check(controlResponsive,"A stalled file write does not hold the mesh identity/receive gate");
        check(expired==0,"An actively transmitting file does not expire its reconnect retry state after two minutes");await Pump();
        int queuedBefore=transit.Count;await pc.PeerLinkReady(victim.Id,firstLink).WaitAsync(TimeSpan.FromSeconds(2));
        check(transit.Count==queuedBefore,"Alternating advertisements on two existing physical links do not restart the Noise session");
    }
}
