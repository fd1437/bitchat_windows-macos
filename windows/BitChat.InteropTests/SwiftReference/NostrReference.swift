import Foundation
import CryptoKit
import P256K
import BitFoundation
struct NostrProtocol { enum EventKind: Int { case courierDrop = 1401 }
static func createCourierDropEvent(
        envelope: Data,
        recipientTagHex: String,
        expiresAt: Date,
        senderIdentity: NostrIdentity
    ) throws -> NostrEvent {
        let tags = [
            ["x", recipientTagHex],
            ["expiration", String(Int(expiresAt.timeIntervalSince1970))]
        ]
        let event = NostrEvent(
            pubkey: senderIdentity.publicKeyHex,
            createdAt: Date(),
            kind: .courierDrop,
            tags: tags,
            content: envelope.base64EncodedString()
        )
        let schnorrKey = try senderIdentity.schnorrSigningKey()
        return try event.sign(with: schnorrKey)
    }
}
struct NostrEvent: Codable {
    var id: String
    let pubkey: String
    let created_at: Int
    let kind: Int
    let tags: [[String]]
    let content: String
    var sig: String?
    
    init(
        pubkey: String,
        createdAt: Date,
        kind: NostrProtocol.EventKind,
        tags: [[String]],
        content: String
    ) {
        self.pubkey = pubkey
        self.created_at = Int(createdAt.timeIntervalSince1970)
        self.kind = kind.rawValue
        self.tags = tags
        self.content = content
        self.sig = nil
        self.id = "" // Will be set during signing
    }
    
    init(from dict: [String: Any]) throws {
        guard let pubkey = dict["pubkey"] as? String,
              let createdAt = dict["created_at"] as? Int,
              let kind = dict["kind"] as? Int,
              let tags = dict["tags"] as? [[String]],
              let content = dict["content"] as? String else {
            throw NostrError.invalidEvent
        }

        guard Self.isWithinInboundTagLimits(tags) else {
            throw NostrError.invalidEvent
        }
        
        self.id = dict["id"] as? String ?? ""
        self.pubkey = pubkey
        self.created_at = createdAt
        self.kind = kind
        self.tags = tags
        self.content = content
        self.sig = dict["sig"] as? String
    }

    /// Bounds untrusted relay tag arrays so attackers cannot force large
    /// allocations or expensive joins on the inbound hot path.
    static func isWithinInboundTagLimits(_ tags: [[String]]) -> Bool {
        guard tags.count <= TransportConfig.nostrMaxEventTags else { return false }

        for tag in tags {
            guard tag.count <= TransportConfig.nostrMaxEventTagValues else { return false }
            guard tag.allSatisfy({ $0.utf8.count <= TransportConfig.nostrMaxEventTagValueBytes }) else {
                return false
            }
        }

        return true
    }
    
    func sign(with key: P256K.Schnorr.PrivateKey) throws -> NostrEvent {
        let (eventId, eventIdHash) = try calculateEventId()
        
        // Sign with Schnorr (BIP-340)
        var messageBytes = [UInt8](eventIdHash)
        var auxRand = [UInt8](repeating: 0, count: 32)
        let auxStatus = auxRand.withUnsafeMutableBytes { ptr in
            SecRandomCopyBytes(kSecRandomDefault, 32, ptr.baseAddress!)
        }
        guard auxStatus == errSecSuccess else {
            throw NostrError.cryptographicFailure
        }
        let schnorrSignature = try key.signature(message: &messageBytes, auxiliaryRand: &auxRand)
        
        let signatureHex = schnorrSignature.dataRepresentation.hexEncodedString()
        
        var signed = self
        signed.id = eventId
        signed.sig = signatureHex
        return signed
    }

    /// Validate that the event ID and Schnorr signature match the content and pubkey.
    /// Returns false when the signature is missing, malformed, or does not verify.
    func isValidSignature() -> Bool {
        guard let sig = sig,
              let sigData = Data(hexString: sig),
              let pubData = Data(hexString: pubkey),
              sigData.count == 64,
              pubData.count == 32,
              let signature = try? P256K.Schnorr.SchnorrSignature(dataRepresentation: sigData),
              let (expectedId, eventHash) = try? calculateEventId(),
              expectedId == id
        else {
            return false
        }

        var messageBytes = [UInt8](eventHash)
        let xonly = P256K.Schnorr.XonlyKey(dataRepresentation: pubData)
        return xonly.isValid(signature, for: &messageBytes)
    }
    
    private func calculateEventId() throws -> (String, Data) {
        let serialized = [
            0,
            pubkey,
            created_at,
            kind,
            tags,
            content
        ] as [Any]
        
        let data = try JSONSerialization.data(withJSONObject: serialized, options: [.withoutEscapingSlashes])
        return (data.sha256Fingerprint(), data.sha256Hash())
    }
    
    func jsonString() throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        let data = try encoder.encode(self)
        return String(data: data, encoding: .utf8) ?? ""
    }
}

enum NostrError: Error {
    case invalidPublicKey
    case invalidEvent
    case invalidCiphertext
    case cryptographicFailure
}


struct SwiftBridgeIdentity {
private static func senderDropKey(
        messageID: String,
        recipientNoiseKey: Data
    ) -> String {
        var material = Data("bitchat-bridge-drop-dedup-v2".utf8)
        appendLengthPrefixed(Data(messageID.utf8), to: &material)
        appendLengthPrefixed(recipientNoiseKey, to: &material)
        return "v2:\(material.sha256Hex())"
    }
private static func appendLengthPrefixed(_ value: Data, to output: inout Data) {
        let length = UInt32(value.count)
        output.append(UInt8((length >> 24) & 0xFF))
        output.append(UInt8((length >> 16) & 0xFF))
        output.append(UInt8((length >> 8) & 0xFF))
        output.append(UInt8(length & 0xFF))
        output.append(value)
    }
static func fixture(_ id:String,_ recipient:Data)->String { senderDropKey(messageID:id,recipientNoiseKey:recipient) }
}

// Upstream TransportConfig limits, unchanged.
enum TransportConfig { static let nostrMaxEventTags = 64; static let nostrMaxEventTagValues = 16; static let nostrMaxEventTagValueBytes = 1024 }
