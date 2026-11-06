using BitChat.Core;
using System.Text.Json;
using System.Security.Cryptography;

int passed=0;
void Check(bool result,string name){if(!result)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);passed++;}
byte[] Hex(string s)=>Convert.FromHexString(s);
var vectors=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"NoiseTestVectors.json"))).RootElement;
foreach(var v in vectors.EnumerateArray())
{
    byte[] Field(string key)=>Hex(v.GetProperty(key).GetString()!);
    using var init=new NoiseHandshake(true,Field("init_static"),Field("init_prologue"),Field("init_ephemeral"));
    using var resp=new NoiseHandshake(false,Field("resp_static"),Field("resp_prologue"),Field("resp_ephemeral"));
    var messages=v.GetProperty("messages").EnumerateArray().ToArray();
    for(int i=0;i<3;i++)
    {
        var payload=Hex(messages[i].GetProperty("payload").GetString()!);var expected=Hex(messages[i].GetProperty("ciphertext").GetString()!);
        var writer=i%2==0?init:resp;var reader=i%2==0?resp:init;
        var actual=writer.Write(payload);Check(actual.SequenceEqual(expected),$"Noise reference handshake frame {i+1}");
        Check(reader.Read(actual).SequenceEqual(payload),$"Noise reference read {i+1}");
    }
    if(v.TryGetProperty("handshake_hash",out var h))Check(Bytes.Hex(init.HandshakeHash)==h.GetString(),"Noise handshake hash");
    var a=init.Split(false);var b=resp.Split(false);
    for(int i=3;i<messages.Length;i++)
    {
        var plain=Hex(messages[i].GetProperty("payload").GetString()!);var expected=Hex(messages[i].GetProperty("ciphertext").GetString()!);
        var writer=i%2==1?b.Send:a.Send;var reader=i%2==1?a.Receive:b.Receive;
        var actual=writer.Encrypt(plain);Check(actual.SequenceEqual(expected),$"Noise reference transport frame {i+1}");Check(reader.Decrypt(actual).SequenceEqual(plain),"Noise reference transport decrypt");
    }
}
var fixtures=Path.Combine(AppContext.BaseDirectory,"Fixtures");
foreach(var (file,keyFile,keyProp) in new[]{("LegacyPrivateEnvelope733098bb.json","LegacyPrivateEnvelope733098bbRecipientKey.json","recipient_private_key"),("AndroidLegacyPrivateEnvelopeB7f0b33d.json","AndroidLegacyPrivateEnvelopeB7f0b33dMetadata.json","recipient_private_key")})
{
    var e=NostrEvent.Parse(File.ReadAllText(Path.Combine(fixtures,file)));Check(e.Valid(),file+" Schnorr signature");
    var key=Hex(JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures,keyFile))).RootElement.GetProperty(keyProp).GetString()!);
    var rumor=PrivateEnvelope.Open(e,key);Check(rumor.content.Length>0,file+" authenticated decryption");Console.WriteLine("Fixture plaintext: "+rumor.content);
}
var s1=Schnorr.GenerateKey();var s2=Schnorr.GenerateKey();var wrap=PrivateEnvelope.Wrap("Windows ↔ iOS",s1,Bytes.Hex(Schnorr.Public(s2)));
Check(PrivateEnvelope.Open(wrap,s2).content=="Windows ↔ iOS","Private envelope roundtrip");
var announce=new Announcement("battery peer",RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),BatteryPercent:73);
Check(Announcement.Decode(announce.Encode()).BatteryPercent==73,"Optional battery TLV round-trips");
Check(Announcement.Decode(Bytes.Join(announce.Encode(),Announcement.Tlv(0xFE,[1,2,3]))).BatteryPercent==73,"Unknown announce TLVs stay forward-compatible with battery sharing");
try{_ = new Announcement("invalid",new byte[32],new byte[32],BatteryPercent:101).Encode();Check(false,"Invalid battery percentages rejected");}catch(InvalidDataException){Check(true,"Invalid battery percentages rejected");}
var cipher1=new NoiseCipher(RandomNumberGenerator.GetBytes(32),true);
var keyReplay=RandomNumberGenerator.GetBytes(32);using var tx=new NoiseCipher(keyReplay.ToArray(),true);using var rx=new NoiseCipher(keyReplay.ToArray(),true);var encrypted=tx.Encrypt(Bytes.Utf8("hello"));rx.Decrypt(encrypted);
try{rx.Decrypt(encrypted);Check(false,"Replay rejected");}catch(CryptographicException){Check(true,"Replay rejected");}
Console.WriteLine($"{passed} interoperability/security checks passed.");

