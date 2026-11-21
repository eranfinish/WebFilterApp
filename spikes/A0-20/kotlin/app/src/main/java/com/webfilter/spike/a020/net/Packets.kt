package com.webfilter.spike.a020.net

// Pure Kotlin (no android.* imports) so it can be unit-tested on the JVM.
// IPv4 addresses are Ints in network order, e.g. 10.0.0.1 = 0x0A000001.

const val PROTO_ICMP = 1
const val PROTO_TCP = 6
const val PROTO_UDP = 17
const val PROTO_ICMPV6 = 58

object Be {
    fun u8(b: ByteArray, o: Int): Int = b[o].toInt() and 0xFF
    fun u16(b: ByteArray, o: Int): Int = ((b[o].toInt() and 0xFF) shl 8) or (b[o + 1].toInt() and 0xFF)
    fun u32(b: ByteArray, o: Int): Long =
        ((b[o].toLong() and 0xFF) shl 24) or ((b[o + 1].toLong() and 0xFF) shl 16) or
            ((b[o + 2].toLong() and 0xFF) shl 8) or (b[o + 3].toLong() and 0xFF)
    fun put16(b: ByteArray, o: Int, v: Int) {
        b[o] = (v ushr 8).toByte(); b[o + 1] = v.toByte()
    }
    fun put32(b: ByteArray, o: Int, v: Long) {
        b[o] = (v ushr 24).toByte(); b[o + 1] = (v ushr 16).toByte()
        b[o + 2] = (v ushr 8).toByte(); b[o + 3] = v.toByte()
    }
    fun ip4(b: ByteArray, o: Int): Int =
        (u8(b, o) shl 24) or (u8(b, o + 1) shl 16) or (u8(b, o + 2) shl 8) or u8(b, o + 3)
    fun putIp4(b: ByteArray, o: Int, ip: Int) {
        b[o] = (ip ushr 24).toByte(); b[o + 1] = (ip ushr 16).toByte()
        b[o + 2] = (ip ushr 8).toByte(); b[o + 3] = ip.toByte()
    }
}

object Checksum {
    /** Sum of 16-bit big-endian words (odd trailing byte is padded with zero), not folded. */
    fun sum(b: ByteArray, off: Int, len: Int, initial: Long = 0L): Long {
        var s = initial
        var i = off
        val end = off + len
        while (i + 1 < end) {
            s += ((b[i].toInt() and 0xFF) shl 8 or (b[i + 1].toInt() and 0xFF)).toLong()
            i += 2
        }
        if (i < end) s += ((b[i].toInt() and 0xFF) shl 8).toLong()
        return s
    }

    fun fold(sumIn: Long): Int {
        var s = sumIn
        while (s ushr 16 != 0L) s = (s and 0xFFFF) + (s ushr 16)
        return (s.toInt().inv()) and 0xFFFF
    }

    /** Pseudo-header sum for TCP/UDP over IPv4. */
    fun pseudoIpv4(src: Int, dst: Int, proto: Int, length: Int): Long =
        (src ushr 16).toLong() + (src and 0xFFFF) + (dst ushr 16) + (dst and 0xFFFF) + proto + length
}

class Ipv4Packet(
    val raw: ByteArray,
    val len: Int,
    val headerLen: Int,
    val totalLen: Int,
    val protocol: Int,
    val src: Int,
    val dst: Int,
    val ttl: Int,
) {
    val payloadOff: Int get() = headerLen
    val payloadLen: Int get() = totalLen - headerLen
}

object Ipv4 {
    /** Returns null if the buffer is not a well-formed IPv4 packet we can use. */
    fun parse(b: ByteArray, len: Int): Ipv4Packet? {
        if (len < 20) return null
        val vihl = Be.u8(b, 0)
        if (vihl ushr 4 != 4) return null
        val ihl = (vihl and 0x0F) * 4
        if (ihl < 20 || ihl > len) return null
        val total = Be.u16(b, 2)
        if (total < ihl || total > len) return null
        val frag = Be.u16(b, 6)
        // Fragments (MF set or non-zero offset) are not reassembled in this spike.
        if (frag and 0x3FFF != 0) return null
        return Ipv4Packet(b, len, ihl, total, Be.u8(b, 9), Be.ip4(b, 12), Be.ip4(b, 16), Be.u8(b, 8))
    }

    /** Builds an IPv4 packet with a valid header checksum. */
    fun build(src: Int, dst: Int, protocol: Int, payload: ByteArray, payloadLen: Int = payload.size, ident: Int = 0): ByteArray {
        val total = 20 + payloadLen
        val out = ByteArray(total)
        out[0] = 0x45.toByte()
        Be.put16(out, 2, total)
        Be.put16(out, 4, ident and 0xFFFF)
        Be.put16(out, 6, 0x4000) // DF
        out[8] = 64
        out[9] = protocol.toByte()
        Be.putIp4(out, 12, src)
        Be.putIp4(out, 16, dst)
        Be.put16(out, 10, Checksum.fold(Checksum.sum(out, 0, 20)).toInt())
        System.arraycopy(payload, 0, out, 20, payloadLen)
        return out
    }
}

class TcpHeader(
    val srcPort: Int,
    val dstPort: Int,
    val seq: Long,
    val ack: Long,
    val dataOff: Int,
    val flags: Int,
    val window: Int,
    val payloadOff: Int, // absolute offset in the IP buffer
    val payloadLen: Int,
    val mss: Int?, // MSS option if present
) {
    val syn get() = flags and Tcp.SYN != 0
    val ackSet get() = flags and Tcp.ACK != 0
    val fin get() = flags and Tcp.FIN != 0
    val rst get() = flags and Tcp.RST != 0
}

