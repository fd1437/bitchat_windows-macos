namespace BitChat.Core;

public static class AttachmentBatch
{
    public static async Task Run<T>(IEnumerable<T> items,Func<T,Task> send,Action<T,Exception?> finished)
    {
        foreach(var item in items)
        {
            Exception? failure=null;
            try{await send(item);}catch(OperationCanceledException){throw;}catch(Exception error){failure=error;}
            finished(item,failure);
        }
    }
}