foreach(var f in JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"apple-compression.json"))).RootElement.EnumerateArray())
{
 var raw=Convert.FromBase64String(f.GetProperty("raw").GetString()!);var expected=Convert.FromBase64String(f.GetProperty("deflate").GetString()!);
 foreach(var level in new[]{System.IO.Compression.CompressionLevel.Optimal,System.IO.Compression.CompressionLevel.SmallestSize})
 {using var stream=new MemoryStream();using(var z=new System.IO.Compression.DeflateStream(stream,level,true))z.Write(raw);Console.WriteLine($"Apple compression {raw.Length} {level}: {stream.ToArray().SequenceEqual(expected)}");}
}

var reference=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"swift-reference.json"))).RootElement;
var seed=Convert.FromBase64String(reference.GetProperty("seed").GetString()!);
foreach(var item in reference.GetProperty("packets").EnumerateArray())
{
 var wire=Convert.FromBase64String(item.GetProperty("wire").GetString()!);var canonical=Convert.FromBase64String(item.GetProperty("signing").GetString()!);
 var packet=MeshPacket.Decode(wire);Check(packet.SigningBytes().SequenceEqual(canonical),$"Actual Swift canonical signing bytes ({packet.Payload.Length} bytes)");
 Check(Crypto.VerifyEd(Crypto.PublicEd(seed),packet.SigningBytes(),packet.Signature!),"Actual Swift signed packet verification");Check(packet.Encode().SequenceEqual(wire),"Actual Swift packet re-encoding");
}
var groupBytes=Convert.FromBase64String(reference.GetProperty("group_state").GetString()!);var group=PrivateGroup.Decode(groupBytes);
Check(group.Valid(),"Actual Swift group creator signature");Check(group.Encode().SequenceEqual(groupBytes),"Actual Swift group state re-encoding");
var groupMessage=group.Open(Convert.FromBase64String(reference.GetProperty("group_message").GetString()!));Check(groupMessage.Content=="bonjour Windows ↔ iOS","Actual Swift group message decryption and author signature");
Console.WriteLine($"TOTAL {passed} checks passed");

