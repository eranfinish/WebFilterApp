package webfilter.hub

import okhttp3.ConnectionSpec
import okhttp3.Dns
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.TlsVersion
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.security.KeyPair
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.concurrent.TimeUnit
import javax.net.ssl.KeyManager
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager

fun b64(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)
fun unb64(text: String): ByteArray = Base64.getDecoder().decode(text)
fun certFromDer(der: ByteArray): X509Certificate =
    CertificateFactory.getInstance("X.509").generateCertificate(ByteArrayInputStream(der)) as X509Certificate

/** The hub answered 4xx on pairing. */
class PairingRefused(val status: Int) : RuntimeException("the hub refused the pairing with HTTP $status")

/** A device identity: its key and the certificate chain the hub issued for it. */
class Identity(val keys: KeyPair, val chain: Array<X509Certificate>)

/** What the agent keeps after pairing (HubLink in the C# code). */
class Paired(val hubId: String, val identity: Identity, val ca: X509Certificate, val serverLeaf: X509Certificate)

/** The HTTP client the agent uses after pairing: pinned CA, optional client identity, optional TLS 1.2 only (the Android 9 case). */
object HubHttp {
    fun client(
        ca: X509Certificate,
        identity: Identity? = null,
        pinnedHostnames: Boolean = true,
        dns: Dns = Dns.SYSTEM,
        tls12Only: Boolean = false,
    ): OkHttpClient {
        val trust = PinnedCaTrustManager(ca)
        val keyManagers: Array<KeyManager>? = identity?.let { arrayOf(FixedKeyManager(it.keys.private, it.chain)) }
        val context = SSLContext.getInstance("TLS")
        context.init(keyManagers, arrayOf<TrustManager>(trust), null)
        val builder = OkHttpClient.Builder()
            .sslSocketFactory(context.socketFactory, trust)
            .dns(dns)
            .readTimeout(60, TimeUnit.SECONDS) // the hub sends a keep-alive every 25 s
        if (pinnedHostnames) builder.hostnameVerifier(PinnedCaHostnameVerifier(ca))
        if (tls12Only) builder.connectionSpecs(listOf(ConnectionSpec.Builder(ConnectionSpec.MODERN_TLS).tlsVersions(TlsVersion.TLS_1_2).build()))
        return builder.build()
    }
}

/**
 * Pairing as the hub expects it (HubServer.cs /v1/pair; HubLink.PairAsync). It runs before the CA is known, so the first
 * handshake only captures the server leaf, which the agent's proof must cover.
 */
class Pairing(private val address: String) {
    private val capture = CapturingTrustManager()
    private val client: OkHttpClient

    init {
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf<TrustManager>(capture), null)
        client = OkHttpClient.Builder()
            .sslSocketFactory(context.socketFactory, capture)
            .hostnameVerifier { _, _ -> true }
            .readTimeout(30, TimeUnit.SECONDS)
            .build()
    }

    /** The server leaf the TLS handshake presented. The request itself gets 401 (no client certificate) and is ignored. */
    fun serverLeaf(): X509Certificate {
        client.newCall(Request.Builder().url("https://$address/v1/snapshot?since=-1").build()).execute().use { }
        return capture.leaf ?: error("the hub presented no certificate")
    }

    /** POST /v1/pair. Returns the response body, or throws [PairingRefused] on a 4xx. */
    fun post(deviceId: String, csrDer: ByteArray, proof: ByteArray): JSONObject {
        val body = JSONObject()
            .put("deviceId", deviceId)
            .put("deviceName", "A0.8 test device")
            .put("os", "android")
            .put("csrBase64", b64(csrDer))
            .put("proof", b64(proof))
            .toString()
        val request = Request.Builder().url("https://$address/v1/pair").post(body.toRequestBody("application/json".toMediaType())).build()
        client.newCall(request).execute().use { response ->
            val text = response.body?.string().orEmpty()
            if (!response.isSuccessful) throw PairingRefused(response.code)
            return JSONObject(text)
        }
    }
}

/** The agent's side of pairing, as HubLink.PairAsync: proof, CSR, the hub's response proof, leaf-to-CA check, key match. */
fun pairDevice(address: String, code: String, deviceId: String = "android-test"): Paired {
    val pairing = Pairing(address)
    val leaf = pairing.serverLeaf()
    val keys = Pki.newKeyPair()
    val csr = Pki.csrDer(keys, deviceId)
    val proof = Proof.mac(code, Proof.agentProofData(csr, leaf.encoded))
    val response = pairing.post(deviceId, csr, proof)

    val clientDer = unb64(response.getString("clientCertificateBase64"))
    val caDer = unb64(response.getString("hubCaCertificateBase64"))
    if (!Proof.equal(Proof.mac(code, clientDer, caDer), unb64(response.getString("proof")))) {
        throw IllegalStateException("the hub could not prove it knows the pairing code")
    }
    val ca = certFromDer(caDer)
    if (!Trust.isServerLeaf(leaf, ca)) throw IllegalStateException("the hub's certificate does not match its CA")
    val client = certFromDer(clientDer)
    if (!client.publicKey.encoded.contentEquals(keys.public.encoded)) throw IllegalStateException("the issued certificate is for another key")
    return Paired(response.getString("hubId"), Identity(keys, arrayOf(client)), ca, leaf)
}
