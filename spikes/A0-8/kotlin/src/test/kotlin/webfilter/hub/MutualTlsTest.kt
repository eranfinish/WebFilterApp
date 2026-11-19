package webfilter.hub

import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.SSLPeerUnverifiedException
import org.bouncycastle.asn1.x509.KeyPurposeId
import okhttp3.Dns
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.TlsVersion
import org.json.JSONObject
import java.io.IOException
import java.net.InetAddress
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * Criterion 1: mutual TLS against the real hub. The hub's TLS layer accepts any client certificate (AllowAnyClientCertificate),
 * so a rejected client gets 401 from the application check (HubServer.Authorize), not a handshake failure.
 */
class MutualTlsTest {
    private fun get(client: OkHttpClient, url: String): Response = client.newCall(Request.Builder().url(url).build()).execute()

    @Test
    fun pairedDeviceWithItsCertificateReadsTheSnapshot() {
        HubProcess.start().use { hub ->
            val paired = pairDevice(hub.address, hub.code)
            val client = HubHttp.client(paired.ca, paired.identity)
            get(client, "https://${hub.address}/v1/snapshot?since=-1").use { response ->
                assertEquals(200, response.code)
                val protocol = response.handshake?.tlsVersion
                println("negotiated TLS: $protocol")
                assertTrue(protocol == TlsVersion.TLS_1_3 || protocol == TlsVersion.TLS_1_2)
                assertTrue(JSONObject(response.body!!.string()).has("version"))
            }
        }
    }

    @Test
    fun clientWithoutCertificateIsRejectedWith401() {
        HubProcess.start().use { hub ->
            val paired = pairDevice(hub.address, hub.code)
            // Same pinned CA, but no identity: the server asks, the client has nothing to send.
            val client = HubHttp.client(paired.ca, identity = null)
            get(client, "https://${hub.address}/v1/snapshot?since=-1").use { response ->
                assertEquals(401, response.code)
            }
        }
    }

    @Test
    fun clientCertificateFromAnotherCaIsRejectedWith401() {
        HubProcess.start().use { hub ->
            val paired = pairDevice(hub.address, hub.code)
            val (foreignKeys, foreignCa) = Pki.foreignCa()
            val keys = Pki.newKeyPair()
            val intruder = Identity(keys, arrayOf(Pki.endEntity(keys, foreignKeys, foreignCa, "intruder", KeyPurposeId.id_kp_clientAuth)))
            val client = HubHttp.client(paired.ca, intruder)
            get(client, "https://${hub.address}/v1/snapshot?since=-1").use { response ->
                assertEquals(401, response.code)
            }
        }
    }

    @Test
    fun tls12OnlyWithClientCertificateWorks() {
        // The Galaxy S8 runs Android 9, where TLS 1.3 is not on by default. The same request, forced to TLS 1.2.
        HubProcess.start().use { hub ->
            val paired = pairDevice(hub.address, hub.code)
            val client = HubHttp.client(paired.ca, paired.identity, tls12Only = true)
            get(client, "https://${hub.address}/v1/snapshot?since=-1").use { response ->
                assertEquals(200, response.code)
                assertEquals(TlsVersion.TLS_1_2, response.handshake?.tlsVersion)
            }
        }
    }

    @Test
    fun pinnedWrongCaFailsTheHandshake() {
        HubProcess.start().use { hub ->
            val paired = pairDevice(hub.address, hub.code)
            val (_, foreignCa) = Pki.foreignCa()
            val client = HubHttp.client(foreignCa, paired.identity)
            assertFailsWith<SSLHandshakeException> { get(client, "https://${hub.address}/v1/snapshot?since=-1").close() }
        }
    }

    @Test
    fun pinnedCaAcceptsAHostNameTheCertificateDoesNotCover() {
        // The hub's certificate names the machine, localhost and 127.0.0.1. "hub.test" is not one of them, so the
        // platform's default host check fails, while the plan's CA-based verifier accepts it: a hub on any address works.
        HubProcess.start().use { hub ->
            val paired = pairDevice(hub.address, hub.code)
            val dns = object : Dns {
                override fun lookup(hostname: String): List<InetAddress> =
                    if (hostname == "hub.test") listOf(InetAddress.getByName("127.0.0.1")) else Dns.SYSTEM.lookup(hostname)
            }

            val defaultCheck = HubHttp.client(paired.ca, paired.identity, pinnedHostnames = false, dns = dns)
            assertFailsWith<SSLPeerUnverifiedException> { get(defaultCheck, "https://hub.test:${hub.port}/v1/snapshot?since=-1").close() }

            val pinned = HubHttp.client(paired.ca, paired.identity, pinnedHostnames = true, dns = dns)
            get(pinned, "https://hub.test:${hub.port}/v1/snapshot?since=-1").use { response -> assertEquals(200, response.code) }
        }
    }
}