var transit=new Queue<(bool ToB,byte[] Data)>();
Task<int> Link(bool toB,MeshPacket p){foreach(var fragment in FragmentAssembler.Split(p,128))transit.Enqueue((toB,fragment.Encode()));return Task.FromResult(1);}
using var alice=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"alice",p=>Link(true,p));
using var bob=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"bob",p=>Link(false,p));
var received=new List<ChatMessage>();var statuses=new List<string>();MediaPacket? receivedMedia=null;byte? peerBattery=null;
bob.Message+=received.Add;bob.Media+=(_,media,_)=>receivedMedia=media;alice.Receipt+=(_,_,status)=>statuses.Add(status);bob.PeerChanged+=peer=>{if(peer.Id==alice.Id)peerBattery=peer.BatteryPercent;};alice.BatteryPercent=()=>83;
alice.Status+=s=>Console.WriteLine("Alice: "+s);bob.Status+=s=>Console.WriteLine("Bob: "+s);
async Task Pump(){int budget=10000;while(transit.Count>0){if(--budget==0)throw new Exception("Mesh relay loop");var m=transit.Dequeue();await(m.ToB?bob:alice).Receive(m.Data);}}
await alice.Announce();await bob.Announce();await Pump();
Check(peerBattery==83,"Signed peer announcements expose an opt-in battery percentage");
alice.BatteryPercent=()=>null;await alice.Announce();await Pump();Check(peerBattery==null,"Turning off battery sharing clears the published percentage");
await alice.SendPrivate(bob.Id,"hello from Windows", "interop-dm-1");await Pump();
Check(received.Any(m=>m.Id=="interop-dm-1"&&m.Content=="hello from Windows"),"Two-client Noise session and private-message delivery");
Check(statuses.Contains("delivered"),"Two-client encrypted delivery receipt");
var attachment=RandomNumberGenerator.GetBytes(4096);await alice.SendMedia(new("reference.bin","application/octet-stream",attachment),bob.Id);await Pump();
Check(receivedMedia!=null&&receivedMedia.Content.SequenceEqual(attachment),"Encrypted media fragmentation/reassembly across 128-byte link");
var hash=Crypto.Hash(Bytes.Utf8("tampered event"));Check(!Schnorr.Verify(Schnorr.Public(s1),hash,Convert.FromHexString(wrap.sig!)),"Forged Nostr event rejected");
var changed=group with{Name="tampered group"};Check(!changed.Valid(),"Tampered group name rejected");
var before=received.Count;var fake=new MeshPacket(2,alice.PeerId,Bytes.Utf8("forged public message"),Timestamp:(ulong)Bytes.Now,Signature:new byte[64]);await bob.Receive(fake.Encode());Check(received.Count==before,"Forged public mesh message rejected");
Console.WriteLine($"TOTAL {passed} checks passed");
var courier=reference.GetProperty("courier");byte[] C(string name)=>Convert.FromBase64String(courier.GetProperty(name).GetString()!);
using(var oneWay=new NoiseHandshake(false,C("recipient_secret"),Bytes.Utf8("bitchat-courier-v1"),oneWay:true))
{var clear=oneWay.Read(C("ciphertext"));Check(clear.SequenceEqual(C("payload")),"Actual Swift Noise X courier decryption");Check(oneWay.RemoteStatic!.SequenceEqual(Crypto.PublicX(C("sender_secret"))),"Actual Swift Noise X sender authentication");}
using(var oneWay=new NoiseHandshake(true,C("sender_secret"),Bytes.Utf8("bitchat-courier-v1"),Enumerable.Repeat((byte)0x22,32).ToArray(),true,Crypto.PublicX(C("recipient_secret"))))
Check(oneWay.Write(C("payload")).SequenceEqual(C("ciphertext")),"Actual Swift Noise X ciphertext re-encoding");
var courierEnvelope=CourierEnvelope.Seal(Bytes.Utf8("offline message"),C("sender_secret"),Crypto.PublicX(C("recipient_secret")));
Check(courierEnvelope.Open(C("recipient_secret")).Payload.SequenceEqual(Bytes.Utf8("offline message")),"Courier envelope roundtrip");
var ids=Enumerable.Range(0,100).Select(i=>Crypto.Hash(BitConverter.GetBytes(i))[..16]).ToArray();var filter=GossipFilter.Build(ids);var values=filter.Values();Check(ids.All(id=>values.Contains(GossipFilter.Bucket(id,filter.M))),"Gossip membership has no false negatives");
var embedded=NostrEmbedded.Decode(NostrEmbedded.Message("Nostr private", "nostr-message-id"));var pm=NostrEmbedded.PrivateMessage(embedded.Payload[1..]);Check(pm.Text=="Nostr private"&&pm.Id=="nostr-message-id"&&embedded.Recipient==null,"Nostr embedded private-message framing");
var npub=Bech32.Encode("npub",Schnorr.Public(s1));Check(Bech32.Decode(npub,"npub").SequenceEqual(Schnorr.Public(s1)),"Nostr npub encoding/checksum");
var verification=VerificationQr.Create(C("sender_secret"),seed,"alice",npub);Check(VerificationQr.Parse(verification.Url()).Noise==verification.Noise,"QR identity signature and freshness");
Console.WriteLine($"TOTAL {passed} checks passed");
var firstWire=new MeshPacket(2,new byte[]{1,2,3,4,5,6,7,8},Bytes.Utf8("first"),Timestamp:1000).Encode();
var secondWire=new MeshPacket(2,new byte[]{8,7,6,5,4,3,2,1},Bytes.Utf8("second"),Timestamp:2000).Encode();var concatenated=Bytes.Join(firstWire,secondWire);var packetStream=new PacketStream();
Check(packetStream.Add(concatenated).Count()==2,"BLE stream preserves consecutive unpadded frames");
var chunked=new PacketStream();var streamFrames=new List<byte[]>();for(int i=0;i<concatenated.Length;i+=7)streamFrames.AddRange(chunked.Add(concatenated.Skip(i).Take(7).ToArray()));Check(streamFrames.Count==2,"BLE stream reassembles seven-byte notification chunks");
var referenceGcs=reference.GetProperty("gcs");var referenceIds=Enumerable.Range(0,100).Select(i=>Crypto.Hash(Bytes.Utf8("id"+i))[..16]).ToArray();var referenceFilter=GossipFilter.Build(referenceIds);
Check(referenceFilter.P==referenceGcs.GetProperty("p").GetInt32()&&referenceFilter.M==referenceGcs.GetProperty("m").GetUInt32()&&Convert.ToBase64String(referenceFilter.Data)==referenceGcs.GetProperty("data").GetString(),"Actual Swift gossip filter bytes");
var board=BoardPost.Decode(Convert.FromBase64String(reference.GetProperty("board").GetString()!),1793740000001);Check(board.Content=="Swift board reference"&&board.Flags==1,"Actual Swift board signature and urgent flag");Check(Convert.ToBase64String(board.Encode())==reference.GetProperty("board").GetString(),"Actual Swift board re-encoding");
var fakeCashu="cashuA"+Convert.ToBase64String(Bytes.Utf8("{\"token\":[{\"mint\":\"https://mint.example\",\"proofs\":[{\"amount\":5}]}]}")).TrimEnd('=').Replace('+','-').Replace('/','_');Check(Cashu.Decode(fakeCashu).Amount==5,"Cashu JSON amount parsing (synthetic token)");
Console.WriteLine($"TOTAL {passed} checks passed");
foreach(var identity in reference.GetProperty("private_media_identities").EnumerateArray())
{
    string? expected=identity.GetProperty("id").ValueKind==System.Text.Json.JsonValueKind.Null?null:identity.GetProperty("id").GetString();
    Check(PrivateMediaIdentity.StableId(identity.GetProperty("sender").GetString()!,identity.GetProperty("recipient").GetString()!,identity.GetProperty("name").GetString()!)==expected,"Actual Swift private-media stable identity: "+identity.GetProperty("name").GetString());
}
await PrivateMediaTests.Run(Check);
Console.WriteLine($"TOTAL {passed} checks passed");

