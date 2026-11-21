package com.webfilter.spike.a021

import com.webfilter.spike.a021.dns.BlockRules
import com.webfilter.spike.a021.dns.Dns
import com.webfilter.spike.a021.net.Be
import com.webfilter.spike.a021.net.Checksum
import com.webfilter.spike.a021.net.Icmp
import com.webfilter.spike.a021.net.Ipv4
import com.webfilter.spike.a021.net.PROTO_ICMP
import com.webfilter.spike.a021.net.PROTO_TCP
import com.webfilter.spike.a021.net.PROTO_UDP
import com.webfilter.spike.a021.net.Tcp
import com.webfilter.spike.a021.net.Udp
import com.webfilter.spike.a021.net.UdpParse
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class PacketTest {
    private val me = (10 shl 24) or (8 shl 16) or (0 shl 8) or 2   // 10.8.0.2
    private val peer = (93 shl 24) or (184 shl 16) or (216 shl 8) or 34

    private fun ipChecksumOk(p: ByteArray): Boolean =
        Checksum.fold(Checksum.sum(p, 0, 20)) == 0

    private fun transportChecksumOk(p: ByteArray, proto: Int): Boolean {
        val total = Be.u16(p, 2)
        val seg = total - 20
        val sum = Checksum.pseudoIpv4(Be.ip4(p, 12), Be.ip4(p, 16), proto, seg) + Checksum.sum(p, 20, seg)
        return Checksum.fold(sum) == 0
    }

    @Test
    fun ipv4BuildParseAndHeaderChecksum() {
        val payload = byteArrayOf(1, 2, 3, 4, 5)
        val pkt = Ipv4.build(me, peer, PROTO_UDP, payload)
        assertTrue(ipChecksumOk(pkt))
        val p = assertNotNull(Ipv4.parse(pkt, pkt.size))
        assertEquals(me, p.src)
        assertEquals(peer, p.dst)
        assertEquals(PROTO_UDP, p.protocol)
        assertEquals(5, p.payloadLen)
        assertEquals(64, p.ttl)
    }

    @Test
    fun ipv4RejectsShortVersionAndFragments() {
        assertNull(Ipv4.parse(ByteArray(10), 10))
        val pkt = Ipv4.build(me, peer, PROTO_UDP, ByteArray(8))
        pkt[0] = 0x65 // version 6 in an IPv4 slot
        assertNull(Ipv4.parse(pkt, pkt.size))
        val frag = Ipv4.build(me, peer, PROTO_UDP, ByteArray(8))
        Be.put16(frag, 6, 0x2000) // MF set
        assertNull(Ipv4.parse(frag, frag.size))
    }

    @Test
    fun tcpSynAckWithMssParsesAndChecksums() {
        val pkt = Tcp.buildIpv4(peer, me, 443, 50000, seq = 0xFFFFFFF0L, ack = 1234L,
            flags = Tcp.SYN or Tcp.ACK, window = 65535, mss = 1240)
        assertTrue(ipChecksumOk(pkt))
        assertTrue(transportChecksumOk(pkt, PROTO_TCP))
        val p = assertNotNull(Ipv4.parse(pkt, pkt.size))
        val h = assertNotNull(Tcp.parse(p))
        assertEquals(443, h.srcPort)
        assertEquals(50000, h.dstPort)
        assertEquals(0xFFFFFFF0L, h.seq)
        assertEquals(1234L, h.ack)
        assertTrue(h.syn && h.ackSet)
        assertEquals(1240, h.mss)
        assertEquals(0, h.payloadLen)
    }

    @Test
    fun tcpDataSegmentPayloadAndFlags() {
        val data = "GET / HTTP/1.1\r\n\r\n".toByteArray()
        val pkt = Tcp.buildIpv4(me, peer, 50000, 80, seq = 7L, ack = 9L, flags = Tcp.PSH or Tcp.ACK, window = 4096, payload = data)
        assertTrue(transportChecksumOk(pkt, PROTO_TCP))
        val p = assertNotNull(Ipv4.parse(pkt, pkt.size))
        val h = assertNotNull(Tcp.parse(p))
        assertEquals(data.size, h.payloadLen)
        assertFalse(h.syn)
        assertNull(h.mss)
        assertEquals("GET", String(pkt, h.payloadOff, 3))
    }

    @Test
    fun tcpParsesIncomingSynWithMssOption() {
        // Client SYN as Chrome sends it: MSS 1460 + SACK-permitted + window scale + NOP padding.
        val opts = byteArrayOf(2, 4, 5, 0xB4.toByte(), 4, 2, 1, 3, 3, 7, 1, 1)
        val seg = ByteArray(20 + opts.size)
        Be.put16(seg, 0, 50001); Be.put16(seg, 2, 443); Be.put32(seg, 4, 100); Be.put32(seg, 8, 0)
        seg[12] = (((20 + opts.size) / 4) shl 4).toByte(); seg[13] = Tcp.SYN.toByte(); Be.put16(seg, 14, 64240)
        System.arraycopy(opts, 0, seg, 20, opts.size)
        val pkt = Ipv4.build(me, peer, PROTO_TCP, seg, seg.size)
        val h = assertNotNull(Tcp.parse(assertNotNull(Ipv4.parse(pkt, pkt.size))))
        assertTrue(h.syn)
        assertEquals(1460, h.mss)
    }

    @Test
    fun udpBuildParseAndChecksum() {
        val pkt = Udp.buildIpv4(me, peer, 40000, 53, byteArrayOf(9, 8, 7))
        assertTrue(ipChecksumOk(pkt))
        assertTrue(transportChecksumOk(pkt, PROTO_UDP))
        val p = assertNotNull(Ipv4.parse(pkt, pkt.size))
        val u = assertNotNull(UdpParse.parse(p))
        assertEquals(40000, u.srcPort)
        assertEquals(53, u.dstPort)
        assertEquals(3, u.payloadLen)
    }

    @Test
    fun icmpUnreachableQuotesHeaderAndChecksum() {
        val offending = Udp.buildIpv4(me, peer, 40000, 443, ByteArray(20))
        val p = assertNotNull(Ipv4.parse(offending, offending.size))
        val icmp = Icmp.unreachableIpv4(p, peer, code = 3) // "from" the far end, back to the sender
        assertTrue(ipChecksumOk(icmp))
        val ip = assertNotNull(Ipv4.parse(icmp, icmp.size))
        assertEquals(PROTO_ICMP, ip.protocol)
        assertEquals(peer, ip.src)
        assertEquals(me, ip.dst)
        assertEquals(3, Be.u8(icmp, 20))
        assertEquals(3, Be.u8(icmp, 21))
        assertEquals(Checksum.fold(Checksum.sum(icmp, 20, icmp.size - 20)), 0)
    }

    private fun dnsQuery(id: Int, name: String, rd: Boolean = true): ByteArray {
        val labels = name.split('.')
        val qnameLen = labels.sumOf { it.length + 1 } + 1
        val b = ByteArray(12 + qnameLen + 4)
        Be.put16(b, 0, id)
        Be.put16(b, 2, if (rd) 0x0100 else 0)
        Be.put16(b, 4, 1)
        var o = 12
        for (l in labels) {
            b[o++] = l.length.toByte()
            for (c in l) b[o++] = c.code.toByte()
        }
        b[o++] = 0
        Be.put16(b, o, 1)     // A
        Be.put16(b, o + 2, 1) // IN
        return b
    }

    @Test
    fun dnsParsesQuestionAndNormalisesCase() {
        val q = dnsQuery(0xBEEF, "WWW.Wikipedia.ORG")
        val parsed = assertNotNull(Dns.parseQuestion(q, q.size))
        assertEquals(0xBEEF, parsed.id)
        assertEquals("www.wikipedia.org", parsed.name)
        assertEquals(1, parsed.qtype)
        assertEquals(1, parsed.qclass)
        assertEquals(q.size, parsed.questionEnd)
    }

    @Test
    fun dnsRejectsResponsesCompressionAndTruncation() {
        val resp = dnsQuery(1, "example.com"); Be.put16(resp, 2, 0x8000)
        assertNull(Dns.parseQuestion(resp, resp.size))
        val comp = dnsQuery(1, "example.com"); comp[12] = 0xC0.toByte()
        assertNull(Dns.parseQuestion(comp, comp.size))
        val q = dnsQuery(1, "example.com")
        assertNull(Dns.parseQuestion(q, q.size - 6))
    }

    @Test
    fun dnsNxdomainKeepsIdQuestionAndRd() {
        val q = dnsQuery(0x1234, "blocked.example", rd = true)
        val parsed = assertNotNull(Dns.parseQuestion(q, q.size))
        val r = Dns.reply(q, q.size, parsed, Dns.RCODE_NXDOMAIN)
        assertEquals(0x1234, Be.u16(r, 0))
        val flags = Be.u16(r, 2)
        assertTrue(flags and 0x8000 != 0, "QR")
        assertTrue(flags and 0x0100 != 0, "RD copied")
        assertTrue(flags and 0x0080 != 0, "RA")
        assertEquals(3, flags and 0x0F, "RCODE NXDOMAIN")
        assertEquals(1, Be.u16(r, 4))
        assertEquals(0, Be.u16(r, 6))
        assertEquals(q.size, r.size)
        assertEquals(q.size - 12, r.size - 12)
        // parseQuestion rejects responses by design, so compare the copied question bytes directly.
        assertTrue(r.copyOfRange(12, r.size).contentEquals(q.copyOfRange(12, q.size)), "question copied unchanged")
    }

    @Test
    fun blockRulesMatchSuffixOnly() {
        assertTrue(BlockRules.isBlocked("wikipedia.org"))
        assertTrue(BlockRules.isBlocked("en.wikipedia.org"))
        assertTrue(BlockRules.isBlocked("www.wikipedia.org."))
        assertTrue(BlockRules.isBlocked("blocked.example"))
        assertTrue(BlockRules.isBlocked("x.blocked.test"))
        assertFalse(BlockRules.isBlocked("notwikipedia.org"))
        assertFalse(BlockRules.isBlocked("example.com"))
        assertFalse(BlockRules.isBlocked("example.org"))
        assertFalse(BlockRules.isBlocked("neverssl.com"))
    }
}
