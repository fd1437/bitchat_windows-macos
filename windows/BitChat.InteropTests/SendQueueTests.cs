using BitChat.Core;
using System.Collections.Concurrent;
using System.Security.Cryptography;

internal static class SendQueueTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var queue=new CorrespondentSendQueue();var steps=new ConcurrentQueue<string>();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=queue.Enqueue("alice",async _=>{steps.Enqueue("first");entered.SetResult();await release.Task;throw new IOException("radio failure");});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var canceled=new CancellationTokenSource();
        var second=queue.Enqueue("alice",_=>{steps.Enqueue("must not send");return Task.CompletedTask;},canceled.Token);
        var third=queue.Enqueue("alice",_=>{steps.Enqueue("third");return Task.CompletedTask;});
        await queue.Enqueue("bob",_=>{steps.Enqueue("bob");return Task.CompletedTask;}).WaitAsync(TimeSpan.FromSeconds(2));
        check(!third.IsCompleted&&steps.Contains("bob"),"A stalled correspondent does not block another correspondent and preserves its own file order");
        canceled.Cancel();
        try{await second.WaitAsync(TimeSpan.FromSeconds(2));}catch(OperationCanceledException){}
        check(second.IsCanceled&&!first.IsCompleted,"Canceling a queued file completes immediately even when the previous file is stalled");
        release.SetResult();
        try{await first;}catch(IOException){}
        bool wasCanceled=false;try{await second;}catch(OperationCanceledException){wasCanceled=true;}
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        check(wasCanceled&&!steps.Contains("must not send")&&steps.Last()=="third","A failed file and a canceled queued file do not poison subsequent sends");
        await queue.Enqueue("alice",_=>Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2));
        check(true,"A completed correspondent queue can accept a new batch");

        await Stress(check);
        var transit=new Queue<(bool ToBob,MeshPacket Packet)>();bool blockPrivate=false;
        var privateEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var privateRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var alice=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"alice",async packet=>{if(blockPrivate&&packet.Type==0x11){privateEntered.TrySetResult();await privateRelease.Task;}transit.Enqueue((true,packet));return 1;}){Capabilities=[0x79,0x03]};
        using var bob=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"bob",packet=>{transit.Enqueue((false,packet));return Task.FromResult(1);}){Capabilities=[0x79,0x03]};
        async Task Pump(){int budget=200;while(transit.Count>0){if(--budget==0)throw new Exception("Cancellation handshake loop");var item=transit.Dequeue();await(item.ToBob?bob:alice).Receive(item.Packet.Encode());}}
        await alice.Announce();await bob.Announce();await Pump();
        await alice.SendPrivate(bob.Id,"session proof","cancel-session");await Pump();
        blockPrivate=true;var privateSend=alice.SendPrivate(bob.Id,"stalled text","stall-text");
        await privateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try{await alice.Announce().WaitAsync(TimeSpan.FromSeconds(2));await alice.MaintainPrivateMedia().WaitAsync(TimeSpan.FromSeconds(2));}
        finally{blockPrivate=false;privateRelease.SetResult();await privateSend;}
        check(true,"A stalled private text write does not hold the receive and identity gate");await Pump();
        int transmitted=0;
        var radioEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.SendMediaPacket=async (_,token)=>{transmitted++;radioEntered.SetResult();await Task.Delay(Timeout.Infinite,token);return 1;};
        using var stop=new CancellationTokenSource();
        var sending=alice.SendMedia(new("img_"+Guid.NewGuid().ToString("D")+".jpg","image/jpeg",new byte[4096]),bob.Id,cancellation:stop.Token);
        await radioEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));stop.Cancel();
        bool interrupted=false;try{await sending;}catch(OperationCanceledException){interrupted=true;}
        check(interrupted,"Cancellation reaches the active encrypted media transport");
        alice.SendMediaPacket=(_,_)=>{transmitted++;return Task.FromResult(1);};
        await alice.PeerLinkReady(bob.Id,Guid.NewGuid());await Pump();
        check(transmitted==1,"A canceled private file is not resurrected by reconnection retries");
        using var alreadyCanceled=new CancellationTokenSource();alreadyCanceled.Cancel();
        try{await alice.SendMedia(new("no-send.bin","application/octet-stream",[1]),cancellation:alreadyCanceled.Token);}catch(OperationCanceledException){}
        check(transmitted==1,"A canceled unsent file never enters the radio transport");
    }
    private static async Task Stress(Action<bool,string> check)
    {
        var queue=new CorrespondentSendQueue(2,256);int running=0,peak=0,overlaps=0;
        var inPeer=new ConcurrentDictionary<string,int>();
        var order=new ConcurrentDictionary<string,ConcurrentQueue<int>>();
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        var jobs=new ConcurrentBag<Task>();
        await Task.WhenAll(Enumerable.Range(0,4).Select(peer=>Task.Run(()=>
        {
            string key=peer.ToString();order[key]=new();
            for(int i=0;i<50;i++)
            {
                int index=i;
                jobs.Add(queue.Enqueue(key,async token=>
                {
                    int total=Interlocked.Increment(ref running),local=inPeer.AddOrUpdate(key,1,(_,count)=>count+1);
                    int seen;do{seen=Volatile.Read(ref peak);}while(total>seen&&Interlocked.CompareExchange(ref peak,total,seen)!=seen);
                    if(local>1)Interlocked.Increment(ref overlaps);
                    try{order[key].Enqueue(index);await Task.Delay(1,token);if(index%17==0)throw new IOException("simulated radio error");}
                    finally{inPeer.AddOrUpdate(key,0,(_,count)=>count-1);Interlocked.Decrement(ref running);}
                },index%9==0?canceled.Token:CancellationToken.None));
            }
        })));
        try{await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(10));}catch(Exception error)when(error is IOException or OperationCanceledException){}
        check(jobs.All(job=>job.IsCompleted)&&peak<=2&&overlaps==0,"Two hundred concurrent submissions remain bounded to two transfers and never overlap within a correspondent");
        check(order.Values.All(actual=>actual.SequenceEqual(Enumerable.Range(0,50).Where(i=>i%9!=0))),"FIFO order survives concurrent submissions, errors and cancellations across four correspondents");
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        bool rejected=false;try{_ = queue.Enqueue("later",_=>Task.CompletedTask);}catch(InvalidOperationException){rejected=true;}
        check(rejected,"Shutdown rejects new sends after draining the queue");

        var bounded=new CorrespondentSendQueue(1,2);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activeStop=new CancellationTokenSource();bool secondStarted=false;
        var first=bounded.Enqueue("peer",async _=>{entered.SetResult();await release.Task;},activeStop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second=bounded.Enqueue("peer",_=>{secondStarted=true;return Task.CompletedTask;});
        bool full=false;try{_ = bounded.Enqueue("other",_=>Task.CompletedTask);}catch(IOException){full=true;}
        check(full,"Queue admission rejects overflow without dropping already accepted sends");
        activeStop.Cancel();await Task.Delay(20);
        check(!first.IsCompleted&&!secondStarted,"Canceling an active write does not release its correspondent before the current frame ends");
        release.SetResult();try{await first;}catch(OperationCanceledException){}
        await second.WaitAsync(TimeSpan.FromSeconds(2));await bounded.StopAsync();
        check(secondStarted,"The next queued file starts after a canceled active write actually finishes");

        var stopping=new CorrespondentSendQueue(1,8);
        var onWire=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int unexpected=0;
        var active=stopping.Enqueue("peer",async token=>{onWire.SetResult();await Task.Delay(Timeout.Infinite,token);});
        await onWire.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var waiting=stopping.Enqueue("peer",_=>{unexpected++;return Task.CompletedTask;});
        await stopping.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        try{await active;}catch(OperationCanceledException){}try{await waiting;}catch(OperationCanceledException){}
        check(active.IsCanceled&&waiting.IsCanceled&&unexpected==0,"Shutdown cancels both active and queued sends without starting pending work");
    }

}