var hiddenNotice=NotificationContent.ForMessage("alice @ u09tv", "secret body #u09tv");
Check(hiddenNotice.Title=="BitChat"&&hiddenNotice.Body=="New message","Notification previews hide sender, body and geohash by default");
var visibleNotice=NotificationContent.ForMessage("alice",new string('x',300),false);
Check(visibleNotice.Title.Contains("alice")&&visibleNotice.Body.Length==200,"Explicit notification previews show sender and bounded body");
Check(!LiveVoicePolicy.CanAutoplay(true,false)&&!LiveVoicePolicy.CanAutoplay(false,true)&&LiveVoicePolicy.CanAutoplay(true,true),"Live voice autoplay requires both explicit preference and foreground activity");
Console.WriteLine($"TOTAL {passed} checks passed");

await BridgeCourierTests.Run(Check,reference);
Console.WriteLine($"TOTAL {passed} checks passed");

foreach(int attPayload in new[]{20,61,182,244,512})
{
    byte[] content=RandomNumberGenerator.GetBytes(65536);
    var original=new MeshPacket(0x11,new byte[]{1,2,3,4,5,6,7,8},content,new byte[]{8,7,6,5,4,3,2,1},Version:2,Timestamp:(ulong)Bytes.Now);
    var stream=new PacketStream();var assembly=new FragmentAssembler();MeshPacket? restored=null;bool bounded=true;
    foreach(var fragment in FragmentAssembler.Split(original,512))
        foreach(var chunk in BleFraming.Chunks(fragment.Encode(fragment.Type is 0x10 or 0x11),attPayload))
        {
            bounded&=chunk.Length<=attPayload;
            foreach(var frame in stream.Add(chunk))restored=assembly.Add(MeshPacket.Decode(frame))??restored;
        }
    Check(bounded&&restored!=null&&restored.Payload.SequenceEqual(content),$"BLE notification stream survives negotiated {attPayload}-byte ATT payloads");
}
using(var offlineMesh=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"offline",_=>Task.FromResult(0)))
{
    bool failed=false;try{await offlineMesh.SendMedia(new("test.bin","application/octet-stream",[1,2,3]));}catch(IOException){failed=true;}
    Check(failed,"Attachment with no accepting Bluetooth link reports failure");
}
Console.WriteLine($"TOTAL {passed} checks passed");

