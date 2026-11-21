package com.webfilter.spike.a021.net

// IPv6 is not forwarded in this spike. Every IPv6 packet from the tun is answered with ICMPv6 destination
// unreachable so the app fails fast (see Forwarder). Parsing covers only the fixed 40-byte header.

class Ip6Packet(val raw: ByteArray, val len: Int, val nextHeader: Int, val src: ByteArray, val dst: ByteArray)

object Ip6 {
    const val HDR = 40

    fun parse(b: ByteArray, len: Int): Ip6Packet? {
        if (len < HDR) return null
        if (Be.u8(b, 0) ushr 4 != 6) return null
        val payloadLen = Be.u16(b, 4)
        if (HDR + payloadLen > len) return null
        return Ip6Packet(b, len, Be.u8(b, 6), b.copyOfRange(8, 24), b.copyOfRange(24, 40))
    }

    /** ICMPv6 destination unreachable (type 1) with [code], sourced from the offending packet's destination. */
    fun unreachable(offending: Ip6Packet, code: Int): ByteArray {
        val quote = minOf(offending.len, 1232) // stay within the IPv6 minimum MTU
        val icmpLen = 8 + quote
        val out = ByteArray(HDR + icmpLen)
        out[0] = 0x60
        Be.put16(out, 4, icmpLen)
        out[6] = PROTO_ICMPV6.toByte()
        out[7] = 64
        System.arraycopy(offending.dst, 0, out, 8, 16)
        System.arraycopy(offending.src, 0, out, 24, 16)
        val icmp = HDR
        out[icmp] = 1
        out[icmp + 1] = code.toByte()
        System.arraycopy(offending.raw, 0, out, icmp + 8, quote)
        var sum = 0L
        for (i in 0 until 16 step 2) sum += Be.u16(out, 8 + i) + Be.u16(out, 24 + i) // pseudo header addresses
        sum += icmpLen + PROTO_ICMPV6
        sum += Checksum.sum(out, icmp, icmpLen)
        Be.put16(out, icmp + 2, Checksum.fold(sum))
        return out
    }
}
