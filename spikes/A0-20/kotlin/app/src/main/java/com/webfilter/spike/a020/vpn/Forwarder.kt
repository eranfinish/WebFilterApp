package com.webfilter.spike.a020.vpn

import com.webfilter.spike.a020.dns.BlockRules
import com.webfilter.spike.a020.dns.Policy
import com.webfilter.spike.a020.net.Tls
import com.webfilter.spike.a020.dns.Dns
import com.webfilter.spike.a020.net.Be
import com.webfilter.spike.a020.net.Icmp
import com.webfilter.spike.a020.net.Ip6
import com.webfilter.spike.a020.net.Ipv4
import com.webfilter.spike.a020.net.Ipv4Packet
import com.webfilter.spike.a020.net.PROTO_ICMP
import com.webfilter.spike.a020.net.PROTO_TCP
import com.webfilter.spike.a020.net.PROTO_UDP
import com.webfilter.spike.a020.net.Tcp
import com.webfilter.spike.a020.net.TcpHeader
import com.webfilter.spike.a020.net.Udp
import com.webfilter.spike.a020.net.UdpParse
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicLong
import kotlin.concurrent.thread
import kotlin.random.Random

/**
 * User-space forwarder for a full tunnel (spike A0.20, option (a)).
 *
 * Packets come in from the tun fd via [onPacket]; replies go out via [Sink.output]. Upstream sockets come from
 * [Sockets]; on Android these call VpnService.protect(), so our own traffic leaves through the underlying network
 * and is not looped back into the tun.
 *
 * Scope: IPv4 TCP (small state machine: no window scaling, no SACK, no fast retransmit, RTO retransmit only),
 * IPv4 UDP (transparent relay per flow), DNS answered locally for blocked names and relayed for the rest,
 * and ICMP rejects for everything not forwarded, so that nothing is dropped silently.
 * Pure Kotlin, no android.* imports, so it is unit-tested on the JVM.
 */
