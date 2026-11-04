import Foundation
import CryptoKit
import BitFoundation
let seed=Data(repeating:0x42,count:32)
let key=try Curve25519.Signing.PrivateKey(rawRepresentation:seed)
let sender=Data([1,2,3,4,5,6,7,8])
var packets:[[String:Any]]=[]
for size in [10,255,256,300,500,1024,4000] {
 let text=String(repeating:"hello bitchat mesh! ",count:size/20+1)
 let raw=Data(text.utf8).prefix(size)
 var packet=BitchatPacket(type:2,senderID:sender,recipientID:nil,timestamp:1793740000000,payload:raw,signature:nil,ttl:7)
 let canonical=packet.toBinaryDataForSigning()!
 packet.signature=try key.signature(for:canonical)
 packets.append(["payload":raw.base64EncodedString(),"signing":canonical.base64EncodedString(),"wire":packet.toBinaryData(padding:false)!.base64EncodedString()])
}
let fingerprint=Data(repeating:0x11,count:32).map{String(format:"%02x",$0)}.joined()
let group=BitchatGroup(groupID:Data(repeating:0x12,count:16),name:"fixture group",epoch:3,members:[GroupMember(fingerprint:fingerprint,signingKey:key.publicKey.rawRepresentation,nickname:"alice")],creatorFingerprint:fingerprint)
let state=GroupStatePayload.makeSigned(group:group,key:Data(repeating:0x55,count:32),sign:{try? key.signature(for:$0)})!
let message=try GroupCrypto.sealMessage(content:"bonjour Windows ↔ iOS",messageID:"FIXTURE-001",senderNickname:"alice",senderSigningKey:key.publicKey.rawRepresentation,timestampMs:1793740000000,groupID:group.groupID,epoch:3,key:state.key,sign:{try? key.signature(for:$0)})
let courierRecipient=try Curve25519.KeyAgreement.PrivateKey(rawRepresentation:Data(repeating:0x33,count:32))
let courierSender=try Curve25519.KeyAgreement.PrivateKey(rawRepresentation:Data(repeating:0x44,count:32))
let courierHandshake=NoiseHandshakeState(role:.initiator,pattern:.X,keychain:MockKeychain(),localStaticKey:courierSender,remoteStaticKey:courierRecipient.publicKey,prologue:Data("bitchat-courier-v1".utf8),predeterminedEphemeralKey:try Curve25519.KeyAgreement.PrivateKey(rawRepresentation:Data(repeating:0x22,count:32)))
let courierPayload=Data("courier reference payload".utf8)
let courierCipher=try courierHandshake.writeMessage(payload:courierPayload)
let courier:[String:String]=["recipient_secret":courierRecipient.rawRepresentation.base64EncodedString(),"sender_secret":courierSender.rawRepresentation.base64EncodedString(),"payload":courierPayload.base64EncodedString(),"ciphertext":courierCipher.base64EncodedString()]
let filterIDs=(0..<100).map { Data(SHA256.hash(data:Data("id\($0)".utf8))).prefix(16) }
let gcs=GCSFilter.buildFilter(ids:filterIDs,maxBytes:1024,targetFpr:1.0/1024)
let unsignedBoard=BoardPostPacket(postID:Data(repeating:0x66,count:16),geohash:"u09tvw",content:"Swift board reference",authorSigningKey:key.publicKey.rawRepresentation,authorNickname:"alice",createdAt:1793740000000,expiresAt:1793826400000,flags:1,signature:Data())
let board=BoardPostPacket(postID:unsignedBoard.postID,geohash:unsignedBoard.geohash,content:unsignedBoard.content,authorSigningKey:unsignedBoard.authorSigningKey,authorNickname:unsignedBoard.authorNickname,createdAt:unsignedBoard.createdAt,expiresAt:unsignedBoard.expiresAt,flags:unsignedBoard.flags,signature:try key.signature(for:unsignedBoard.signingBytes))
var output:[String:Any]=["gcs":["p":gcs.p,"m":gcs.m,"data":gcs.data.base64EncodedString()],"board":BoardWire.post(board).encode().base64EncodedString(),"courier":courier,"seed":seed.base64EncodedString(),"packets":packets,"group_state":state.encode()!.base64EncodedString(),"group_message":message.base64EncodedString()]

let mediaNames = ["img_12345678-1234-1234-1234-123456789ABC.jpg", "img_été_12345678-1234-1234-1234-123456789abc.JPEG", "voice_0123456789abcdef.m4a", "voice_12345678-1234-1234-1234-123456789ABC.m4a", "voice_20261104.m4a", "img_20261104.jpg", "../img_12345678-1234-1234-1234-123456789ABC.jpg", "voice_0123456789abcdef.aac"]
var mediaIdentities:[[String:Any]]=[]
for name in mediaNames {
    for sender in ["0123456789abcdef", "noise:"+String(repeating:"aa",count:32)] {
        let recipient="fedcba9876543210"
        let id=PrivateMediaMessageIdentity.stableID(senderPeerID:PeerID(str:sender),recipientPeerID:PeerID(str:recipient),fileName:name)
        mediaIdentities.append(["sender":sender,"recipient":recipient,"name":name,"id":id as Any? ?? NSNull()])
    }
}
output["private_media_identities"]=mediaIdentities