{
    var packet=new MeshPacket(0x11,new byte[]{1,2,3,4,5,6,7,8},RandomNumberGenerator.GetBytes(4096),Version:2,Timestamp:(ulong)Bytes.Now);
    var fragmentsForTiming=FragmentAssembler.Split(packet,512).ToArray();long clock=1000;
    var activeAssembly=new FragmentAssembler(()=>clock);MeshPacket? completed=null;
    foreach(var fragment in fragmentsForTiming){completed=activeAssembly.Add(fragment)??completed;clock+=20000;}
    Check(completed!=null&&completed.Payload.SequenceEqual(packet.Payload),"Active file assembly survives more than one minute of steady progress");
    clock=1000;var idleAssembly=new FragmentAssembler(()=>clock);idleAssembly.Add(fragmentsForTiming[0]);clock+=61000;completed=null;
    foreach(var fragment in fragmentsForTiming.Skip(1))completed=idleAssembly.Add(fragment)??completed;
    Check(completed==null,"Abandoned file assembly still expires after inactivity");
}
Console.WriteLine($"TOTAL {passed} checks passed");

foreach(var writeReference in reference.GetProperty("central_write_offsets").EnumerateArray())
{
    byte[] wire=Convert.FromBase64String(writeReference.GetProperty("wire").GetString()!);
    int mtu=writeReference.GetProperty("att_payload").GetInt32();
    Check(writeReference.GetProperty("accepted_offsets").GetBoolean()
        &&writeReference.GetProperty("accepted_offset_zero").GetBoolean()==(wire.Length<=mtu)
        &&MeshPacket.Decode(wire).Payload.SequenceEqual(Convert.FromBase64String(writeReference.GetProperty("payload").GetString()!)),
        $"Actual Swift central-write receiver requires offsets for split {mtu}-byte writes");
}
Console.WriteLine($"TOTAL {passed} checks passed");

foreach(int attPayload in new[]{64,128})
{
    var file=new MeshPacket(0x11,new byte[]{1,2,3,4,5,6,7,8},RandomNumberGenerator.GetBytes(1024*1024),new byte[]{8,7,6,5,4,3,2,1},Version:2,Timestamp:(ulong)Bytes.Now);
    int count=FragmentAssembler.Split(file,BleFraming.CentralFrameLimit(attPayload)).Count();
    Check(count<=8192,$"One-MiB central transfer stays within receiver fragment quota on {attPayload}-byte ATT link");
}
Console.WriteLine($"TOTAL {passed} checks passed");

