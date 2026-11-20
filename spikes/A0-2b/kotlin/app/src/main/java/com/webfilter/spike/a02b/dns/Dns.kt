package com.webfilter.spike.a02b.dns

import com.webfilter.spike.a02b.net.Be

// Minimal DNS: parse one question, answer NXDOMAIN for blocked names. Everything else is forwarded upstream by the caller.

class DnsQuestion(val id: Int, val name: String, val qtype: Int, val qclass: Int, val questionEnd: Int)

object Dns {
    const val RCODE_NXDOMAIN = 3

    /** Parses the first question. Returns null for anything malformed or without exactly one question. */
    fun parseQuestion(b: ByteArray, len: Int): DnsQuestion? {
        if (len < 12) return null
        val flags = Be.u16(b, 2)
        if (flags and 0x8000 != 0) return null // a response, not a query
        val qd = Be.u16(b, 4)
        if (qd < 1) return null
        var off = 12
        val labels = ArrayList<String>()
        var total = 0
        while (true) {
            if (off >= len) return null
            val l = Be.u8(b, off)
            if (l and 0xC0 != 0) return null // compression is not valid in a question
            off++
            if (l == 0) break
            if (l > 63 || off + l > len) return null
            labels.add(String(b, off, l, Charsets.US_ASCII).lowercase())
            off += l
            total += l + 1
            if (total > 255) return null
        }
        if (off + 4 > len) return null
        val qtype = Be.u16(b, off)
        val qclass = Be.u16(b, off + 2)
        return DnsQuestion(Be.u16(b, 0), labels.joinToString("."), qtype, qclass, off + 4)
    }

    /** Copies the query's header ID and question, sets QR=1, RD copied, RA=1, and the given RCODE. No answers. */
    fun reply(query: ByteArray, queryLen: Int, q: DnsQuestion, rcode: Int): ByteArray {
        val qEnd = q.questionEnd.coerceAtMost(queryLen)
        val out = ByteArray(qEnd)
        System.arraycopy(query, 0, out, 0, qEnd)
        val rd = Be.u16(query, 2) and 0x0100
        Be.put16(out, 2, 0x8000 or rd or 0x0080 or (rcode and 0x0F))
        Be.put16(out, 4, 1) // QDCOUNT
        Be.put16(out, 6, 0)
        Be.put16(out, 8, 0)
        Be.put16(out, 10, 0)
        return out
    }

    /** True if [name] equals [domain] or is a subdomain of it (case-insensitive, no trailing dot). */
    fun matchesSuffix(name: String, domain: String): Boolean =
        name == domain || name.endsWith(".$domain")
}

/** Hard-coded block rules for the spike. Same three names as A0.2, so the results can be compared. */
object BlockRules {
    val blockedDomains: List<String> = listOf("blocked.example", "blocked.test", "wikipedia.org")

    fun isBlocked(name: String): Boolean {
        val n = name.trimEnd('.').lowercase()
        return blockedDomains.any { Dns.matchesSuffix(n, it) }
    }
}