let dropNow=Date(timeIntervalSince1970:1793750000)
let dropTag=CourierEnvelope.recipientTag(noiseStaticKey:courierRecipient.publicKey.rawRepresentation,epochDay:CourierEnvelope.epochDay(for:dropNow))
let dropEnvelope=CourierEnvelope(recipientTag:dropTag,expiry:UInt64((dropNow.timeIntervalSince1970+3600)*1000),ciphertext:courierCipher,copies:1)
let dropBytes=dropEnvelope.encode()!
let dropIdentity=try NostrIdentity(privateKeyData:Data(repeating:0x13,count:32))
let dropEvent=try NostrProtocol.createCourierDropEvent(envelope:dropBytes,recipientTagHex:dropTag.hexEncodedString(),expiresAt:dropNow.addingTimeInterval(3600),senderIdentity:dropIdentity)
output["bridge_drop"]=["now_ms":Int64(dropNow.timeIntervalSince1970*1000),"envelope":dropBytes.base64EncodedString(),"event":try dropEvent.jsonString(),"sender_key":SwiftBridgeIdentity.fixture("bridge-été-001",courierRecipient.publicKey.rawRepresentation),"recipient_public":courierRecipient.publicKey.rawRepresentation.base64EncodedString()]

var writeFixtures:[[String:Any]]=[]
let writeWire=Data(base64Encoded:packets[3]["wire"] as! String)!
for mtu in [20,61,182,244,512] {
    var correct=BLEInboundWriteBuffer(),wrong=BLEInboundWriteBuffer()
    var accepted=false,offsetZeroAccepted=false
    for offset in stride(from:0,to:writeWire.count,by:mtu) {
        let chunk=Data(writeWire[offset..<min(offset+mtu,writeWire.count)])
        if case .decoded = correct.append(chunks:[BLEInboundWriteChunk(offset:offset,data:chunk)],for:"peer",capBytes:4096){accepted=true}
        if case .decoded = wrong.append(chunks:[BLEInboundWriteChunk(offset:0,data:chunk)],for:"peer",capBytes:4096){offsetZeroAccepted=true}
    }
    precondition(accepted)
    writeFixtures.append(["att_payload":mtu,"wire":writeWire.base64EncodedString(),"payload":packets[3]["payload"]!,"accepted_offsets":accepted,"accepted_offset_zero":offsetZeroAccepted])
}
output["central_write_offsets"]=writeFixtures

let imageContent=Data(base64Encoded:"iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4nGOwWZCHFTEMLQkAMChSgdtUpbYAAAAASUVORK5CYII=")!
let imageName="img_12345678-1234-1234-1234-123456789ABC.png"
let canonicalImage=BitchatFilePacket(fileName:imageName,fileSize:UInt64(imageContent.count),mimeType:"image/png",content:imageContent).encode()!
func imageTlv(_ tag:UInt8,_ value:Data)->Data { var result=Data([tag,UInt8(value.count>>8),UInt8(value.count&255)]);result.append(value);return result }
var legacyImage=imageTlv(1,Data(imageName.utf8));legacyImage.append(imageTlv(2,Data([0,0,0,0,0,0,0,UInt8(imageContent.count)])));legacyImage.append(imageTlv(3,Data("image/png".utf8)));legacyImage.append(imageTlv(4,imageContent))
let imageNoisePublic=courierSender.publicKey.rawRepresentation
let imageSender=Data(SHA256.hash(data:imageNoisePublic)).prefix(8)
var imageFixtures:[[String:Any]]=[]
for (name,encoded) in [("canonical",canonicalImage),("legacy",legacyImage)] {
    precondition(BitchatFilePacket.decode(encoded)?.content==imageContent)
    var imagePacket=BitchatPacket(type:0x22,senderID:imageSender,recipientID:nil,timestamp:1793740000000,payload:encoded,signature:nil,ttl:7,version:2)
    imagePacket.signature=try key.signature(for:imagePacket.toBinaryDataForSigning()!)
    imageFixtures.append(["format":name,"file":encoded.base64EncodedString(),"wire":imagePacket.toBinaryData(padding:false)!.base64EncodedString(),"content":imageContent.base64EncodedString(),"noise_public":imageNoisePublic.base64EncodedString()])
}
output["image_files"]=imageFixtures
try JSONSerialization.data(withJSONObject:output,options:[.prettyPrinted,.sortedKeys]).write(to:URL(fileURLWithPath:CommandLine.arguments[1]))
