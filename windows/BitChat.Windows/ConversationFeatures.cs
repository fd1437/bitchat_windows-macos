using System.Windows.Forms;
using System.Text;
using BitChat.Core;

namespace BitChat.Windows;

public sealed partial class AppController
{
    private static readonly string[] ReactionChoices=["👍","❤️","😂","😮","😢","🙏"];
    public event Action<string,bool>? TypingChanged;

    internal static byte? LocalBatteryPercent()
    {
        try
        {
            var power=SystemInformation.PowerStatus;
            if(power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery)||power.BatteryLifePercent<0)return null;
            return (byte)Math.Clamp((int)Math.Round(power.BatteryLifePercent*100),0,100);
        }
        catch{return null;}
    }

    public Task SendTyping(string peer,bool typing)=>Mesh.SendTyped(peer,[0x31,(byte)(typing?1:0)]);

    public async Task ToggleReaction(StoredMessage message,string emoji)
    {
        if(!ReactionChoices.Contains(emoji,StringComparer.Ordinal))throw new ArgumentException("Unsupported reaction");
        string actor=Mesh.Id;
        bool remove=Store.State.Reactions.Any(r=>r.Conversation==message.Conversation&&r.MessageId==message.Id&&r.Actor==actor&&r.Emoji==emoji);
        SetReaction(message.Conversation,message.Id,actor,Store.State.Nickname,emoji,!remove);
        byte[] body=ReactionPayload(message.Conversation,message.Id,emoji,!remove);
        if(message.Conversation.StartsWith("mesh:"))await Mesh.SendTyped(message.Conversation[5..],Bytes.Join([0x30],ReactionBody(message.Id,emoji,!remove)));
        else if(message.Conversation is "mesh" or "board")await Mesh.SendBroadcast(0x2A,body);
    }

    private static byte[] ReactionBody(string id,string emoji,bool add)=>Bytes.Join([1,(byte)(add?1:0)],Announcement.Tlv(1,Bytes.Utf8(id)),Announcement.Tlv(2,Bytes.Utf8(emoji)),Announcement.Tlv(3,Bytes.Utf8("")));
    private static byte[] ReactionPayload(string conversation,string id,string emoji,bool add)=>Bytes.Join([1,(byte)(add?1:0)],Announcement.Tlv(1,Bytes.Utf8(id)),Announcement.Tlv(2,Bytes.Utf8(emoji)),Announcement.Tlv(3,Bytes.Utf8(conversation)));

    private void ReceiveReaction(string peer,byte[] payload,bool encryptedDirect)
    {
        if(payload.Length<3||payload[0]!=1||payload[1]>1)return;
        var fields=new WireReader(payload[2..]);var values=new Dictionary<byte,byte[]>();
        while(fields.Remaining>0){byte type=fields.Byte();if(!values.TryAdd(type,fields.Read(fields.Byte())))return;}
        if(!values.TryGetValue(1,out var idBytes)||!values.TryGetValue(2,out var emojiBytes)||!values.TryGetValue(3,out var conversationBytes))return;
        string id,emoji,conversation;try{id=Bytes.Text(idBytes);emoji=Bytes.Text(emojiBytes);conversation=Bytes.Text(conversationBytes);}catch(DecoderFallbackException){return;}
        if(id.Length is <1 or >128||!ReactionChoices.Contains(emoji,StringComparer.Ordinal))return;
        if(encryptedDirect)
        {
            if(conversationBytes.Length!=0)return;
            conversation="mesh:"+peer;
        }
        else if(conversation is not ("mesh" or "board"))return;
        if(!Store.State.Messages.Any(m=>m.Conversation==conversation&&m.Id==id))return;
        SetReaction(conversation,id,peer,Peers.GetValueOrDefault(peer)?.Nickname??peer,emoji,payload[1]==1);
    }

    private void SetReaction(string conversation,string id,string actor,string nickname,string emoji,bool add)
    {
        Store.State.Reactions.RemoveAll(r=>r.Conversation==conversation&&r.MessageId==id&&r.Actor==actor&&r.Emoji==emoji);
        if(add)Store.State.Reactions.Add(new(conversation,id,actor,nickname,emoji));
        if(Store.State.Reactions.Count>50000)Store.State.Reactions.RemoveRange(0,Store.State.Reactions.Count-50000);
        Store.Save();TimelineChanged?.Invoke();
    }
}
