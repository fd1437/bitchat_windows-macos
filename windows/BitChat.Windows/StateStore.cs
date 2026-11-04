using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BitChat.Core;

namespace BitChat.Windows;

public sealed class AppState
{
    public byte[] NoiseKey{get;set;}=RandomNumberGenerator.GetBytes(32);
    public byte[] SigningSeed{get;set;}=RandomNumberGenerator.GetBytes(32);
    public byte[] NostrKey{get;set;}=Schnorr.GenerateKey();
    public Dictionary<string,byte[]> LocationKeys{get;set;}=[];
    public List<byte[]> GossipArchive{get;set;}=[];
    public Dictionary<uint,LocalPrekey> LocalPrekeys{get;set;}=[];
    public HashSet<string> UsedRemotePrekeys{get;set;}=[];
    public ulong OwnBundleGeneration{get;set;}
    public Dictionary<string,PrekeyBundle> PrekeyBundles{get;set;}=[];
    public Dictionary<string,CarriedEnvelope> CourierSpool{get;set;}=[];
    public HashSet<string> OpenedCourierIds{get;set;}=[];
    public List<PendingPrivate> Outbox{get;set;}=[];
    public Dictionary<string,MeshPeer> KnownPeers{get;set;}=[];
    public Dictionary<string,List<Vouch>> Vouches{get;set;}=[];
    public Dictionary<string,BoardPost> Board{get;set;}=[];
    public Dictionary<string,BoardDeletion> BoardDeletions{get;set;}=[];
    public Dictionary<string,PrivateGroup> Groups{get;set;}=[];
    public string Nickname{get;set;}="anon";
    public string Cell{get;set;}="";
    public List<string> Relays{get;set;}=["wss://relay.damus.io","wss://nos.lol","wss://relay.primal.net"];
    public HashSet<string> Blocked{get;set;}=[];
    public HashSet<string> Favorites{get;set;}=[];
    public Dictionary<string,string> NostrReplyAs{get;set;}=[];
    public Dictionary<string,string> MeshNostrKeys{get;set;}=[];
    public HashSet<string> PrivateMediaCapable{get;set;}=[];
    public HashSet<string> FavoritedBy{get;set;}=[];
    public Dictionary<string,string> NostrPeers{get;set;}=[];
    public Dictionary<string,string> Verified{get;set;}=[];
    public Dictionary<string,byte[]> PinnedSigningKeys{get;set;}=[];
    public List<StoredMessage> Messages{get;set;}=[];
    public bool Teleported{get;set;}=true;
    public bool Gateway{get;set;}=false;
    public bool Bridge{get;set;}=false;
    public bool UseTor{get;set;}=false;
    public bool Dark{get;set;}=true;
    public bool Notifications{get;set;}=true;
    public bool HideNotificationPreviews{get;set;}=true;
    public bool LiveVoice{get;set;}=true;
    public byte[] LocationKey(string cell){if(!LocationKeys.TryGetValue(cell,out var k))LocationKeys[cell]=k=Schnorr.GenerateKey();return k;}
}
public sealed record StoredMessage(string Conversation,string Id,string Sender,string Text,long Timestamp,bool Own=false,string Status="received",string? Attachment=null,string? Mime=null,string? Peer=null);

public sealed class StateStore
{
    internal static string? OverrideRoot;
    public static string Root=>OverrideRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BitChat");
    private static string FilePath=>Path.Combine(Root,"state.dpapi");
    private readonly object sync=new();
    public AppState State{get;private set;}
    public StateStore()
    {
        Directory.CreateDirectory(Root);
        if(File.Exists(FilePath))
        {
            byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(FilePath),null,DataProtectionScope.CurrentUser);
            try{State=JsonSerializer.Deserialize<AppState>(plain)??throw new InvalidDataException("Invalid saved BitChat state");}finally{CryptographicOperations.ZeroMemory(plain);}
        }
        else{State=new();Save();}
    }
    public void Save()
    {
        lock(sync)
        {
            if(State.Messages.Count>20000)State.Messages.RemoveRange(0,State.Messages.Count-20000);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(State);byte[] encrypted;
            try{encrypted=ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);}finally{CryptographicOperations.ZeroMemory(bytes);}
            var tmp=FilePath+".tmp";File.WriteAllBytes(tmp,encrypted);File.Move(tmp,FilePath,true);
        }
    }
    public string SaveMedia(MediaPacket media)
    {
        string dir=Path.Combine(Root,"media");Directory.CreateDirectory(dir);
        // Keep file contents DPAPI-encrypted; only decrypt on explicit export/play.
        string file=Path.Combine(dir,Guid.NewGuid().ToString("N")+".dpapi");PrivateMediaLedger.AtomicWrite(file,ProtectedData.Protect(media.Content,null,DataProtectionScope.CurrentUser));return file;
    }
    public byte[] ReadMedia(string path)
    {if(!Path.GetFullPath(path).StartsWith(Path.Combine(Root,"media")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid attachment path");return ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);}
    public void RemoveMedia(string path)
    {
        string full=Path.GetFullPath(path),parent=Path.GetDirectoryName(full)!;
        if(!parent.Equals(Path.Combine(Root,"media"),StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileNameWithoutExtension(full),"N",out _))throw new InvalidDataException("Invalid attachment path");
        File.Delete(full);
    }
    public void Wipe()
    {
        lock(sync)
        {
            CryptographicOperations.ZeroMemory(State.NoiseKey);CryptographicOperations.ZeroMemory(State.SigningSeed);CryptographicOperations.ZeroMemory(State.NostrKey);foreach(var k in State.LocationKeys.Values)CryptographicOperations.ZeroMemory(k);
            if(Directory.Exists(Path.Combine(Root,"media")))Directory.Delete(Path.Combine(Root,"media"),true);
            State=new();Save();
        }
    }
}