foreach(var imageReference in reference.GetProperty("image_files").EnumerateArray())
{
    byte[] expected=Convert.FromBase64String(imageReference.GetProperty("content").GetString()!);
    var decodedImage=MediaPacket.Decode(Convert.FromBase64String(imageReference.GetProperty("file").GetString()!));
    Check(decodedImage.Content.SequenceEqual(expected)&&decodedImage.Mime=="image/png","Actual Swift image file payload: "+imageReference.GetProperty("format").GetString());
    using var receiver=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"Windows",_=>Task.FromResult(1));
    byte[] publicNoise=Convert.FromBase64String(imageReference.GetProperty("noise_public").GetString()!);string imagePeer=Bytes.Hex(Crypto.Hash(publicNoise)[..8]);
    receiver.RestorePeer(new(imagePeer,"Swift image sender",publicNoise,Crypto.PublicEd(Convert.FromBase64String(reference.GetProperty("seed").GetString()!)),[0],0));
    receiver.RestorePinnedKey(imagePeer,Crypto.PublicEd(seed));
    MediaPacket? receivedImage=null;receiver.Media+=(_,file,_)=>receivedImage=file;
    var signedImage=MeshPacket.Decode(Convert.FromBase64String(imageReference.GetProperty("wire").GetString()!));
    foreach(var fragment in FragmentAssembler.Split(signedImage,128))await receiver.Receive(fragment.Encode());
    Check(receivedImage?.Content.SequenceEqual(expected)==true,"Fragmented signed Swift image reaches Windows media receiver: "+imageReference.GetProperty("format").GetString());
}
foreach(byte[] legacyContent in new byte[][]{[],[42],[42,43]})
{
    var encodedLegacy=Bytes.Join([4],Bytes.U16((ushort)legacyContent.Length),legacyContent);
    Check(MediaPacket.Decode(encodedLegacy).Content.SequenceEqual(legacyContent),"Legacy image/file content length edge: "+legacyContent.Length);
}
Console.WriteLine($"TOTAL {passed} checks passed");

// Messages can race the sender's identity announcement on independent BLE links.
var recoveryPackets=new List<MeshPacket>();
var recoveryTransit=new Queue<(bool ToReceiver,MeshPacket Packet)>();
using var recoverySender=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"Mac peer",packet=>{recoveryPackets.Add(packet);recoveryTransit.Enqueue((true,packet));return Task.FromResult(1);});
using var recoveryReceiver=new MeshEngine(RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32),"Windows peer",packet=>{recoveryTransit.Enqueue((false,packet));return Task.FromResult(1);});
var recoveredMessages=new List<ChatMessage>();recoveryReceiver.Message+=recoveredMessages.Add;
await recoverySender.SendPublic("arrived before announcement");var early=recoveryPackets[^1];
await recoveryReceiver.Receive(early.Encode());await recoveryReceiver.Receive(early.Encode());
Check(recoveredMessages.Count==0,"Messages from an unknown identity are not exposed before authentication");
await recoverySender.Announce();var identityPacket=recoveryPackets[^1];
await recoveryReceiver.Receive((identityPacket with{Signature=new byte[64]}).Encode());
await recoveryReceiver.Receive(identityPacket.Encode());
Check(recoveredMessages.Count==0,"A self-signed announcement alone does not prove ownership of the claimed Noise identity");
await recoveryReceiver.Announce();int recoveryBudget=200;while(recoveryTransit.Count>0){if(--recoveryBudget==0)throw new Exception("Recovery negotiation loop");var item=recoveryTransit.Dequeue();await(item.ToReceiver?recoveryReceiver:recoverySender).Receive(item.Packet.Encode());}
Check(recoveredMessages.Count==1&&recoveredMessages[0].Content=="arrived before announcement","Noise-bound identity proof recovers an early Mac message exactly once");
await recoverySender.SendPublic("valid packet after forged copy");var later=recoveryPackets[^1];
await recoveryReceiver.Receive((later with{Signature=new byte[64]}).Encode());await recoveryReceiver.Receive(later.Encode());
Check(recoveredMessages.Count==2,"An invalid signature cannot suppress the matching authentic message via dedup");
await recoveryReceiver.Receive(early.Encode());
Check(recoveredMessages.Count==2,"Recovered messages remain deduplicated on subsequent BLE retries");
Console.WriteLine($"TOTAL {passed} checks passed");

