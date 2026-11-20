package com.webfilter.spike.a02b

import com.webfilter.spike.a02b.net.Be
import com.webfilter.spike.a02b.net.Icmp
import com.webfilter.spike.a02b.net.Ipv4
import com.webfilter.spike.a02b.net.Ipv4Packet
import com.webfilter.spike.a02b.net.PROTO_ICMP
import com.webfilter.spike.a02b.net.PROTO_TCP
import com.webfilter.spike.a02b.net.PROTO_UDP
import com.webfilter.spike.a02b.net.Tcp
import com.webfilter.spike.a02b.net.TcpHeader
import com.webfilter.spike.a02b.net.Udp
import com.webfilter.spike.a02b.dns.Dns
import com.webfilter.spike.a02b.vpn.Forwarder
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Runs the forwarder against real loopback sockets. The "client" is this test: it crafts IPv4 packets as the tun would
 * deliver them and reads the forwarder's replies from the sink.
 */
class ForwarderTest {
    private val client = (10 shl 24) or (8 shl 16) or 2          // 10.8.0.2
    private val tunIp = (10 shl 24) or (8 shl 16) or 1           // 10.8.0.1 (our resolver)
    private val loop = (127 shl 24) or 1                          // 127.0.0.1

    private class Harness {
        val out = LinkedBlockingQueue<ByteArray>()
        val logs = LinkedBlockingQueue<String>()
        val fwd = Forwarder(
            myIp = (10 shl 24) or (8 shl 16) or 1,
            fakeDnsIp = (10 shl 24) or (8 shl 16) or 1,
            upstreamDns = { null },
            mtu = 1400,
            sockets = object : Forwarder.Sockets {
                override fun tcp(): Socket = Socket()
                override fun udp(): DatagramSocket = DatagramSocket()
            },
            sink = object : Forwarder.Sink { override fun output(pkt: ByteArray) { out.add(pkt) } },
            log = { logs.add(it) },
        )
        fun feed(pkt: ByteArray) = fwd.onPacket(pkt, pkt.size)
        fun next(timeoutMs: Long = 3000): ByteArray = assertNotNull(out.poll(timeoutMs, TimeUnit.MILLISECONDS), "no packet from forwarder")
        fun nextTcp(timeoutMs: Long = 3000): Pair<Ipv4Packet, TcpHeader> {
            while (true) {
                val b = next(timeoutMs)
                val p = Ipv4.parse(b, b.size) ?: continue
                if (p.protocol != PROTO_TCP) continue
                return p to assertNotNull(Tcp.parse(p))
            }
        }
    }

    private fun payloadOf(p: Ipv4Packet, h: TcpHeader): ByteArray = p.raw.copyOfRange(h.payloadOff, h.payloadOff + h.payloadLen)

    @Test
    fun tcpHandshakeDataAndCloseThroughRealSocket() {
        val server = ServerSocket(0, 5, java.net.InetAddress.getByName("127.0.0.1"))
        val got = LinkedBlockingQueue<String>()
        thread(isDaemon = true) {
            server.accept().use { s ->
                val buf = ByteArray(5)
                var n = 0
                while (n < 5) { val r = s.getInputStream().read(buf, n, 5 - n); if (r < 0) break; n += r }
                got.add(String(buf, 0, n))
                s.getOutputStream().write("WORLD".toByteArray())
                s.shutdownOutput()
                // Hold the socket until the test ends; the forwarder closes its side on EOF.
                Thread.sleep(3000)
            }
        }
        val h = Harness()
        val port = server.localPort
        val clientPort = 40000
        val isn = 1000L

        // 1. SYN -> SYN-ACK with MSS
        h.feed(Tcp.buildIpv4(client, loop, clientPort, port, seq = isn, ack = 0, flags = Tcp.SYN, window = 65535, mss = 1460))
        val (sp, synack) = h.nextTcp()
        assertTrue(synack.syn && synack.ackSet, "SYN-ACK")
        assertEquals(isn + 1, synack.ack)
        assertNotNull(synack.mss)
        assertTrue(synack.mss!! <= 1360, "MSS clamped to the tun MTU: ${synack.mss}")
        val serverIss = synack.seq
        assertEquals(loop, sp.src)

        // 2. ACK + "HELLO" from the client
        h.feed(Tcp.buildIpv4(client, loop, clientPort, port, seq = isn + 1, ack = serverIss + 1, flags = Tcp.PSH or Tcp.ACK, window = 65535, payload = "HELLO".toByteArray()))
        assertEquals("HELLO", got.poll(3, TimeUnit.SECONDS), "upstream got the client's bytes")

        // 3. Data from upstream, then ACK from the client for it
        var dataSeq = -1L
        val received = StringBuilder()
        while (received.length < 5) {
            val (p, t) = h.nextTcp()
            if (t.payloadLen > 0) {
                if (dataSeq < 0) dataSeq = t.seq
                received.append(String(payloadOf(p, t)))
            }
            if (t.fin) break
        }
        assertEquals("WORLD", received.toString())
        h.feed(Tcp.buildIpv4(client, loop, clientPort, port, seq = isn + 6, ack = dataSeq + 5, flags = Tcp.ACK, window = 65535))

        // 4. Upstream closed: forwarder sends FIN. Client ACKs it and sends its own FIN.
        var fin: TcpHeader? = null
        while (fin == null) {
            val (_, t) = h.nextTcp()
            if (t.fin) fin = t
        }
        assertEquals(dataSeq + 5, fin!!.seq)
        h.feed(Tcp.buildIpv4(client, loop, clientPort, port, seq = isn + 6, ack = fin.seq + 1, flags = Tcp.FIN or Tcp.ACK, window = 65535))
        // The forwarder ACKs the client's FIN.
        var sawFinAck = false
        val deadline = System.currentTimeMillis() + 3000
        while (!sawFinAck && System.currentTimeMillis() < deadline) {
            val b = h.out.poll(500, TimeUnit.MILLISECONDS) ?: continue
            val p = Ipv4.parse(b, b.size) ?: continue
            val t = Tcp.parse(p) ?: continue
            if (p.protocol == PROTO_TCP && t.ackSet && t.ack == isn + 7) sawFinAck = true
        }
        assertTrue(sawFinAck, "client FIN acknowledged")
        server.close()
    }

