These frozen fixtures are generated with the actual Swift sources at the baseline commit.
The generator's main.swift lives here. It imports BitFoundation and uses upstream
GroupProtocol.swift, BoardPackets.swift, BitchatFilePacket.swift and NoiseProtocol.swift with the upstream
MockKeychain (only its @testable import is removed). The full source remains in
this repository at the paths named above. Run it from a small Swift executable
target with BitFoundation and BitLogger dependencies; pass the desired JSON path
as its first command-line argument. No production keychain is accessed.

Private media fixtures call the upstream PrivateMediaMessageIdentity directly, including full Noise-key peer IDs, UTF-8 filenames, UUIDs, live voice burst IDs and ineligible legacy names.

Bridge-drop fixtures use upstream NostrIdentity.swift and Bech32.swift, with P256K
from swift-secp256k1 pinned to 0.24.0 (the upstream Xcode dependency).
NostrReference.swift retains the upstream createCourierDropEvent, NostrEvent,
NostrError and senderDropKey implementations, wrapped to compile independently;
the three TransportConfig tag limits are copied unchanged. No signing mock is used.
Only bridge_drop was appended to the previously frozen JSON: regenerating the
whole generator produces new randomized Ed25519/Schnorr signatures elsewhere.
