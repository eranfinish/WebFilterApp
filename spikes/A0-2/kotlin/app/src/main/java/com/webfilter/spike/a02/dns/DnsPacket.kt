package com.webfilter.spike.a02.dns

// Spike A0.2 (throwaway). Pure Kotlin with no Android imports, so the JVM unit tests can run it.
// Covers only what the spike needs: IPv4 + UDP framing, DNS question parsing, and NXDOMAIN answers.

internal fun u16(b: ByteArray, off: Int): Int =
    ((b[off].toInt() and 0xFF) shl 8) or (b[off + 1].toInt() and 0xFF)

internal fun putU16(b: ByteArray, off: Int, v: Int) {
    b[off] = ((v ushr 8) and 0xFF).toByte()
    b[off + 1] = (v and 0xFF).toByte()
}

object Ipv4Udp {
    const val PROTO_UDP = 17

    class Datagram(
        val srcIp: ByteArray,
        val dstIp: ByteArray,
        val srcPort: Int,
        val dstPort: Int,
        val payload: ByteArray,
    )

    /** The UDP datagram carried by an IPv4 packet, or null (not IPv4, not UDP, a fragment, or bad lengths). */
    fun parse(packet: ByteArray, length: Int = packet.size): Datagram? {
        if (length < 28) return null
        if (((packet[0].toInt() and 0xFF) ushr 4) != 4) return null
        val ihl = (packet[0].toInt() and 0x0F) * 4
        if (ihl < 20 || length < ihl + 8) return null
        if ((packet[9].toInt() and 0xFF) != PROTO_UDP) return null
        val totalLen = u16(packet, 2)
        if (totalLen < ihl + 8 || totalLen > length) return null
        val fragField = u16(packet, 6)
        if ((fragField and 0x2000) != 0 || (fragField and 0x1FFF) != 0) return null
        val udpLen = u16(packet, ihl + 4)
        if (udpLen < 8 || ihl + udpLen > totalLen) return null
        return Datagram(
            srcIp = packet.copyOfRange(12, 16),
            dstIp = packet.copyOfRange(16, 20),
            srcPort = u16(packet, ihl),
            dstPort = u16(packet, ihl + 2),
            payload = packet.copyOfRange(ihl + 8, ihl + udpLen),
        )
    }

    /** An IPv4/UDP packet with a valid IPv4 header checksum. The UDP checksum is 0 (not computed; legal for IPv4). */
    fun build(srcIp: ByteArray, dstIp: ByteArray, srcPort: Int, dstPort: Int, payload: ByteArray): ByteArray {
        val udpLen = 8 + payload.size
        val total = 20 + udpLen
        val out = ByteArray(total)
        out[0] = 0x45
        putU16(out, 2, total)
        putU16(out, 6, 0x4000) // DF, no fragments
        out[8] = 64 // TTL
        out[9] = PROTO_UDP.toByte()
        srcIp.copyInto(out, 12)
        dstIp.copyInto(out, 16)
        putU16(out, 10, checksum(out, 0, 20)) // header checksum field is still 0 here
        putU16(out, 20, srcPort)
        putU16(out, 22, dstPort)
        putU16(out, 24, udpLen)
        payload.copyInto(out, 28)
        return out
    }

    /** Internet checksum (RFC 1071) over [off, off+len). Over a header that already holds its checksum, the result is 0. */
    fun checksum(b: ByteArray, off: Int, len: Int): Int {
        var sum = 0L
        var i = off
        val end = off + len
        while (i + 1 < end) {
            sum += u16(b, i)
            i += 2
        }
        if (i < end) sum += (b[i].toInt() and 0xFF) shl 8
        while ((sum ushr 16) != 0L) sum = (sum and 0xFFFFL) + (sum ushr 16)
        return (sum.inv() and 0xFFFFL).toInt()
    }
}

object DnsMessage {
    const val TYPE_A = 1
    const val TYPE_AAAA = 28
    const val TYPE_SVCB = 64
    const val TYPE_HTTPS = 65
    const val RCODE_NXDOMAIN = 3

    class Question(val name: String, val qtype: Int, val qclass: Int, val endOffset: Int)

    /** The first question of a DNS query, or null if the message is not a well-formed query. Name is lower-case, without a trailing dot. */
    fun parseQuestion(msg: ByteArray): Question? {
        if (msg.size < 12) return null
        if ((u16(msg, 2) and 0x8000) != 0) return null // a response, not a query
        if (u16(msg, 4) < 1) return null // QDCOUNT
        val name = StringBuilder()
        var pos = 12
        var wire = 0
        while (true) {
            if (pos >= msg.size) return null
            val len = msg[pos].toInt() and 0xFF
            if (len == 0) {
                pos++
                break
            }
            if ((len and 0xC0) != 0 || len > 63) return null // compression pointers are not valid in a query
            if (pos + 1 + len > msg.size) return null
            if (name.isNotEmpty()) name.append('.')
            for (i in 1..len) {
                var c = msg[pos + i].toInt() and 0xFF
                if (c in 0x41..0x5A) c += 0x20 // ASCII lower-case only
                name.append(c.toChar())
            }
            wire += len + 1
            if (wire > 255) return null
            pos += len + 1
        }
        if (pos + 4 > msg.size) return null
        return Question(name.toString(), u16(msg, pos), u16(msg, pos + 2), pos + 4)
    }

    /** NXDOMAIN for the query: same ID and question, QR set, RD and opcode copied, no records. Used for every qtype of a blocked name. */
    fun nxdomainFor(query: ByteArray, q: Question): ByteArray {
        val out = query.copyOf(q.endOffset)
        val qflags = u16(query, 2)
        putU16(out, 2, 0x8000 or (qflags and 0x7900) or 0x0080 or RCODE_NXDOMAIN)
        putU16(out, 6, 0) // ANCOUNT
        putU16(out, 8, 0) // NSCOUNT
        putU16(out, 10, 0) // ARCOUNT (an EDNS OPT record in the query is dropped; not needed for the spike)
        return out
    }
}