    @Test
    fun connectFailureAnswersWithReset() {
        val closed = ServerSocket(0); val port = closed.localPort; closed.close()
        val h = Harness()
        h.feed(Tcp.buildIpv4(client, loop, 40001, port, seq = 500L, ack = 0, flags = Tcp.SYN, window = 65535))
        val (_, t) = h.nextTcp(6000)
        assertTrue(t.rst, "RST for a refused connect")
        assertTrue(h.logs.any { it.contains("tcp connect failed") })
    }

    @Test
    fun tcpToPrivateDnsIsResetNotDropped() {
        val h = Harness()
        h.feed(Tcp.buildIpv4(client, tunIp, 40002, 853, seq = 9L, ack = 0, flags = Tcp.SYN, window = 65535))
        val (_, t) = h.nextTcp()
        assertTrue(t.rst)
        assertEquals(1L, h.fwd.counters["tcp853_rst"]!!.get())
    }

    @Test
    fun dnsForBlockedNameGetsNxdomainLocally() {
        val h = Harness()
        val q = query(0x4242, "blocked.example")
        h.feed(Udp.buildIpv4(client, tunIp, 50000, 53, q))
        val b = h.next()
        val p = assertNotNull(Ipv4.parse(b, b.size))
        assertEquals(PROTO_UDP, p.protocol)
        assertEquals(tunIp, p.src, "answer comes from the resolver address")
        val payloadOff = p.payloadOff + 8
        assertEquals(0x4242, Be.u16(b, payloadOff))
        assertEquals(3, Be.u16(b, payloadOff + 2) and 0x0F, "NXDOMAIN")
        assertEquals(1L, h.fwd.counters["dns_blocked"]!!.get())
    }

    @Test
    fun udp443IsRejectedWithIcmpPortUnreachable() {
        val h = Harness()
        h.feed(Udp.buildIpv4(client, loop, 50001, 443, ByteArray(40)))
        val b = h.next()
        val p = assertNotNull(Ipv4.parse(b, b.size))
        assertEquals(PROTO_ICMP, p.protocol)
        assertEquals(3, Be.u8(b, p.payloadOff))
        assertEquals(3, Be.u8(b, p.payloadOff + 1))
        assertEquals(1L, h.fwd.counters["udp443_rejected"]!!.get())
    }

    @Test
    fun udpEchoThroughRealSocket() {
        val echo = DatagramSocket(0, java.net.InetAddress.getByName("127.0.0.1"))
        thread(isDaemon = true) {
            val buf = ByteArray(512)
            val dp = java.net.DatagramPacket(buf, buf.size)
            echo.receive(dp)
            echo.send(java.net.DatagramPacket(buf, dp.length, dp.socketAddress))
        }
        val h = Harness()
        h.feed(Udp.buildIpv4(client, loop, 51000, echo.localPort, "ping".toByteArray()))
        val b = h.next()
        val p = assertNotNull(Ipv4.parse(b, b.size))
        assertEquals(PROTO_UDP, p.protocol)
        assertEquals(loop, p.src, "reply appears to come from the address the app sent to")
        assertEquals(51000, Be.u16(b, p.payloadOff + 2))
        assertEquals("ping", String(b, p.payloadOff + 8, 4))
        echo.close()
    }

    @Test
    fun ipv6IsRejectedWithIcmpv6() {
        val h = Harness()
        val v6 = ByteArray(40 + 8)
        v6[0] = 0x60
        Be.put16(v6, 4, 8)
        v6[6] = PROTO_UDP.toByte()
        v6[7] = 64
        for (i in 8 until 24) v6[i] = 0x20          // src
        for (i in 24 until 40) v6[i] = 0x26         // dst
        h.feed(v6)
        val b = h.next()
        assertEquals(6, Be.u8(b, 0) ushr 4)
        assertEquals(58, Be.u8(b, 6), "next header ICMPv6")
        assertEquals(1, Be.u8(b, 40), "destination unreachable")
        assertEquals(1L, h.fwd.counters["v6_rejected"]!!.get())
    }

    @Test
    fun unknownProtocolIsRejectedWithIcmp() {
        val h = Harness()
        val pkt = Ipv4.build(client, loop, 47, ByteArray(8))
        h.feed(pkt)
        val b = h.next()
        val p = assertNotNull(Ipv4.parse(b, b.size))
        assertEquals(PROTO_ICMP, p.protocol)
        assertEquals(3, Be.u8(b, p.payloadOff))
        assertEquals(2, Be.u8(b, p.payloadOff + 1))
    }

    private fun query(id: Int, name: String): ByteArray {
        val labels = name.split('.')
        val b = ByteArray(12 + labels.sumOf { it.length + 1 } + 1 + 4)
        Be.put16(b, 0, id); Be.put16(b, 2, 0x0100); Be.put16(b, 4, 1)
        var o = 12
        for (l in labels) { b[o++] = l.length.toByte(); for (c in l) b[o++] = c.code.toByte() }
        b[o++] = 0
        Be.put16(b, o, 1); Be.put16(b, o + 2, 1)
        assertTrue(Dns.parseQuestion(b, b.size) != null)
        return b
    }
}