object Tcp {
    const val FIN = 0x01
    const val SYN = 0x02
    const val RST = 0x04
    const val PSH = 0x08
    const val ACK = 0x10

    /** Parse a TCP segment inside an IPv4 packet. */
    fun parse(p: Ipv4Packet): TcpHeader? {
        val b = p.raw
        val o = p.payloadOff
        val avail = p.payloadLen
        if (avail < 20) return null
        val dataOff = (Be.u8(b, o + 12) ushr 4) * 4
        if (dataOff < 20 || dataOff > avail) return null
        var mss: Int? = null
        var i = o + 20
        val optEnd = o + dataOff
        while (i < optEnd) {
            val kind = Be.u8(b, i)
            if (kind == 0) break
            if (kind == 1) { i++; continue }
            if (i + 1 >= optEnd) break
            val l = Be.u8(b, i + 1)
            if (l < 2 || i + l > optEnd) break
            if (kind == 2 && l == 4) mss = Be.u16(b, i + 2)
            i += l
        }
        return TcpHeader(
            srcPort = Be.u16(b, o),
            dstPort = Be.u16(b, o + 2),
            seq = Be.u32(b, o + 4),
            ack = Be.u32(b, o + 8),
            dataOff = dataOff,
            flags = Be.u8(b, o + 13) and 0x3F,
            window = Be.u16(b, o + 14),
            payloadOff = o + dataOff,
            payloadLen = avail - dataOff,
            mss = mss,
        )
    }

    /**
     * Build an IPv4+TCP packet. [mss] adds the MSS option (SYN-ACK only). Window scaling is never offered.
     */
    fun buildIpv4(
        src: Int, dst: Int, srcPort: Int, dstPort: Int,
        seq: Long, ack: Long, flags: Int, window: Int,
        payload: ByteArray? = null, payloadLen: Int = payload?.size ?: 0, mss: Int? = null,
    ): ByteArray {
        val optLen = if (mss != null) 4 else 0
        val tcpLen = 20 + optLen + payloadLen
        val seg = ByteArray(tcpLen)
        Be.put16(seg, 0, srcPort)
        Be.put16(seg, 2, dstPort)
        Be.put32(seg, 4, seq and 0xFFFFFFFFL)
        Be.put32(seg, 8, ack and 0xFFFFFFFFL)
        seg[12] = (((20 + optLen) / 4) shl 4).toByte()
        seg[13] = flags.toByte()
        Be.put16(seg, 14, window.coerceIn(0, 65535))
        if (mss != null) {
            seg[20] = 2; seg[21] = 4; Be.put16(seg, 22, mss)
        }
        if (payloadLen > 0 && payload != null) System.arraycopy(payload, 0, seg, 20 + optLen, payloadLen)
        val sum = Checksum.pseudoIpv4(src, dst, PROTO_TCP, tcpLen) + Checksum.sum(seg, 0, tcpLen)
        Be.put16(seg, 16, Checksum.fold(sum))
        return Ipv4.build(src, dst, PROTO_TCP, seg, tcpLen)
    }
}

object Udp {
    /** Build an IPv4+UDP packet with a valid UDP checksum. */
    fun buildIpv4(src: Int, dst: Int, srcPort: Int, dstPort: Int, payload: ByteArray): ByteArray {
        val len = 8 + payload.size
        val seg = ByteArray(len)
        Be.put16(seg, 0, srcPort)
        Be.put16(seg, 2, dstPort)
        Be.put16(seg, 4, len)
        System.arraycopy(payload, 0, seg, 8, payload.size)
        val sum = Checksum.pseudoIpv4(src, dst, PROTO_UDP, len) + Checksum.sum(seg, 0, len)
        var c = Checksum.fold(sum)
        if (c == 0) c = 0xFFFF // RFC 768: zero means "no checksum"; send 0xFFFF instead
        Be.put16(seg, 6, c)
        return Ipv4.build(src, dst, PROTO_UDP, seg, len)
    }
}

object Icmp {
    /** ICMPv4 destination unreachable (type 3) quoting the offending IPv4 header + 8 bytes, sent back to its source. */
    fun unreachableIpv4(offending: Ipv4Packet, myIp: Int, code: Int): ByteArray {
        val quote = minOf(offending.headerLen + 8, offending.len)
        val body = ByteArray(8 + quote)
        body[0] = 3
        body[1] = code.toByte()
        System.arraycopy(offending.raw, 0, body, 8, quote)
        Be.put16(body, 2, Checksum.fold(Checksum.sum(body, 0, body.size)))
        return Ipv4.build(myIp, offending.src, PROTO_ICMP, body, body.size)
    }
}

class UdpHeader(val srcPort: Int, val dstPort: Int, val payloadOff: Int, val payloadLen: Int)

object UdpParse {
    fun parse(p: Ipv4Packet): UdpHeader? {
        val b = p.raw
        val o = p.payloadOff
        if (p.payloadLen < 8) return null
        val len = Be.u16(b, o + 4)
        if (len < 8 || len > p.payloadLen) return null
        return UdpHeader(Be.u16(b, o), Be.u16(b, o + 2), o + 8, len - 8)
    }
}
