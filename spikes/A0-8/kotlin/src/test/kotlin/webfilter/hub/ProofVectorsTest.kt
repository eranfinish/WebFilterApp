package webfilter.hub

import org.json.JSONObject
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Criterion 3: the Kotlin proof equals the C# proof on the same inputs. The vectors come from the real HubPairing and
 * HubCertificates code (`A0_8Hub vectors`), not from a re-implementation.
 */
class ProofVectorsTest {
    private val vectors = JSONObject(File(System.getProperty("user.dir"), "../vectors/proof-vectors.json").readText())
    private fun bytes(name: String) = unb64(vectors.getString(name))

    @Test
    fun codeIsNormalizedLikeTheHub() {
        assertEquals("ABCDEFGHJKMN", Proof.normalize("abcd-efgh-jkmn"))
        assertEquals(Proof.normalize("ABCD-EFGH-JKMN"), Proof.normalize("abcdefghjkmn"))
    }

    @Test
    fun agentProofEqualsTheCSharpProof() {
        val proof = Proof.mac(vectors.getString("code"), Proof.agentProofData(bytes("csrDerBase64"), bytes("serverLeafDerBase64")))
        assertTrue(Proof.equal(proof, bytes("agentProofBase64")), "Kotlin ${b64(proof)} != C# ${vectors.getString("agentProofBase64")}")
    }

    @Test
    fun hubResponseProofEqualsTheCSharpProof() {
        val proof = Proof.mac(vectors.getString("code"), bytes("clientCertDerBase64"), bytes("caDerBase64"))
        assertTrue(Proof.equal(proof, bytes("responseProofBase64")))
    }

    @Test
    fun theHubsOwnChainCheckAcceptsADeviceCertificateAsTheHub() {
        // Finding for A2 and the hub, not fixed here: HubCertificates.ChainsTo (used by HubLink for the hub certificate) has no
        // EKU check, so a device certificate passes it. The Kotlin agent rejects that case (TrustTest), the C# agent does not.
        assertEquals("True", vectors.getString("csharpChainsToAcceptsDeviceCertAsHub"))
    }

    @Test
    fun proofOverTheCaHashIsADifferentProofSoTheLeafMustBeHashed() {
        val code = vectors.getString("code")
        val overCa = Proof.mac(code, Proof.agentProofData(bytes("csrDerBase64"), bytes("caDerBase64")))
        assertTrue(Proof.equal(overCa, bytes("agentProofOverCaHashBase64")), "the C# negative vector must match the Kotlin one")
        assertFalse(Proof.equal(overCa, bytes("agentProofBase64")))
    }
}