// Reproduce the private-message race: ciphertext beats handshake message 3.
var privateTransit=new List<(bool ToReceiver,MeshPacket Packet)>();
Task<int> PrivateLink(bool toReceiver,MeshPacket packet){privateTransit.Add((toReceiver,packet));return Task.FromResult(1);}
var macNoise=RandomNumberGenerator.GetBytes(32);var macSigning=RandomNumberGenerator.GetBytes(32);
var pcNoise=RandomNumberGenerator.GetBytes(32);var pcSigning=RandomNumberGenerator.GetBytes(32);
using var macSession=new MeshEngine(macNoise,macSigning,"mac",packet=>PrivateLink(true,packet));
using var pcSession=new MeshEngine(pcNoise,pcSigning,"pc",packet=>PrivateLink(false,packet));
var privateReceived=new List<ChatMessage>();pcSession.Message+=privateReceived.Add;
await macSession.Announce();await pcSession.Announce();
foreach(var item in privateTransit.ToArray())await(item.ToReceiver?pcSession:macSession).Receive(item.Packet.Encode());privateTransit.Clear();
await macSession.SendPrivate(pcSession.Id,"private ciphertext before final handshake","early-private-1");
var firstPrivate=privateTransit[0];privateTransit.Clear();await pcSession.Receive(firstPrivate.Packet.Encode());
var secondPrivate=privateTransit[0];privateTransit.Clear();await macSession.Receive(secondPrivate.Packet.Encode());
var finalHandshake=privateTransit.Single(item=>item.Packet.Type==0x10);
foreach(var item in privateTransit.Where(item=>item.Packet.Type==0x11).ToArray())await pcSession.Receive(item.Packet.Encode());privateTransit.Clear();
Check(privateReceived.Count==0,"Early private ciphertext stays private until the exact session authenticates");
await pcSession.Receive(finalHandshake.Packet.Encode());
Check(privateReceived.Count==1&&privateReceived[0].Content=="private ciphertext before final handshake","Out-of-order Mac private message is delivered after handshake authentication");
privateTransit.Clear();
using var restartedPc=new MeshEngine(pcNoise,pcSigning,"restarted pc",packet=>PrivateLink(false,packet));
restartedPc.RestorePeer(new(macSession.Id,"mac",Crypto.PublicX(macNoise),Crypto.PublicEd(macSigning),[0],Bytes.Now));
await macSession.SendPrivate(pcSession.Id,"message after PC restart","restart-private-1");
var staleCipher=privateTransit.First(item=>item.ToReceiver&&item.Packet.Type==0x11);privateTransit.Clear();await restartedPc.Receive(staleCipher.Packet.Encode());
Check(privateTransit.Any(item=>!item.ToReceiver&&item.Packet.Type==0x10),"PC restart actively renegotiates a stale Mac private session");
Console.WriteLine($"TOTAL {passed} checks passed");
var restartedReceived=new List<ChatMessage>();restartedPc.Message+=restartedReceived.Add;
int restartBudget=100;while(privateTransit.Count>0){if(--restartBudget==0)throw new Exception("Restart negotiation loop");var item=privateTransit[0];privateTransit.RemoveAt(0);await(item.ToReceiver?restartedPc:macSession).Receive(item.Packet.Encode());}
await macSession.SendPrivate(restartedPc.Id,"message after PC restart","restart-private-1");
while(privateTransit.Count>0){if(--restartBudget==0)throw new Exception("Restart message loop");var item=privateTransit[0];privateTransit.RemoveAt(0);await(item.ToReceiver?restartedPc:macSession).Receive(item.Packet.Encode());}
Check(restartedReceived.Count==1&&restartedReceived[0].Id=="restart-private-1","Renegotiated Mac session can deliver the previously unacknowledged private message");
await IdentityAndTransferTests.Run(Check);
Console.WriteLine($"TOTAL {passed} checks passed");
