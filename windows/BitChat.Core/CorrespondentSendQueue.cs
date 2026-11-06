namespace BitChat.Core;

// Independent correspondents never wait for each other's stalled radio writes.
// A failure or a canceled item must not poison the next item in a conversation.
public sealed class CorrespondentSendQueue
{
    private readonly object sync=new();
    private readonly Dictionary<string,Task> tails=[];
    public Task Enqueue(string correspondent,Func<CancellationToken,Task> action,CancellationToken cancellation=default)
    {
        lock(sync)
        {
            Task previous=tails.GetValueOrDefault(correspondent)??Task.CompletedTask;
            Task next=Run(previous,action,cancellation);tails[correspondent]=next;
            _=next.ContinueWith(_=>{lock(sync)if(tails.GetValueOrDefault(correspondent)==next)tails.Remove(correspondent);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            return next;
        }
    }
    private static async Task Run(Task previous,Func<CancellationToken,Task> action,CancellationToken cancellation)
    {
        await Task.Yield();
        try{await previous;}catch(Exception){} // each item's caller observes its own error
        cancellation.ThrowIfCancellationRequested();await action(cancellation);
    }
}
