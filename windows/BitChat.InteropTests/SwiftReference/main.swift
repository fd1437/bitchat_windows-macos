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

try JSONSerialization.data(withJSONObject:output,options:[.prettyPrinted,.sortedKeys]).write(to:URL(fileURLWithPath:CommandLine.arguments[1]))
