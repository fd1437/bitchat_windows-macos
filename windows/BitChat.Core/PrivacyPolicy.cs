namespace BitChat.Core;

public sealed record NotificationContent(string Title,string Body)
{
    public static NotificationContent ForMessage(string sender,string text,bool hidePreviews=true)
        =>hidePreviews?new("BitChat","New message"):new("BitChat · "+sender,text.Length>200?text[..200]:text);
}

public static class LiveVoicePolicy
{
    public static bool CanAutoplay(bool liveEnabled,bool appActive)=>liveEnabled&&appActive;
}
