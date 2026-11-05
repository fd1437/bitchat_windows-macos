using System.Security.Cryptography;
using BitChat.Core;

internal static class PrivateMediaTests
{
    public static async Task Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"bitchat-media-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{Ledger(root,check);await Sessions(Path.Combine(root,"network"),check);}
        finally{Directory.Delete(root,true);}
    }
    private static string Save(string root,MediaPacket media)
    {Directory.CreateDirectory(root);string path=Path.Combine(root,Guid.NewGuid().ToString("N")+".dpapi");PrivateMediaLedger.AtomicWrite(path,media.Content);return path;}
    private static PrivateMediaLedger Open(string root,Action<string,byte[]>? write=null,Func<long>? clock=null,int capacity=4096)
        =>new(root,b=>b,b=>b,write,clock,capacity);
    private static void Ledger(string root,Action<bool,string> check)
    {
        root=Path.Combine(root,"ledger");Directory.CreateDirectory(root);
        string first="media-"+new string('a',32),second="media-"+new string('b',32),third="media-"+new string('c',32);
        var media=new MediaPacket("img_12345678-1234-1234-1234-123456789ABC.jpg","image/jpeg",[1,2,3,4]);
        var ledger=Open(root);int writes=0;
        string SavePayload(MediaPacket value){writes++;return Save(root,value);}
        var original=ledger.Accept(first,media,SavePayload);
        check(original.Kind==MediaDecisionKind.Accepted&&File.ReadAllBytes(original.Path!).SequenceEqual(media.Content),"Media receipt committed after payload exists");
        ledger=Open(root);var duplicate=ledger.Accept(first,media with{Content=[9],Mime="audio/mp4"},SavePayload);
        check(duplicate.Path==original.Path&&duplicate.Mime=="image/jpeg"&&writes==1,"Media duplicate after restart preserves canonical file and MIME");
        check(ledger.Delete(new Dictionary<string,string?>{{first,original.Path}})&&!File.Exists(original.Path),"Media deletion journals intent before unlink");
        ledger=Open(root);check(ledger.Accept(first,media,SavePayload).Kind==MediaDecisionKind.Deleted&&writes==1,"Restarted media retry cannot resurrect explicit deletion");
        bool failReceipt=true;
        var failing=Open(root,(path,bytes)=>{if(failReceipt&&path.EndsWith(".receipt"))throw new IOException("simulated disk failure");PrivateMediaLedger.AtomicWrite(path,bytes);});
        int before=Directory.GetFiles(root,"*.dpapi").Length;
        check(failing.Accept(second,media,SavePayload).Kind==MediaDecisionKind.Unavailable&&Directory.GetFiles(root,"*.dpapi").Length==before,"Failed receipt commit rolls back payload and withholds acceptance");
        failReceipt=false;var saved=failing.Accept(second,media,SavePayload);check(saved.Kind==MediaDecisionKind.Accepted,"Failed media storage remains retryable");
        failReceipt=true;
        check(failing.Delete(new Dictionary<string,string?>{{second,saved.Path}}),"Deletion journal commits despite later tombstone write failure");
        check(File.Exists(Path.Combine(root,".receipts",".deletions"))&&File.Exists(saved.Path),"Interrupted deletion retains recovery journal and payload ownership");
        ledger=Open(root);check(ledger.Get(second).Kind==MediaDecisionKind.Deleted&&!File.Exists(saved.Path),"Restart replays committed deletion before accepting a retry");
        var thirdSaved=ledger.Accept(third,media,SavePayload);
        File.WriteAllText(Path.Combine(root,".receipts",first+".receipt"),"corrupt decision");
        ledger=Open(root);check(ledger.Get(first).Kind==MediaDecisionKind.Unavailable&&ledger.Get(third).Kind==MediaDecisionKind.Accepted,"Corrupt receipt quarantines only that ID");
        ledger=Open(root);check(ledger.Get(first).Kind==MediaDecisionKind.Unavailable,"Receipt quarantine survives restart");
        string journal=Path.Combine(root,".receipts",".deletions");File.WriteAllText(journal,"corrupt journal");
        check(Open(root).Get(third).Kind==MediaDecisionKind.Unavailable,"Unreadable batch deletion journal fails closed globally");File.Delete(journal);
        var outside=Path.Combine(Path.GetDirectoryName(root)!,Guid.NewGuid().ToString("N")+".dpapi");File.WriteAllBytes(outside,[55]);
        check(!ledger.Delete(new Dictionary<string,string?>{{third,outside}})&&File.Exists(outside),"Media deletion refuses files outside encrypted media root");
        check(ledger.Get(third).Path==thirdSaved.Path,"Refused deletion preserves the accepted media mapping");
        string quotaRoot=Path.Combine(root,"quota");long clock=1000;var quota=Open(quotaRoot,clock:()=>clock,capacity:1);
        var one=quota.Accept(first,media,m=>Save(quotaRoot,m));clock+=8L*86400000;
        check(quota.Accept(second,media,m=>Save(quotaRoot,m)).Kind==MediaDecisionKind.Unavailable&&quota.Get(first).Kind==MediaDecisionKind.Accepted,"Live accepted files retain their receipt beyond TTL and enforce admission capacity");
        File.Delete(one.Path!);quota=Open(quotaRoot,clock:()=>clock,capacity:1);
        check(quota.Accept(second,media,m=>Save(quotaRoot,m)).Kind==MediaDecisionKind.Accepted,"Expired missing-payload receipts release admission capacity");
    }

    private static async Task Sessions(string root,Action<bool,string> check)
    {
        var network=new Dictionary<string,MeshEngine>();var bus=new Queue<(string To,byte[] Bytes)>();
        MeshEngine? aliceForReconnect=null;string? aliceId=null,bobId=null;bool dropBob=false;int senderFrames=0,arrivals=0;long clock=1000;
        Task<int> Send(MeshPacket packet)
        {
            string from=Bytes.Hex(packet.Sender);
            if(packet.Type==0x11&&from==bobId&&dropBob&&packet.Payload.Length==59)return Task.FromResult(1);
            if(packet.Type==0x11&&from==aliceId&&packet.Recipient!=null&&Bytes.Hex(packet.Recipient)==bobId)senderFrames++;
            var targets=packet.Recipient==null?network.Keys.Where(k=>k!=from).ToArray():[Bytes.Hex(packet.Recipient)];
            foreach(string target in targets.Where(network.ContainsKey))foreach(var fragment in FragmentAssembler.Split(packet,512))bus.Enqueue((target,fragment.Encode()));
            return Task.FromResult(targets.Length);
        }
        async Task Pump(){int budget=40000;while(bus.Count>0){if(--budget==0)throw new Exception("Media network relay loop");var frame=bus.Dequeue();await network[frame.To].Receive(frame.Bytes);}}
        async Task Reconnect(Guid epoch){var pending=aliceForReconnect!.PeerLinkReady(bobId!,epoch);await Pump();await pending;await Pump();}
        byte[] bobNoise=RandomNumberGenerator.GetBytes(32),bobSigning=RandomNumberGenerator.GetBytes(32);
        var ledger=Open(root);var timeline=new HashSet<string>();var savedPaths=new Dictionary<string,string>();
        MeshEngine Bob(byte caps)
        {
            var engine=new MeshEngine(bobNoise,bobSigning,"bob",Send){Capabilities=[0x40,caps]};
            engine.AcceptPrivateMedia=(peer,id,media)=>
            {
                arrivals++;var result=ledger.Accept(id,media,m=>Save(root,m));
                if(result.Kind==MediaDecisionKind.Accepted){timeline.Add(id);savedPaths[id]=result.Path!;}
                return Task.FromResult(result.Kind is MediaDecisionKind.Accepted or MediaDecisionKind.Deleted);
            };
            return engine;
        }
        using var alice=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"alice",Send){Capabilities=[0x40,3],MediaClock=()=>clock};
        var bob=Bob(3);using var legacy=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"legacy",Send){Capabilities=[0x40,0]};
        aliceForReconnect=alice;aliceId=alice.Id;bobId=bob.Id;network[alice.Id]=alice;network[bob.Id]=bob;network[legacy.Id]=legacy;
        try
        {
            await alice.Announce();await bob.Announce();await legacy.Announce();await Pump();
            var policy=alice.ResolvePrivateMediaPolicy(bob.Id);check(!policy.IsCompleted&&senderFrames==0,"Public media capability hints cannot pre-queue encrypted media before session proof");await Pump();
            check(await policy==PrivateMediaSendPolicy.Encrypted,"Media capability is proved through the authenticated Noise session");
            var media=new MediaPacket("img_12345678-1234-1234-1234-123456789ABC.jpg","image/jpeg",RandomNumberGenerator.GetBytes(4096));
            string id=PrivateMediaIdentity.StableId(alice.Id,bob.Id,media.Name)!;
            dropBob=true;int framesBefore=senderFrames;await alice.SendMedia(media,bob.Id);await Pump();
            check(timeline.Contains(id)&&arrivals==1,"Encrypted stable media is durably delivered before receipt");
            string path=savedPaths[id];ledger=Open(root);
            Guid epoch=Guid.NewGuid();await Reconnect(epoch);
            check(arrivals==2&&timeline.Count==1&&savedPaths[id]==path,"Reconnect retry after receiver restart preserves one media bubble and file");
            int previousFrames=senderFrames;await Reconnect(epoch);
            check(senderFrames==previousFrames,"Duplicate link announcements do not chain media retries");
            await legacy.SendPrivate(alice.Id,"hello","legacy-auth");await Pump();
            string? receiptPeer=null;alice.Receipt+=(peer,message,status)=>{if(message==id)receiptPeer=peer;};
            await legacy.SendTyped(alice.Id,Bytes.Join([3],Bytes.Utf8(id)));await Pump();
            await Reconnect(Guid.NewGuid());
            check(receiptPeer==legacy.Id&&arrivals==3,"Receipt identity stays peer-bound and a foreign ACK cannot release another peer's media retry");
            await Reconnect(Guid.NewGuid());
            check(arrivals==3,"Automatic whole-media retries are bounded to two attempts per message");
            dropBob=false;await bob.SendTyped(alice.Id,Bytes.Join([3],Bytes.Utf8(id)));await Pump();
            previousFrames=senderFrames;await Reconnect(Guid.NewGuid());
            check(senderFrames==previousFrames,"Authenticated recipient receipt releases retained media");

            var recovered=media with{Name="img_11111111-1234-1234-1234-123456789ABC.jpg"};
            dropBob=true;await alice.SendMedia(recovered,bob.Id);await Pump();int beforeRestart=arrivals;
            bob.Dispose();bob=Bob(3);network[bob.Id]=bob;ledger=Open(root);dropBob=false;
            await alice.Announce();await bob.Announce();await Pump();await Reconnect(Guid.NewGuid());
            check(arrivals==beforeRestart+1&&timeline.Count==2,"Physical reconnect recovers a restarted recipient without requiring an incoming chat message");

            var replacement=media with{Name="img_ABCDEF01-1234-1234-1234-123456789ABC.jpg"};
            int beforeReplacementTimeline=timeline.Count;dropBob=true;await alice.SendMedia(replacement,bob.Id);await Pump();int beforeReplacement=arrivals;
            bob.Dispose();bob=Bob(3);network[bob.Id]=bob;ledger=Open(root);dropBob=false;await alice.Announce();await bob.Announce();await Pump();
            await bob.SendPrivate(alice.Id,"new session","replacement-auth");await Pump();
            check(arrivals==beforeReplacement+1&&timeline.Count==beforeReplacementTimeline+1,"Replacement Noise generation retries only after its authenticated receipt proof");

            var downgraded=media with{Name="img_FFFFFFFF-1234-1234-1234-123456789ABC.jpg"};
            dropBob=true;await alice.SendMedia(downgraded,bob.Id);await Pump();int beforeDowngrade=arrivals;
            bob.Dispose();bob=Bob(1);network[bob.Id]=bob;dropBob=false;await alice.Announce();await bob.Announce();await Pump();
            await bob.SendPrivate(alice.Id,"bit 8 only","no-receipts-auth");await Pump();
            check(arrivals==beforeDowngrade,"Bit-8-only replacement session cannot flush a bit-9 media retry");
            bob.Dispose();bob=Bob(0);network[bob.Id]=bob;await alice.Announce();await bob.Announce();await Pump();
            await bob.SendPrivate(alice.Id,"no media","downgrade-auth");await Pump();
            check(await alice.ResolvePrivateMediaPolicy(bob.Id)==PrivateMediaSendPolicy.BlockedDowngrade,"Previously authenticated media capability pins block downgrade");
            bool blocked=false;try{await alice.SendMedia(media,bob.Id,true);}catch(IOException){blocked=true;}
            check(blocked,"Legacy consent cannot override an authenticated media downgrade");

            int legacyFiles=0;legacy.Media+=(_,_,isPrivate)=>{if(isPrivate)legacyFiles++;};
            blocked=false;try{await alice.SendMedia(media,legacy.Id);}catch(IOException){blocked=true;}
            await Pump();check(blocked&&legacyFiles==0,"Legacy raw media requires explicit consent for this send");
            await alice.SendMedia(media,legacy.Id,true);await Pump();
            check(legacyFiles==1,"Explicit legacy consent sends a signed directed media packet");
            blocked=false;try{await alice.SendMedia(media,legacy.Id);}catch(IOException){blocked=true;}
            check(blocked,"Legacy consent is never retained for subsequent sends");

            bob.Dispose();bob=Bob(3);network[bob.Id]=bob;await alice.Announce();await bob.Announce();await Pump();
            await bob.SendPrivate(alice.Id,"upgraded","upgrade-auth");await Pump();
            dropBob=true;await alice.SendMedia(media with{Name="img_DDDDDDDD-1234-1234-1234-123456789ABC.jpg"},bob.Id);await Pump();
            clock+=120001;await alice.MaintainPrivateMedia();previousFrames=senderFrames;
            await Reconnect(Guid.NewGuid());
            check(senderFrames==previousFrames,"Private media retry retention expires after the bounded horizon");

            dropBob=true;int beforeQuota=arrivals;
            for(int i=0;i<9;i++) {await alice.SendMedia(media with{Name=$"img_{i+1:X8}-1234-1234-1234-123456789ABC.jpg"},bob.Id);await Pump();}
            for(int i=0;i<20;i++){await Reconnect(Guid.NewGuid());}
            check(arrivals==beforeQuota+9+16,"Media retry packet budget retains at most eight packets, each with at most two retries");
            clock+=120001;await alice.MaintainPrivateMedia();beforeQuota=arrivals;
            var large=media with{Content=RandomNumberGenerator.GetBytes(1024*1024)};
            for(int i=0;i<5;i++) {await alice.SendMedia(large with{Name=$"img_{i+100:X8}-1234-1234-1234-123456789ABC.jpg"},bob.Id);await Pump();}
            int beforeReconnect=arrivals;await Reconnect(Guid.NewGuid());
            check(arrivals==beforeReconnect+2,"A reconnect retries at most two retained media files");
            for(int i=0;i<10;i++){await Reconnect(Guid.NewGuid());}
            check(arrivals==beforeQuota+5+8,"Media retry memory budget retains at most four MiB");
            clock+=120001;await alice.MaintainPrivateMedia();

            ledger=Open(root,(path,bytes)=>{if(path.EndsWith(".receipt"))throw new IOException("simulated receipt write failure");PrivateMediaLedger.AtomicWrite(path,bytes);});
            var failed=media with{Name="img_EEEEEEEE-1234-1234-1234-123456789ABC.jpg"};
            string failedId=PrivateMediaIdentity.StableId(alice.Id,bob.Id,failed.Name)!;int beforeFailure=timeline.Count;
            dropBob=false;await alice.SendMedia(failed,bob.Id);await Pump();
            check(timeline.Count==beforeFailure&&!timeline.Contains(failedId),"A failed durable receiver commit withholds UI insertion and ACK");
            ledger=Open(root);await Reconnect(Guid.NewGuid());
            check(timeline.Contains(failedId)&&timeline.Count==beforeFailure+1,"An unacknowledged failed save is retried successfully after reconnect");

            var canceled=media with{Name="img_CCCCCCCC-1234-1234-1234-123456789ABC.jpg"};string canceledId=PrivateMediaIdentity.StableId(alice.Id,bob.Id,canceled.Name)!;
            dropBob=true;await alice.SendMedia(canceled,bob.Id);await Pump();int beforeCancel=arrivals;
            await alice.ForgetPrivateMedia([canceledId]);await Reconnect(Guid.NewGuid());
            check(arrivals==beforeCancel,"Explicit sender removal cancels retained whole-file retries");

            var deleted=media with{Name="img_BBBBBBBB-1234-1234-1234-123456789ABC.jpg"};
            string deletedId=PrivateMediaIdentity.StableId(alice.Id,bob.Id,deleted.Name)!;
            dropBob=true;await alice.SendMedia(deleted,bob.Id);await Pump();
            check(ledger.Delete(new Dictionary<string,string?>{{deletedId,savedPaths[deletedId]}}),"Receiver commits explicit deletion while sender awaits ACK");timeline.Remove(deletedId);
            ledger=Open(root);dropBob=false;await Reconnect(Guid.NewGuid());
            check(!timeline.Contains(deletedId)&&ledger.Get(deletedId).Kind==MediaDecisionKind.Deleted,"A retried deleted media file is ACKed without restoring the bubble");
            beforeReconnect=arrivals;await Reconnect(Guid.NewGuid());
            check(arrivals==beforeReconnect,"Durable deletion ACK releases the sender retry owner");
        }
        finally{bob.Dispose();}
    }
}
