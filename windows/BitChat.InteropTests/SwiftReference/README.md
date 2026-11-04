These frozen fixtures are generated with the actual Swift sources at the baseline commit.
The generator's main.swift lives here. It imports BitFoundation and uses upstream
GroupProtocol.swift, BoardPackets.swift and NoiseProtocol.swift with the upstream
MockKeychain (only its @testable import is removed). The full source remains in
this repository at the paths named above. Run it from a small Swift executable
target with BitFoundation and BitLogger dependencies; pass the desired JSON path
as its first command-line argument. No production keychain is accessed.
