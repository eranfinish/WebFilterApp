package com.webfilter.spike.a02.dns

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** JVM tests for the spike's packet code. They prove the parser and builders, not Android behavior. */
class DnsPacketTest {

    private fun query(id: Int, name: String, qtype: Int, rd: Boolean = true): ByteArray {
        val labels = name.split('.').filter { it.isNotEmpty() }
        val wire = ArrayList<Byte>()
        for (l in labels) {
            wire.add(l.length.toByte())
            l.forEach { wire.add(it.code.toByte()) }
        }
        wire.add(0)
        val out = ByteArray(12 + wire.size + 4)
        putU16(out, 0, id)
        putU16(out, 2, if (rd) 0x0100 else 0)
        putU16(out, 4, 1)
        for (i in wire.indices) out[12 + i] = wire[i]
        val t = 12 + wire.size
        putU16(out, t, qtype)
        putU16(out, t + 2, 1)
        return out
    }

    @Test
    fun parsesQuestionNameTypeAndEnd() {
        val q = DnsMessage.parseQuestion(query(0x1234, "blocked.example", DnsMessage.TYPE_A))
        assertNotNull(q)
        assertEquals("blocked.example", q.name)
        assertEquals(DnsMessage.TYPE_A, q.qtype)
        assertEquals(1, q.qclass)
        assertEquals(12 + 1 + 7 + 1 + 7 + 1 + 4, q.endOffset)
    }

    @Test
    fun lowercasesAsciiNames() {
        val q = DnsMessage.parseQuestion(query(1, "WWW.Example.COM", DnsMessage.TYPE_AAAA))
        assertEquals("www.example.com", q!!.name)
    }

    @Test
    fun rejectsResponsesCompressionAndTruncation() {
        val resp = query(1, "a.example", 1)
        putU16(resp, 2, 0x8000) // QR bit set: a response
        assertNull(DnsMessage.parseQuestion(resp))

        val compressed = query(2, "a.example", 1)
        compressed[12] = 0xC0.toByte() // compression pointer in the question
        assertNull(DnsMessage.parseQuestion(compressed))

        val truncated = query(3, "a.example", 1).copyOf(16)
        assertNull(DnsMessage.parseQuestion(truncated))

        assertNull(DnsMessage.parseQuestion(ByteArray(11)))
    }

    @Test
    fun nxdomainKeepsIdAndQuestionAndSetsFlags() {
        for (qtype in listOf(DnsMessage.TYPE_A, DnsMessage.TYPE_AAAA, DnsMessage.TYPE_HTTPS, DnsMessage.TYPE_SVCB)) {
            val q = query(0xBEEF, "x.blocked.example", qtype, rd = true)
            val parsed = DnsMessage.parseQuestion(q)!!
            val r = DnsMessage.nxdomainFor(q, parsed)
            assertEquals(0xBEEF, u16(r, 0), "ID echoed")
            val flags = u16(r, 2)
            assertTrue((flags and 0x8000) != 0, "QR set")
            assertTrue((flags and 0x0100) != 0, "RD copied")
            assertTrue((flags and 0x0080) != 0, "RA set")
            assertEquals(DnsMessage.RCODE_NXDOMAIN, flags and 0x000F, "RCODE NXDOMAIN")
            assertEquals(1, u16(r, 4), "QDCOUNT")
            assertEquals(0, u16(r, 6), "ANCOUNT")
            assertEquals(0, u16(r, 8), "NSCOUNT")
            assertEquals(0, u16(r, 10), "ARCOUNT")
            assertEquals(parsed.endOffset, r.size, "question copied, nothing after it")
            // parseQuestion rejects responses (QR set), so compare the question bytes directly.
            assertTrue(q.copyOfRange(12, parsed.endOffset).contentEquals(r.copyOfRange(12, parsed.endOffset)), "question bytes equal")
        }
    }

    @Test
    fun ipv4UdpRoundTripAndHeaderChecksum() {
        val src = byteArrayOf(10, 111, 0, 1)
        val dst = byteArrayOf(10, 111, 0, 53)
        val payload = query(7, "example.com", DnsMessage.TYPE_A)
        val pkt = Ipv4Udp.build(src, dst, 40000, 53, payload)
        assertEquals(0, Ipv4Udp.checksum(pkt, 0, 20), "header checksum verifies to 0")
        val dg = Ipv4Udp.parse(pkt)
        assertNotNull(dg)
        assertTrue(src.contentEquals(dg.srcIp))
        assertTrue(dst.contentEquals(dg.dstIp))
        assertEquals(40000, dg.srcPort)
        assertEquals(53, dg.dstPort)
        assertTrue(payload.contentEquals(dg.payload))
    }

    @Test
    fun ipv4ParserRejectsNonUdpFragmentsAndIpv6() {
        val src = byteArrayOf(10, 111, 0, 1)
        val dst = byteArrayOf(10, 111, 0, 53)
        val good = Ipv4Udp.build(src, dst, 1, 53, ByteArray(20))

        val tcp = good.copyOf()
        tcp[9] = 6
        assertNull(Ipv4Udp.parse(tcp), "TCP is not parsed as UDP")

        val frag = good.copyOf()
        putU16(frag, 6, 0x2000) // MF set
        assertNull(Ipv4Udp.parse(frag), "fragment rejected")

        val v6 = good.copyOf()
        v6[0] = 0x60
        assertNull(Ipv4Udp.parse(v6), "IPv6 rejected")

        assertNull(Ipv4Udp.parse(good, length = 20), "length shorter than headers rejected")
    }

    @Test
    fun rulesMatchExactNamesAndSubdomainsOnly() {
        assertTrue(Rules.isBlocked("blocked.example"))
        assertTrue(Rules.isBlocked("a.b.blocked.example"))
        assertTrue(Rules.isBlocked("blocked.test."))
        assertTrue(Rules.isBlocked("www.wikipedia.org"))
        assertTrue(Rules.isBlocked("wikipedia.org"))
        assertTrue(!Rules.isBlocked("example.com"))
        assertTrue(!Rules.isBlocked("xblocked.example"), "no match on a partial label")
        assertTrue(!Rules.isBlocked("blocked.example.com"), "suffix must align on a label")
        assertTrue(!Rules.isBlocked("notwikipedia.org"))
    }
}
