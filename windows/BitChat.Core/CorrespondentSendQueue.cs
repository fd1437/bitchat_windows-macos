namespace BitChat.Core;

// FIFO per correspondent, with bounded concurrency across correspondents.
// Canceling a queued item removes it immediately, without releasing an active
// correspondent's slot before its current radio operation has actually stopped.
public sealed class CorrespondentSendQueue
{
    private sealed class Lane(string key)
    {
        public readonly string Key=key;
        public readonly LinkedList<Work> Pending=new();
        public LinkedListNode<Lane>? Ready;
        public bool Active;
    }
    private sealed class Work(Lane lane,Func<CancellationToken,Task> action,CancellationTokenSource stop)
    {
        public readonly Lane Lane=lane;
        public readonly Func<CancellationToken,Task> Action=action;
        public readonly CancellationTokenSource Stop=stop;
        public readonly SynchronizationContext? Context=SynchronizationContext.Current;
        public readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public LinkedListNode<Work>? Node;
    }
    private readonly object sync=new();
    private readonly Dictionary<string,Lane> lanes=[];
    private readonly LinkedList<Lane> ready=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly TaskCompletionSource drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int concurrency,capacity;
    private int active,count;
    private bool stopped;
    public CorrespondentSendQueue(int maxConcurrent=2,int maxPending=64)
    {
        if(maxConcurrent<1||maxPending<maxConcurrent)throw new ArgumentOutOfRangeException(nameof(maxPending));
        concurrency=maxConcurrent;capacity=maxPending;
    }
    public Task Enqueue(string correspondent,Func<CancellationToken,Task> action,CancellationToken cancellation=default)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock(sync)
        {
            if(stopped)throw new InvalidOperationException("The send queue is stopping");
            if(cancellation.IsCancellationRequested)return Task.FromCanceled(cancellation);
            if(count>=capacity)throw new IOException("The send queue is full; wait for a transfer or cancel a queued file");
            if(!lanes.TryGetValue(correspondent,out var lane))lanes[correspondent]=lane=new(correspondent);
            var work=new Work(lane,action,CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token));
            work.Node=lane.Pending.AddLast(work);count++;
            work.Registration=work.Stop.Token.Register(()=>CancelQueued(work));
            // Cancellation may run synchronously during registration.
            if(work.Node==null){work.Registration.Unregister();work.Stop.Dispose();return work.Completion.Task;}
            MakeReady(lane);Pump();return work.Completion.Task;
        }
    }
    private void MakeReady(Lane lane)
    {if(!stopped&&!lane.Active&&lane.Pending.Count>0&&lane.Ready==null)lane.Ready=ready.AddLast(lane);}
    private void RemoveIdle(Lane lane)
    {
        if(lane.Active||lane.Pending.Count>0)return;
        if(lane.Ready!=null){ready.Remove(lane.Ready);lane.Ready=null;}lanes.Remove(lane.Key);
    }
    private void Pump()
    {
        while(!stopped&&active<concurrency&&ready.First is {} first)
        {
            var lane=first.Value;ready.Remove(first);lane.Ready=null;
            var work=lane.Pending.First!.Value;lane.Pending.RemoveFirst();work.Node=null;
            lane.Active=true;active++;
            if(work.Context is {} context)context.Post(_=>_=Run(work),null);
            else ThreadPool.QueueUserWorkItem(_=>_=Run(work));
        }
    }
    private void CancelQueued(Work work)
    {
        lock(sync)
        {
            if(work.Node==null)return;
            work.Lane.Pending.Remove(work.Node);work.Node=null;count--;
            work.Completion.TrySetCanceled(work.Stop.Token);RemoveIdle(work.Lane);
            if(stopped&&count==0)drained.TrySetResult();
        }
        work.Registration.Unregister();work.Stop.Dispose();
    }
    private async Task Run(Work work)
    {
        try{work.Stop.Token.ThrowIfCancellationRequested();await work.Action(work.Stop.Token);work.Stop.Token.ThrowIfCancellationRequested();work.Completion.TrySetResult();}
        catch(OperationCanceledException) when(work.Stop.IsCancellationRequested){work.Completion.TrySetCanceled(work.Stop.Token);}
        catch(Exception error){work.Completion.TrySetException(error);}
        finally
        {
            work.Registration.Unregister();work.Stop.Dispose();
            lock(sync)
            {
                active--;count--;work.Lane.Active=false;MakeReady(work.Lane);RemoveIdle(work.Lane);Pump();
                if(stopped&&count==0)drained.TrySetResult();
            }
        }
    }
    public Task StopAsync()
    {
        lock(sync){if(stopped)return drained.Task;stopped=true;ready.Clear();foreach(var lane in lanes.Values)lane.Ready=null;if(count==0)drained.TrySetResult();}
        lifetime.Cancel();return drained.Task;
    }
}
