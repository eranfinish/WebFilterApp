package webfilter.hub

import java.net.Socket
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Date
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.SSLPeerUnverifiedException
import javax.net.ssl.SSLSession
import javax.net.ssl.X509KeyManager
import javax.net.ssl.X509TrustManager

/** Certificate checks the agent does itself, so no test depends on the platform's CA store. */
object Trust {
    const val SERVER_AUTH = "1.3.6.1.5.5.7.3.1"

    /**
     * The hub's TLS certificate: signed by the pinned CA AND carrying the serverAuth EKU. The EKU matters: every device
     * certificate is also signed by that CA, and without this check a stolen device key could pose as the hub to other agents.
     */
    fun isServerLeaf(cert: X509Certificate, ca: X509Certificate): Boolean =
        isIssuedBy(cert, ca) && cert.extendedKeyUsage?.contains(SERVER_AUTH) == true

    /**
     * True when [cert] is an end-entity certificate, valid now, whose signature verifies under [ca]'s public key and whose
     * issuer name is [ca]'s subject. Checking the signature (not only membership in a list) is what stops an attacker
     * who appends the real CA certificate to a chain made with their own key.
     */
    fun isIssuedBy(cert: X509Certificate, ca: X509Certificate): Boolean {
        return try {
            cert.checkValidity(Date())
            ca.checkValidity(Date())
            val endEntity = cert.basicConstraints == -1 && ca.basicConstraints >= 0
            val sameIssuer = cert.issuerX500Principal == ca.subjectX500Principal
            cert.verify(ca.publicKey) // throws when the signature does not match
            endEntity && sameIssuer
        } catch (e: Exception) {
            false
        }
    }
}

/** Trusts the server only when its leaf certificate (chain[0]) is signed by the pinned hub CA. Other chain entries are ignored. */
class PinnedCaTrustManager(private val ca: X509Certificate) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
        if (chain.isEmpty()) throw CertificateException("empty server chain")
        if (!Trust.isServerLeaf(chain[0], ca)) throw CertificateException("server certificate is not a hub server certificate under the pinned CA")
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) {
        throw CertificateException("the agent does not validate client certificates")
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf(ca)
}

/** Records the server leaf of the first handshake and accepts it. Only used before pairing, when the CA is not known yet. */
class CapturingTrustManager : X509TrustManager {
    @Volatile var leaf: X509Certificate? = null
    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
        leaf = chain.firstOrNull()
    }
    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) {}
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

/** The plan's HostnameVerifier: accepts a session whose leaf is signed by the pinned hub CA, whatever host name was used. */
class PinnedCaHostnameVerifier(private val ca: X509Certificate) : HostnameVerifier {
    override fun verify(hostname: String, session: SSLSession): Boolean = try {
        Trust.isServerLeaf(session.peerCertificates[0] as X509Certificate, ca)
    } catch (e: SSLPeerUnverifiedException) {
        false
    }
}

/** Presents one client identity (certificate chain and its key) whenever the server asks, whatever its issuer list says. */
class FixedKeyManager(private val key: PrivateKey, private val chain: Array<X509Certificate>) : X509KeyManager {
    override fun getClientAliases(keyType: String?, issuers: Array<Principal>?): Array<String> = arrayOf(ALIAS)
    override fun chooseClientAlias(keyType: Array<String>?, issuers: Array<Principal>?, socket: Socket?): String = ALIAS
    override fun getServerAliases(keyType: String?, issuers: Array<Principal>?): Array<String>? = null
    override fun chooseServerAlias(keyType: String?, issuers: Array<Principal>?, socket: Socket?): String? = null
    override fun getCertificateChain(alias: String?): Array<X509Certificate>? = if (alias == ALIAS) chain else null
    override fun getPrivateKey(alias: String?): PrivateKey? = if (alias == ALIAS) key else null

    companion object {
        const val ALIAS = "device"
    }
}
