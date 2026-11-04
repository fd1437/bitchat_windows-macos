using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using BitChat.Core;
namespace BitChat.Windows;

public sealed partial class AppController
{
    private VoiceRecorder? recorder;private string? recordingConversation;private byte[]? recordingBurst;private ushort voiceSequence;
    private Channel<VoicePacket>? voicePackets;private Task? voiceSender;private long voiceStarted;
    private sealed class InboundBurst(string peer,string conversation,byte[] id)
    {
        public string Peer=peer,Conversation=conversation;public byte[] Id=id;public long Started=Bytes.Now;public int Size;public ushort Next=1;
        public SortedDictionary<ushort,byte[][]> Frames=[];public VoicePlayback? Playback;
    }
    private readonly Dictionary<string,InboundBurst> voiceBursts=[];
    private readonly HashSet<VoicePlayback> voicePlaybacks=[];
    public bool IsRecording=>recorder!=null;
    public async Task BeginVoice(string conversation,bool live)
    {
        live=live&&Store.State.LiveVoice;
        if(Bluetooth.Connections==0)throw new IOException("No Bluetooth link is available for voice");
        if(recorder!=null)throw new InvalidOperationException("A recording is already active");if(conversation!="mesh"&&!conversation.StartsWith("mesh:"))throw new InvalidOperationException("Voice recording is available in Bluetooth mesh chats");
        recordingConversation=conversation;voiceStarted=Bytes.Now;voiceSequence=1;recorder=new();
        try
        {
            if(live)
            {
                recordingBurst=RandomNumberGenerator.GetBytes(8);voicePackets=Channel.CreateBounded<VoicePacket>(128);var channel=voicePackets;
                voiceSender=Task.Run(async()=>{await foreach(var packet in channel.Reader.ReadAllAsync()){if(conversation=="mesh")await Mesh.SendBroadcast(0x29,packet.Encode());else await Mesh.SendTyped(conversation[5..],Bytes.Join([8],packet.Encode()));}});
                await voicePackets.Writer.WriteAsync(new(recordingBurst,0,1,[1]));
                recorder.Frame+=frame=>{if(!channel.Writer.TryWrite(new(recordingBurst,voiceSequence++,0,Bytes.Join(Bytes.U16((ushort)frame.Length),frame))))Status?.Invoke("Voice network queue is full");};
            }
            recorder.Error+=error=>Post(()=>{Status?.Invoke(error);_=EndVoice(false);});recorder.Start();Status?.Invoke(live?"Live voice · release to stop":"Recording voice note · click stop to send");
        }
        catch{await recorder.DisposeAsync();recorder=null;voicePackets?.Writer.TryComplete();recordingBurst=null;throw;}
    }
    public async Task EndVoice(bool cancel)
    {
        if(recorder==null)return;var capture=recorder;recorder=null;var conversation=recordingConversation!;
        try
        {
            var note=await capture.Stop();
            if(recordingBurst!=null&&voicePackets!=null)
            {
                var end=cancel?new VoicePacket(recordingBurst,voiceSequence,4,[]):new VoicePacket(recordingBurst,voiceSequence,2,Bytes.Join(Bytes.U16((ushort)(voiceSequence-1)),Bytes.U32((uint)(Bytes.Now-voiceStarted))));
                await voicePackets.Writer.WriteAsync(end);voicePackets.Writer.TryComplete();if(voiceSender!=null)await voiceSender.WaitAsync(TimeSpan.FromSeconds(30));
                if(!cancel){note=note with{Name="voice_"+Bytes.Hex(recordingBurst)+".m4a"};await SendAttachment(conversation,note);}
            }
            else if(!cancel)
            {
                await SendAttachment(conversation,note);
            }
            Status?.Invoke(cancel?"Voice recording canceled":"Voice recording sent");
        }
        catch(Exception e){Status?.Invoke("Voice recording: "+e.Message);}
        finally{await capture.DisposeAsync();voicePackets?.Writer.TryComplete();voicePackets=null;voiceSender=null;recordingBurst=null;recordingConversation=null;}
    }
    private void ReceiveVoice(string peer,byte[] bytes,bool isPrivate)
    {
        var packet=VoicePacket.Decode(bytes);string key=peer+":"+Bytes.Hex(packet.Burst);
        if(packet.Flags==1)
        {
            if(voiceBursts.ContainsKey(key))return;if(voiceBursts.Count>=8)throw new InvalidDataException("Concurrent voice burst limit");
            var burst=new InboundBurst(peer,isPrivate?"mesh:"+peer:"mesh",packet.Burst);voiceBursts[key]=burst;
            try{if(LiveVoicePolicy.CanAutoplay(Store.State.LiveVoice,IsAppActive())){burst.Playback=new VoicePlayback();voicePlaybacks.Add(burst.Playback);}}catch(Exception e){Status?.Invoke("Audio playback unavailable: "+e.Message);}Status?.Invoke("Live voice from "+(Peers.GetValueOrDefault(peer)?.Nickname??peer));return;
        }
        if(!voiceBursts.TryGetValue(key,out var active))return;
        if(packet.Flags==4){voiceBursts.Remove(key);if(active.Playback!=null){voicePlaybacks.Remove(active.Playback);_=active.Playback.DisposeAsync();}return;}
        if(packet.Flags==2){voiceBursts.Remove(key);_=FinishVoice(active);return;}
        if(!LiveVoicePolicy.CanAutoplay(Store.State.LiveVoice,IsAppActive()))_=StopVoiceAutoplay();
        var frames=packet.Frames();int size=frames.Sum(f=>f.Length+7);
        if(active.Size+size>384*1024||Bytes.Now-active.Started>130000){voiceBursts.Remove(key);if(active.Playback!=null){voicePlaybacks.Remove(active.Playback);_=active.Playback.DisposeAsync();}return;}
        if(!active.Frames.TryAdd(packet.Sequence,frames))return;active.Size+=size;
        while(active.Frames.TryGetValue(active.Next,out var ready)){foreach(var raw in ready)active.Playback?.Add(raw);active.Next++;}
    }
    public async Task StopVoiceAutoplay()
    {
        var playback=voicePlaybacks.ToArray();voicePlaybacks.Clear();foreach(var burst in voiceBursts.Values)burst.Playback=null;
        foreach(var player in playback)try{await player.DisposeAsync();}catch(Exception e){Status?.Invoke("Voice playback: "+e.Message);}
    }
    private async Task FinishVoice(InboundBurst burst)
    {
        try
        {
            if(burst.Playback!=null)try{await burst.Playback.Complete();}catch(Exception e){Status?.Invoke("Voice playback: "+e.Message);}if(disposed)return;
            var audio=Bytes.Join(burst.Frames.Values.SelectMany(frames=>frames.Select(VoicePacket.Adts)).ToArray());if(audio.Length==0)return;
            var note=await AudioProcess.VoiceNote(audio,Bytes.Hex(burst.Id));
            if(burst.Conversation.StartsWith("mesh:"))
            {
                string id=PrivateMediaIdentity.StableId(burst.Peer,Mesh.Id,note.Name)!;
                Post(()=>ReceiveLiveMedia(burst.Peer,burst.Conversation,id,note));
            }
            else Post(()=>{if(!Store.State.Messages.Any(m=>m.Conversation=="mesh"&&m.Id==Bytes.Hex(burst.Id)))ReceiveRawMedia(burst.Peer,note,false);});
        }
        catch(Exception e){Status?.Invoke("Voice playback: "+e.Message);}
        finally{if(burst.Playback!=null){voicePlaybacks.Remove(burst.Playback);await burst.Playback.DisposeAsync();}}
    }
    private void ExpireVoice()
    {foreach(var key in voiceBursts.Where(p=>Bytes.Now-p.Value.Started>130000).Select(p=>p.Key).ToArray()){var burst=voiceBursts[key];voiceBursts.Remove(key);_=FinishVoice(burst);}}
    private async Task DisposeVoice()
    {await EndVoice(true);await StopVoiceAutoplay();voiceBursts.Clear();}
}
