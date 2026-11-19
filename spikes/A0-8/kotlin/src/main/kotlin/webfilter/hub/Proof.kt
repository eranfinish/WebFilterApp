package webfilter.hub

import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * The pairing proofs, as HubPairing.cs (WebFilter.Service) computes them.
 * The HMAC key is the UTF-8 of the normalized code: uppercase, only the 32-letter alphabet kept (dashes dropped).
 */
object Proof {
    private const val ALPHABET = "ABCDEFGHJKMNPQRSTVWXYZ0123456789" // no I, L, O, U, same as HubPairing

    fun normalize(code: String): String = code.uppercase().filter { it in ALPHABET }

    fun mac(code: String, vararg parts: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(normalize(code).toByteArray(Charsets.UTF_8), "HmacSHA256"))
        for (part in parts) mac.update(part)
        return mac.doFinal()
    }

    fun sha256(data: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(data)

    /** What the agent's proof covers (HubPairing.AgentProofData): CSR DER, then SHA-256 of the server LEAF certificate DER. */
    fun agentProofData(csrDer: ByteArray, serverLeafDer: ByteArray): ByteArray = csrDer + sha256(serverLeafDer)

    fun equal(a: ByteArray, b: ByteArray): Boolean = MessageDigest.isEqual(a, b)
}
