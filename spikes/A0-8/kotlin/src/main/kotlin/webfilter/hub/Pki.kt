package webfilter.hub

import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.asn1.x509.BasicConstraints
import org.bouncycastle.asn1.x509.Extension
import org.bouncycastle.asn1.x509.ExtendedKeyUsage
import org.bouncycastle.asn1.x509.KeyPurposeId
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import org.bouncycastle.pkcs.jcajce.JcaPKCS10CertificationRequestBuilder
import java.math.BigInteger
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Date

/** Keys, CSRs and certificates for the tests. On the device the agent's key will be in AndroidKeyStore (not measured here). */
object Pki {
    fun newKeyPair(): KeyPair =
        KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

    /** A PKCS#10 request for P-256, signed with ecdsa-with-SHA256, subject "CN=device:{deviceId}" as in HubLink.PairAsync. */
    fun csrDer(keys: KeyPair, deviceId: String): ByteArray {
        val signer = JcaContentSignerBuilder("SHA256withECDSA").build(keys.private)
        return JcaPKCS10CertificationRequestBuilder(X500Name("CN=device:$deviceId"), keys.public).build(signer).encoded
    }

    /** A self-signed CA with the given subject (the hub's own subject, for the forged-chain test), marked as a CA. */
    fun foreignCa(name: X500Name = X500Name("CN=Foreign test CA")): Pair<KeyPair, X509Certificate> {
        val keys = newKeyPair()
        val now = System.currentTimeMillis()
        val builder = JcaX509v3CertificateBuilder(
            name, BigInteger.valueOf(now), Date(now - 300_000L), Date(now + 86_400_000L), name, keys.public,
        )
        builder.addExtension(Extension.basicConstraints, true, BasicConstraints(true))
        val cert = JcaX509CertificateConverter().getCertificate(builder.build(JcaContentSignerBuilder("SHA256withECDSA").build(keys.private)))
        return keys to cert
    }

    /** An end-entity certificate for [keys], signed by [caKeys] under [ca], with one extended key usage ([purpose]). */
    fun endEntity(keys: KeyPair, caKeys: KeyPair, ca: X509Certificate, subject: String, purpose: KeyPurposeId): X509Certificate {
        val now = System.currentTimeMillis()
        val builder = JcaX509v3CertificateBuilder(
            X500Name(ca.subjectX500Principal.name), BigInteger.valueOf(now), Date(now - 300_000L), Date(now + 86_400_000L),
            X500Name("CN=$subject"), keys.public,
        )
        builder.addExtension(Extension.basicConstraints, true, BasicConstraints(false))
        builder.addExtension(Extension.extendedKeyUsage, false, ExtendedKeyUsage(purpose))
        return JcaX509CertificateConverter().getCertificate(builder.build(JcaContentSignerBuilder("SHA256withECDSA").build(caKeys.private)))
    }
}