class Forwarder(
    private val myIp: Int,
    private val fakeDnsIp: Int,
    private val upstreamDns: () -> InetSocketAddress?,
    private val mtu: Int,
    private val sockets: Sockets,
    private val sink: Sink,
    private val log: (String) -> Unit,
) {
    interface Sink { fun output(pkt: ByteArray) }

    interface Sockets {
        fun tcp(): Socket
        fun udp(): DatagramSocket
    }

    val counters = ConcurrentHashMap<String, AtomicLong>()
    private fun count(name: String) { counters.getOrPut(name) { AtomicLong() }.incrementAndGet() }
    fun stats(): String = counters.entries.sortedBy { it.key }.joinToString(" ") { "${it.key}=${it.value.get()}" }

    private val udpFlows = ConcurrentHashMap<String, UdpFlow>()
    private val tcpConns = ConcurrentHashMap<String, TcpConn>()
    private val myMss = mtu - 40
    private var packetCount = 0L

    @Volatile var closed = false

    init {
        // Retransmission timer: the reader thread may be blocked in read() when a segment is lost.
        thread(name = "tcp-rto", isDaemon = true) {
            while (!closed) {
                try { Thread.sleep(100) } catch (_: InterruptedException) { break }
                for (c in tcpConns.values) synchronized(c) { if (c.state != TcpState.CLOSED) retransmitIfDue(c) }
            }
        }
    }

    // ---------- dispatch (called from the single tun reader thread) ----------

    fun onPacket(buf: ByteArray, len: Int) {
        if (len < 1) return
        if (++packetCount % 256 == 0L) sweepUdp()
        when (Be.u8(buf, 0) ushr 4) {
            4 -> {
                val p = Ipv4.parse(buf, len)
                if (p == null) { count("v4_malformed_or_fragment"); return }
                when (p.protocol) {
                    PROTO_TCP -> onTcp(p)
                    PROTO_UDP -> onUdp(p)
                    PROTO_ICMP -> { count("icmp_rejected"); sink.output(Icmp.unreachableIpv4(p, myIp, 13)) }
                    else -> { count("proto_rejected_${p.protocol}"); sink.output(Icmp.unreachableIpv4(p, myIp, 2)) }
                }
            }
            6 -> {
                val p = Ip6.parse(buf, len)
                if (p == null) { count("v6_malformed"); return }
                count("v6_rejected")
                sink.output(Ip6.unreachable(p, 0)) // no route: apps fall back to IPv4 quickly
            }
            else -> count("bad_version")
        }
    }

    // ---------- UDP ----------

    private fun onUdp(p: Ipv4Packet) {
        val u = UdpParse.parse(p) ?: run { count("udp_malformed"); return }
        if (u.dstPort == 443) {
            // QUIC (HTTP/3) is refused with ICMP port unreachable so Chrome falls back to TCP at once.
            count("udp443_rejected")
            sink.output(Icmp.unreachableIpv4(p, myIp, 3))
            return
        }
        if (u.dstPort == 53) {
            val payload = p.raw.copyOfRange(u.payloadOff, u.payloadOff + u.payloadLen)
            val q = Dns.parseQuestion(payload, payload.size)
            if (q != null && BlockRules.isBlocked(q.name)) {
                count("dns_blocked")
                val reply = Dns.reply(payload, payload.size, q, Dns.RCODE_NXDOMAIN)
                sink.output(Udp.buildIpv4(p.dst, p.src, u.dstPort, u.srcPort, reply))
                log("A020 dns blocked name=${q.name}")
                return
            }
            count("dns_relayed")
        }
        // DNS sent to our fake resolver IP goes to the real upstream; everything else to its original destination.
        // If the upstream is not known yet, the DNS query is refused (SERVFAIL-like ICMP) instead of relayed to ourselves.
        val up = if (p.dst == fakeDnsIp) upstreamDns() else null
        if (p.dst == fakeDnsIp && up == null) {
            count("dns_no_upstream")
            sink.output(Icmp.unreachableIpv4(p, myIp, 1))
            return
        }
        val target = up ?: InetSocketAddress(addr(p.dst), u.dstPort)
        val key = "${p.src}:${u.srcPort}>${p.dst}:${u.dstPort}"
        val flow = try {
            udpFlows.getOrPut(key) { UdpFlow(p.src, u.srcPort, p.dst, u.dstPort, key) }
        } catch (e: IOException) {
            // protect() failed or no socket: answer at once rather than dropping silently.
            count("udp_socket_fail")
            sink.output(Icmp.unreachableIpv4(p, myIp, 1))
            return
        }
        flow.lastUse = System.currentTimeMillis()
        try {
            flow.socket.send(DatagramPacket(p.raw, u.payloadOff, u.payloadLen, target))
            count("udp_out")
        } catch (e: IOException) {
            count("udp_send_err")
            udpFlows.remove(key)?.close()
        }
    }

    private inner class UdpFlow(val clientIp: Int, val clientPort: Int, val origDstIp: Int, val origDstPort: Int, val key: String) {
        val socket: DatagramSocket = sockets.udp()
        @Volatile var lastUse = System.currentTimeMillis()

        init {
            thread(name = "udp-$key", isDaemon = true) {
                val buf = ByteArray(2048)
                try {
                    while (!closed) {
                        val dp = DatagramPacket(buf, buf.size)
                        socket.receive(dp)
                        lastUse = System.currentTimeMillis()
                        // Replies are sent as if from the address the app sent to, so its socket accepts them.
                        sink.output(Udp.buildIpv4(origDstIp, clientIp, origDstPort, clientPort, buf.copyOf(dp.length)))
                        count("udp_in")
                    }
                } catch (_: IOException) {
                    // closed or swept
                } finally {
                    udpFlows.remove(key, this)
                    socket.close()
                }
            }
        }

        fun close() { socket.close() }
    }

    private fun sweepUdp() {
        val now = System.currentTimeMillis()
        udpFlows.values.forEach { if (now - it.lastUse > 30_000) { udpFlows.remove(it.key, it); it.close() } }
    }

    // ---------- TCP ----------

    private inner class TcpConn(val clientIp: Int, val clientPort: Int, val serverIp: Int, val serverPort: Int, val key: String) {
        var state = TcpState.CONNECTING
        var socket: Socket? = null
        val iss: Long = Random.nextLong(0, 0x100000000L)
        var sndUna = iss          // oldest byte we sent that the client has not acknowledged
        var sndNxt = iss          // next byte we will send
        var rcvNxt = 0L           // next byte we expect from the client
        var clientWindow = 65535  // client's advertised window (unscaled)
        var connMss = myMss
        var clientFinSeen = false
        var finSent = false
        var finAcked = false
        var queuedBytes = 0       // client bytes accepted but not yet written upstream
        var rto = 500L
        var sniChecked = false    // layer "sni": only the first client payload on port 443 is checked
        val toUpstream = LinkedBlockingQueue<ByteArray>() // empty array = half-close
        val unacked = ArrayDeque<Segment>()
    }

    private class Segment(val seq: Long, val data: ByteArray, var sentAt: Long)

    private fun onTcp(p: Ipv4Packet) {
        val h = Tcp.parse(p) ?: run { count("tcp_malformed"); return }
        val key = "${p.src}:${h.srcPort}>${p.dst}:${h.dstPort}"
        val conn = tcpConns[key]
        if (conn == null) {
            if (h.rst) return
            if (!h.syn || h.ackSet) {
                // A segment for a connection we do not have: reset it so the app stops retrying.
                count("tcp_no_conn_rst")
                if (h.ackSet) sendRst(p.dst, p.src, h.dstPort, h.srcPort, h.ack, 0L, ackSet = true)
                else sendRst(p.dst, p.src, h.dstPort, h.srcPort, 0L, (h.seq + h.payloadLen + 1) and MASK, ackSet = false)
                return
            }
            // Debug trace of every new TCP flow (A0.20): lets a probe's own SYN be told from background traffic.
            log("A020 syn ${addr(p.dst).hostAddress}:${h.dstPort} srcPort=${h.srcPort}")
            if (h.dstPort == 853 || h.dstPort == 53) {
                // Private DNS over TLS and TCP DNS are not forwarded in this spike: refuse at once, no silent drop.
                count(if (h.dstPort == 853) "tcp853_rst" else "tcp53_rst")
                sendRst(p.dst, p.src, h.dstPort, h.srcPort, 0L, (h.seq + 1) and MASK, ackSet = false)
                return
            }
            // Layer "ip" (DoH IPs, switched on by the DPC): refuse the SYN, so no upstream connection is made.
            if (Policy.dohIp && Policy.dohIps.contains(addr(p.dst).hostAddress ?: "")) {
                count("doh_ip_rst")
                log("A020 doh ip refused ${addr(p.dst).hostAddress}:${h.dstPort}")
                sendRst(p.dst, p.src, h.dstPort, h.srcPort, 0L, (h.seq + 1) and MASK, ackSet = false)
                return
            }
            count("tcp_syn")
            val c = TcpConn(p.src, h.srcPort, p.dst, h.dstPort, key)
            c.rcvNxt = (h.seq + 1) and MASK
            c.connMss = minOf(h.mss ?: myMss, myMss)
            tcpConns[key] = c
            thread(name = "tcp-connect-$key", isDaemon = true) { connectUpstream(c) }
            return
        }
        synchronized(conn) { handleClientSegment(conn, h, p) }
    }

    private fun connectUpstream(c: TcpConn) {
        var s: Socket? = null
        try {
            // sockets.tcp() throws if protect() fails; an unprotected connect would loop back into our own tun.
            val sock = sockets.tcp()
            s = sock
            sock.connect(InetSocketAddress(addr(c.serverIp), c.serverPort), 5000)
        } catch (e: IOException) {
            count("tcp_connect_fail")
            log("A020 tcp connect failed ${c.key}: ${e.javaClass.simpleName} ${e.message}")
            try { s?.close() } catch (_: IOException) {}
            synchronized(c) {
                sendRst(c.serverIp, c.clientIp, c.serverPort, c.clientPort, 0L, c.rcvNxt, ackSet = false)
                c.state = TcpState.CLOSED
            }
            tcpConns.remove(c.key, c)
            return
        }
        count("tcp_connect_ok")
        synchronized(c) {
            c.socket = s
            c.state = TcpState.SYN_ACKED_WAIT_ACK
            sink.output(Tcp.buildIpv4(c.serverIp, c.clientIp, c.serverPort, c.clientPort,
                seq = c.iss, ack = c.rcvNxt, flags = Tcp.SYN or Tcp.ACK, window = 65535, mss = c.connMss))
            c.sndNxt = (c.iss + 1) and MASK
        }
        thread(name = "tcp-writer-${c.key}", isDaemon = true) { writerLoop(c) }
        thread(name = "tcp-reader-${c.key}", isDaemon = true) { readerLoop(c) }
    }

    /** Called with the connection's monitor held. */
    private fun handleClientSegment(c: TcpConn, h: TcpHeader, p: Ipv4Packet) {
        if (c.state == TcpState.CLOSED) return
        if (h.rst) {
            count("tcp_client_rst")
            closeConn(c)
            return
        }
        if (h.syn) {
            // Duplicate SYN: repeat the SYN-ACK.
            sink.output(Tcp.buildIpv4(c.serverIp, c.clientIp, c.serverPort, c.clientPort,
                seq = c.iss, ack = c.rcvNxt, flags = Tcp.SYN or Tcp.ACK, window = 65535, mss = c.connMss))
            return
        }
        if (c.state == TcpState.CONNECTING) return // upstream not connected yet; the client will retransmit

        if (h.ackSet) {
            val ack = h.ack
            val newly = seqDiff(ack, c.sndUna)
            if (newly != 0L && newly <= seqDiff(c.sndNxt, c.sndUna)) {
                c.sndUna = ack
                while (c.unacked.isNotEmpty()) {
                    val s = c.unacked.first()
                    val d = seqDiff((s.seq + s.data.size) and MASK, ack)
                    if (d != 0L && d < HALF) break // segment is not fully acknowledged yet
                    c.unacked.removeFirst()
                }
                c.rto = 500L
                if (c.state == TcpState.SYN_ACKED_WAIT_ACK) c.state = TcpState.ESTABLISHED
                if (c.finSent && c.sndUna == c.sndNxt) c.finAcked = true
                (c as java.lang.Object).notifyAll()
            }
            c.clientWindow = h.window
        }

        val plen = h.payloadLen
        if (plen > 0) {
            if (h.seq == c.rcvNxt && c.queuedBytes + plen <= 262144) {
                val data = p.raw.copyOfRange(h.payloadOff, h.payloadOff + plen)
                // Layer "sni": a ClientHello whose SNI is blocked is reset here, and nothing more is relayed.
                if (Policy.sni && c.serverPort == 443 && !c.sniChecked) {
                    c.sniChecked = true
                    val name = Tls.sni(data, data.size)
                    if (name != null && BlockRules.isBlockedSni(name)) {
                        count("sni_rst")
                        log("A020 sni blocked name=$name ${c.key}")
                        sendRst(c.serverIp, c.clientIp, c.serverPort, c.clientPort, c.sndNxt, 0L, ackSet = false)
                        closeConn(c)
                        return
                    }
                }
                c.toUpstream.put(data)
                c.queuedBytes += plen
                c.rcvNxt = (c.rcvNxt + plen) and MASK
                count("tcp_in_bytes_accepted")
            } else {
                // Out of order, duplicate, or queue full: do not advance; the client retransmits.
                count("tcp_in_not_accepted")
            }
            sendAck(c)
        }
        if (h.fin && !c.clientFinSeen) {
            if (((h.seq + plen) and MASK) == c.rcvNxt) {
                c.clientFinSeen = true
                c.rcvNxt = (c.rcvNxt + 1) and MASK
                c.toUpstream.put(ByteArray(0))
            }
            sendAck(c)
        }
        if (c.clientFinSeen && c.finAcked) closeConn(c)
    }

    private fun writerLoop(c: TcpConn) {
        try {
            while (true) {
                val data = c.toUpstream.take()
                if (data.isEmpty()) {
                    c.socket?.shutdownOutput()
                    break
                }
                c.socket!!.getOutputStream().write(data)
                synchronized(c) { c.queuedBytes -= data.size }
            }
        } catch (_: IOException) {
            synchronized(c) {
                if (c.state != TcpState.CLOSED) {
                    count("tcp_upstream_write_err")
                    sendRst(c.serverIp, c.clientIp, c.serverPort, c.clientPort, c.sndNxt, c.rcvNxt, ackSet = false)
                    closeConn(c)
                }
            }
        }
    }

    private fun readerLoop(c: TcpConn) {
        val buf = ByteArray(c.connMss)
        try {
            val input = c.socket!!.getInputStream()
            while (true) {
                // Respect the client's window: wait while too much is unacknowledged (retransmit meanwhile).
                synchronized(c) {
                    while (c.state != TcpState.CLOSED && seqDiff(c.sndNxt, c.sndUna) >= maxOf(c.clientWindow, 1)) {
                        (c as java.lang.Object).wait(100)
                        retransmitIfDue(c)
                    }
                    if (c.state == TcpState.CLOSED) return
                }
                val n = input.read(buf)
                if (n < 0) break
                if (n == 0) continue
                synchronized(c) {
                    if (c.state == TcpState.CLOSED) return
                    val seg = Segment(c.sndNxt, buf.copyOf(n), System.currentTimeMillis())
                    c.unacked.addLast(seg)
                    sendData(c, seg.seq, seg.data)
                    c.sndNxt = (c.sndNxt + n) and MASK
                }
                count("tcp_out_bytes_sent")
            }
            // Upstream closed: FIN goes out once everything before it is acknowledged.
            synchronized(c) {
                while (c.state != TcpState.CLOSED && c.sndUna != c.sndNxt) {
                    (c as java.lang.Object).wait(100)
                    retransmitIfDue(c)
                }
                if (c.state != TcpState.CLOSED) {
                    sink.output(Tcp.buildIpv4(c.serverIp, c.clientIp, c.serverPort, c.clientPort,
                        seq = c.sndNxt, ack = c.rcvNxt, flags = Tcp.FIN or Tcp.ACK, window = 65535))
                    c.sndNxt = (c.sndNxt + 1) and MASK
                    c.finSent = true
                }
            }
            // Wait for the client's FIN and the ACK of ours (handled in handleClientSegment), with a 30 s bound.
            synchronized(c) {
                var waited = 0
                while (c.state != TcpState.CLOSED && !(c.clientFinSeen && c.finAcked) && waited < 30000) {
                    (c as java.lang.Object).wait(100); waited += 100
                    retransmitIfDue(c)
                }
            }
        } catch (_: IOException) {
            synchronized(c) {
                if (c.state != TcpState.CLOSED) {
                    count("tcp_upstream_read_err")
                    sendRst(c.serverIp, c.clientIp, c.serverPort, c.clientPort, c.sndNxt, c.rcvNxt, ackSet = false)
                }
            }
        } finally {
            synchronized(c) { closeConn(c) }
        }
    }

    /** Called with the connection's monitor held. Resends the oldest unacknowledged segment once its RTO has passed. */
    private fun retransmitIfDue(c: TcpConn) {
        val first = c.unacked.firstOrNull() ?: return
        val now = System.currentTimeMillis()
        if (now - first.sentAt < c.rto) return
        first.sentAt = now
        c.rto = minOf(c.rto * 2, 4000L)
        count("tcp_retransmit")
        sendData(c, first.seq, first.data)
    }

    private fun sendData(c: TcpConn, seq: Long, data: ByteArray) {
        sink.output(Tcp.buildIpv4(c.serverIp, c.clientIp, c.serverPort, c.clientPort,
            seq = seq, ack = c.rcvNxt, flags = Tcp.PSH or Tcp.ACK, window = 65535, payload = data))
    }

    private fun sendAck(c: TcpConn) {
        sink.output(Tcp.buildIpv4(c.serverIp, c.clientIp, c.serverPort, c.clientPort,
            seq = c.sndNxt, ack = c.rcvNxt, flags = Tcp.ACK, window = 65535))
    }

    /** Called with the connection's monitor held. */
    private fun closeConn(c: TcpConn) {
        if (c.state == TcpState.CLOSED) return
        c.state = TcpState.CLOSED
        tcpConns.remove(c.key, c)
        try { c.socket?.close() } catch (_: IOException) {}
        c.toUpstream.put(ByteArray(0)) // wakes the writer so it exits
        (c as java.lang.Object).notifyAll()
        count("tcp_closed")
    }

    /** Sends a reset. With [ackSet] the reset carries [seq] only; otherwise it carries RST|ACK with [ack]. */
    private fun sendRst(srcIp: Int, dstIp: Int, srcPort: Int, dstPort: Int, seq: Long, ack: Long, ackSet: Boolean) {
        val flags = if (ackSet) Tcp.RST else (Tcp.RST or Tcp.ACK)
        sink.output(Tcp.buildIpv4(srcIp, dstIp, srcPort, dstPort, seq = seq, ack = ack, flags = flags, window = 0))
        count("tcp_rst_sent")
    }

    fun shutdown() {
        closed = true
        udpFlows.values.forEach { it.close() }
        udpFlows.clear()
        tcpConns.values.forEach { synchronized(it) { closeConn(it) } }
    }

    private enum class TcpState { CONNECTING, SYN_ACKED_WAIT_ACK, ESTABLISHED, CLOSED }

    companion object {
        private const val MASK = 0xFFFFFFFFL
        private const val HALF = 0x80000000L

        /** a - b modulo 2^32. Small values mean "a is at or just after b". */
        fun seqDiff(a: Long, b: Long): Long = (a - b) and MASK

        fun addr(ip: Int): InetAddress = InetAddress.getByAddress(byteArrayOf(
            (ip ushr 24).toByte(), (ip ushr 16).toByte(), (ip ushr 8).toByte(), ip.toByte()))
    }
}
