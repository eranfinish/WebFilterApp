package webfilter.hub

import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.asn1.x509.KeyPurposeId
import org.json.JSONObject
import java.io.File
import java.security.cert.CertificateException
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * Pinning without a server: the leaf and CA are the real ones from the vectors, the forged ones are made here.
 * A chain that only mentions the real CA must not pass, and neither may a real leaf under a CA that is not pinned.
 */
class TrustTest {
    private val vectors = JSONObject(File(System.getProperty("user.dir"), "../vectors/proof-vectors.json").readText())
    private val leaf = certFromDer(unb64(vectors.getString("serverLeafDerBase64")))
    private val ca = certFromDer(unb64(vectors.getString("caDerBase64")))
    private val auth = "ECDHE_ECDSA"

    @Test
    fun acceptsTheServerLeafSignedByThePinnedCa() {
        PinnedCaTrustManager(ca).checkServerTrusted(arrayOf(leaf, ca), auth) // no exception: the chain as the hub sends it
    }

    @Test
    fun rejectsALeafForgedUnderACaWithTheSameNameThatIsNotThePinnedKey() {
        // The forger uses the hub CA's own subject name and a key of their own, and gives the leaf the serverAuth EKU, so only
        // the signature check can fail. Their CA is not the real one, so the signature under the real key does not verify.
        val (forgedCaKeys, forgedCa) = Pki.foreignCa(X500Name(ca.subjectX500Principal.name))
        val forgedLeaf = Pki.endEntity(Pki.newKeyPair(), forgedCaKeys, forgedCa, "forged hub", KeyPurposeId.id_kp_serverAuth)
        assertEquals(ca.subjectX500Principal, forgedLeaf.issuerX500Principal, "the forgery must pass the name check")
        assertFailsWith<CertificateException> {
            PinnedCaTrustManager(ca).checkServerTrusted(arrayOf(forgedLeaf, ca), auth)
        }
    }

    @Test
    fun rejectsALeafFromAnotherCaWhenTheRealCaIsAppended() {
        val (foreignKeys, foreignCa) = Pki.foreignCa()
        val leafOfOtherCa = Pki.endEntity(Pki.newKeyPair(), foreignKeys, foreignCa, "other hub", KeyPurposeId.id_kp_serverAuth)
        assertFailsWith<CertificateException> {
            PinnedCaTrustManager(ca).checkServerTrusted(arrayOf(leafOfOtherCa, ca), auth)
        }
    }

    @Test
    fun rejectsADeviceClientCertificatePresentedAsTheServer() {
        // Every device holds a certificate the hub CA signed, with clientAuth only. Accepting it as the hub would let one
        // stolen device key impersonate the hub to the other agents (the EKU check is what stops this).
        val clientCert = certFromDer(unb64(vectors.getString("clientCertDerBase64")))
        assertTrue(Trust.isIssuedBy(clientCert, ca), "the device certificate is signed by the real CA")
        assertFailsWith<CertificateException> {
            PinnedCaTrustManager(ca).checkServerTrusted(arrayOf(clientCert, ca), auth)
        }
    }

    @Test
    fun rejectsTheCaItselfPresentedAsTheLeaf() {
        assertFailsWith<CertificateException> {
            PinnedCaTrustManager(ca).checkServerTrusted(arrayOf(ca), auth)
        }
    }

    @Test
    fun rejectsTheRealLeafWhenAnotherCaIsPinned() {
        val (_, foreignCa) = Pki.foreignCa()
        assertFailsWith<CertificateException> {
            PinnedCaTrustManager(foreignCa).checkServerTrusted(arrayOf(leaf, ca), auth)
        }
    }

    @Test
    fun rejectsAnEmptyChain() {
        assertFailsWith<CertificateException> { PinnedCaTrustManager(ca).checkServerTrusted(arrayOf(), auth) }
    }
}
