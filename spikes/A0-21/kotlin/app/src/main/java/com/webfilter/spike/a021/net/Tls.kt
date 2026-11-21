package com.webfilter.spike.a021.net

/** Reads the SNI host name from the first TLS record when it is a ClientHello. Returns null for anything else. */
object Tls {
    fun sni(b: ByteArray, len: Int): String? {
        if (len < 6 || (b[0].toInt() and 0xFF) != 0x16) return null
        if ((b[5].toInt() and 0xFF) != 0x01) return null // handshake type: ClientHello
        val end = minOf(len, 5 + Be.u16(b, 3))
        var off = 9 // record header (5) + handshake type (1) + handshake length (3)
        off += 2 + 32 // client version + random
        if (off + 1 > end) return null
        off += 1 + (b[off].toInt() and 0xFF) // session id
        if (off + 2 > end) return null
        off += 2 + Be.u16(b, off) // cipher suites
        if (off + 1 > end) return null
        off += 1 + (b[off].toInt() and 0xFF) // compression methods
        if (off + 2 > end) return null
        val extEnd = minOf(end, off + 2 + Be.u16(b, off))
        off += 2
        while (off + 4 <= extEnd) {
            val type = Be.u16(b, off)
            val l = Be.u16(b, off + 2)
            off += 4
            if (off + l > extEnd) return null
            if (type == 0x0000) { // server_name: list length (2), name type (1), name length (2), name
                if (l < 5) return null
                val nameType = b[off + 2].toInt() and 0xFF
                val nameLen = Be.u16(b, off + 3)
                if (nameType != 0 || 5 + nameLen > l) return null
                return String(b, off + 5, nameLen, Charsets.US_ASCII).lowercase()
            }
            off += l
        }
        return null
    }
}
