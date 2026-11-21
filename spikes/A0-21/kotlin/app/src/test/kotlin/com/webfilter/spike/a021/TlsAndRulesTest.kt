package com.webfilter.spike.a021

import com.webfilter.spike.a021.dns.BlockRules
import com.webfilter.spike.a021.dns.Policy
import com.webfilter.spike.a021.net.Tls
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.assertFalse

class TlsAndRulesTest {

    /** A minimal TLS 1.2 ClientHello record. [sni] null means no server_name extension at all. */
    private fun clientHello(sni: String?): ByteArray {
        val body = java.io.ByteArrayOutputStream()
        body.write(0x03); body.write(0x03)                 // client version
        body.write(ByteArray(32))                          // random
        body.write(0)                                      // session id length
        body.write(0); body.write(2); body.write(0x13); body.write(0x01) // cipher suites
        body.write(1); body.write(0)                       // compression methods
        val ext = java.io.ByteArrayOutputStream()
        if (sni != null) {
            val name = sni.toByteArray(Charsets.US_ASCII)
            val list = java.io.ByteArrayOutputStream()
            list.write(0)                                  // name type: host_name
            list.write(name.size shr 8); list.write(name.size and 0xFF)
            list.write(name)
            val sniData = java.io.ByteArrayOutputStream()
            sniData.write(list.size() shr 8); sniData.write(list.size() and 0xFF)
            sniData.write(list.toByteArray())
            ext.write(0); ext.write(0)                     // extension type: server_name
            ext.write(sniData.size() shr 8); ext.write(sniData.size() and 0xFF)
            ext.write(sniData.toByteArray())
        }
        // An unrelated extension first, so the parser must skip it.
        val other = byteArrayOf(0x00, 0x17, 0x00, 0x00)
        val exts = other + ext.toByteArray()
        body.write(exts.size shr 8); body.write(exts.size and 0xFF)
        body.write(exts)
        val hs = java.io.ByteArrayOutputStream()
        hs.write(0x01)                                     // ClientHello
        val n = body.size()
        hs.write(n shr 16); hs.write((n shr 8) and 0xFF); hs.write(n and 0xFF)
        hs.write(body.toByteArray())
        val rec = java.io.ByteArrayOutputStream()
        rec.write(0x16); rec.write(0x03); rec.write(0x01)
        rec.write(hs.size() shr 8); rec.write(hs.size() and 0xFF)
        rec.write(hs.toByteArray())
        return rec.toByteArray()
    }

    @AfterTest
    fun resetPolicy() {
        Policy.dohNames = false; Policy.dohIp = false; Policy.sni = false; Policy.dohIps = emptySet()
    }

    @Test
    fun readsSniFromClientHello() {
        val b = clientHello("en.wikipedia.org")
        assertEquals("en.wikipedia.org", Tls.sni(b, b.size))
    }

    @Test
    fun sniIsLowercased() {
        val b = clientHello("DNS.Google")
        assertEquals("dns.google", Tls.sni(b, b.size))
    }

    @Test
    fun noServerNameExtensionGivesNull() {
        val b = clientHello(null)
        assertNull(Tls.sni(b, b.size))
    }

    @Test
    fun nonHandshakeRecordGivesNull() {
        val b = clientHello("example.org")
        b[0] = 0x17 // application data
        assertNull(Tls.sni(b, b.size))
    }

    @Test
    fun truncatedRecordGivesNullNotException() {
        val b = clientHello("example.org")
        for (cut in 0 until b.size) assertNull(Tls.sni(b, cut), "cut at $cut")
    }

    @Test
    fun dnsLayerOnlyBlocksDohNamesWhenSwitchedOn() {
        assertFalse(BlockRules.isBlocked("dns.google"))
        Policy.dohNames = true
        assertTrue(BlockRules.isBlocked("dns.google"))
        assertTrue(BlockRules.isBlocked("cloudflare-dns.com."))
        assertTrue(BlockRules.isBlocked("en.wikipedia.org"))
        assertFalse(BlockRules.isBlocked("example.org"))
    }

    @Test
    fun sniLayerIncludesDohNamesAndBlockList() {
        assertTrue(BlockRules.isBlockedSni("dns.google"))            // independent of the DNS switch
        assertTrue(BlockRules.isBlockedSni("EN.wikipedia.org"))
        assertFalse(BlockRules.isBlockedSni("example.org"))
        assertFalse(BlockRules.isBlockedSni("notwikipedia.org.example"))
    }
}
