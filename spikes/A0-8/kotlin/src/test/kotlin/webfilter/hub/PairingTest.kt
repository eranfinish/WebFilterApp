package webfilter.hub

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * Criterion 3, live: the hub accepts exactly the proof HMAC(code, CSR || SHA-256(server leaf)), and refuses the others.
 */
class PairingTest {
    @Test
    fun codeTypedInLowercaseWithoutDashesPairs() {
        HubProcess.start().use { hub ->
            val paired = pairDevice(hub.address, hub.code.lowercase().replace("-", ""))
            assertTrue(paired.hubId.isNotBlank())
        }
    }

    @Test
    fun wrongCodeIsRefusedWith403() {
        HubProcess.start().use { hub ->
            val pairing = Pairing(hub.address)
            val leaf = pairing.serverLeaf()
            val keys = Pki.newKeyPair()
            val csr = Pki.csrDer(keys, "wrong-code")
            val proof = Proof.mac("ZZZZ-ZZZZ-ZZZZ", Proof.agentProofData(csr, leaf.encoded))
            val refused = assertFailsWith<PairingRefused> { pairing.post("wrong-code", csr, proof) }
            assertEquals(403, refused.status)
        }
    }

    @Test
    fun proofOverTheHubCaInsteadOfTheLeafIsRefusedWith403() {
        HubProcess.start().use { hub ->
            // First a real pairing, to learn the hub CA the way an agent does.
            val paired = pairDevice(hub.address, hub.code)
            val secondCode = hub.newCode()

            val pairing = Pairing(hub.address)
            pairing.serverLeaf() // the handshake happens; the proof below deliberately covers the CA instead of the leaf
            val keys = Pki.newKeyPair()
            val csr = Pki.csrDer(keys, "ca-hash")
            val wrongProof = Proof.mac(secondCode, Proof.agentProofData(csr, paired.ca.encoded))
            val refused = assertFailsWith<PairingRefused> { pairing.post("ca-hash", csr, wrongProof) }
            assertEquals(403, refused.status)

            // The positive control: the same code, with the proof over the leaf, is accepted. So the refusal is about the hash.
            val again = pairDevice(hub.address, secondCode, deviceId = "ca-hash-control")
            assertTrue(again.hubId.isNotBlank())
        }
    }

    @Test
    fun aCodeWorksOnlyOnce() {
        HubProcess.start().use { hub ->
            pairDevice(hub.address, hub.code, deviceId = "first")
            val refused = assertFailsWith<PairingRefused> { pairDevice(hub.address, hub.code, deviceId = "second") }
            assertEquals(403, refused.status)
        }
    }
}
