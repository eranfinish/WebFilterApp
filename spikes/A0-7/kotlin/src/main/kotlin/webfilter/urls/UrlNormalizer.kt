package webfilter.urls

import java.util.Locale

/**
 * Port of WebFilter.Core.Urls.UrlNormalizer (src/WebFilter.Core/Urls/UrlNormalizer.cs). Keep the structure
 * close to the C# so that a diff between the two stays readable. The corpus in spikes/A0-7/corpus is the
 * oracle: a difference is a bug in this port until shown otherwise.
 */
class UrlNormalizer(private val idn: IdnMapper) {

    /** True for http and https URLs. Only the scheme is checked. */
    fun isWebUrl(input: String?): Boolean {
        if (input == null) return false
        val (scheme, _) = readScheme(clean(input)) ?: return false
        return isWebScheme(scheme)
    }

    /** Normalizes an http/https URL. Returns null for other schemes or unparseable input. */
    fun tryNormalize(input: String?): NormalizedUrl? {
        if (input == null) return null

        val s = clean(input)
        val (scheme, afterColon) = readScheme(s) ?: return null
        if (!isWebScheme(scheme)) return null

        // Special schemes ignore any run of slashes and backslashes before the authority.
        var i = afterColon
        while (i < s.length && (s[i] == '/' || s[i] == '\\')) i++

        val delimiter = indexOfAnyFrom(s, i, "/\\?#")
        val authorityEnd = if (delimiter < 0) s.length else delimiter
        var authority = s.substring(i, authorityEnd)

        // Everything up to the last '@' is userinfo; the host is what follows it.
        val at = authority.lastIndexOf('@')
        if (at >= 0) authority = authority.substring(at + 1)

        val colon = findPortSeparator(authority)
        val hostPart = if (colon < 0) authority else authority.substring(0, colon)
        var port: Int? = null
        if (colon >= 0 && colon + 1 < authority.length) {
            val value = parsePort(authority.substring(colon + 1)) ?: return null
            if (value != defaultPort(scheme)) port = value
        }

        val host = parseHost(hostPart) ?: return null

        var rest = s.substring(authorityEnd)
        val hash = rest.indexOf('#')
        if (hash >= 0) rest = rest.substring(0, hash)
        val question = rest.indexOf('?')
        val path = normalizePath(if (question < 0) rest else rest.substring(0, question))
        val query = if (question < 0) "" else percentEncode(rest.substring(question + 1), inQuery = true)

        return NormalizedUrl(scheme, host.host, host.kind, port, path, query)
    }

    /**
     * Normalizes a host as written in a rule pattern ("Bücher.Example", "192.168.1.50", "[::1]") to the same
     * form [tryNormalize] produces. Returns null if it is not a valid host.
     */
    fun tryNormalizeHost(host: String?): NormalizedHost? {
        if (host == null) return null

        val span = csTrim(host)
        // Also accept an IPv6 address typed without brackets ("::1").
        if (span.isNotEmpty() && span[0] != '[' && span.contains(':')) {
            val pieces = parseIPv6(span) ?: return null
            return NormalizedHost(serializeIPv6(pieces), HostKind.IPv6)
        }
        return parseHost(span)
    }

    // ---- path ----

    /**
     * Path normalization: backslashes act as '/', dot segments (also percent-encoded) are resolved, characters
     * a browser would percent-encode are encoded, existing escapes are kept. Empty becomes "/".
     */
    internal fun normalizePath(raw: String): String {
        if (raw.isEmpty()) return "/"

        val segments = ArrayList<String>()
        val first = if (raw[0] == '/' || raw[0] == '\\') 1 else 0
        var start = first
        for (k in first..raw.length) {
            if (k < raw.length && raw[k] != '/' && raw[k] != '\\') continue

            val segment = raw.substring(start, k)
            val isLast = k == raw.length
            when {
                isDoubleDotSegment(segment) -> {
                    if (segments.isNotEmpty()) segments.removeAt(segments.size - 1)
                    if (isLast) segments.add("")
                }
                isSingleDotSegment(segment) -> {
                    if (isLast) segments.add("")
                }
                else -> segments.add(percentEncode(segment, inQuery = false))
            }
            start = k + 1
        }
        return "/" + segments.joinToString("/")
    }

    // ---- scheme and authority ----

    private fun isWebScheme(scheme: String) = scheme == "http" || scheme == "https"

    private fun defaultPort(scheme: String) = if (scheme == "https") 443 else 80

    /** Strips leading and trailing C0 controls and spaces, and removes tabs and newlines (URL Standard). */
    private fun clean(input: String): String {
        var start = 0
        var end = input.length
        while (start < end && input[start] <= ' ') start++
        while (end > start && input[end - 1] <= ' ') end--
        val span = input.substring(start, end)
        return if (span.none { it == '\t' || it == '\n' || it == '\r' }) span
        else span.filter { it != '\t' && it != '\n' && it != '\r' }
    }

    /** Returns the lowercase scheme and the index just after the colon, or null if there is no valid scheme. */
    private fun readScheme(s: String): Pair<String, Int>? {
        if (s.isEmpty() || !isAsciiLetter(s[0])) return null
        for (i in 1 until s.length) {
            val c = s[i]
            if (c == ':') return s.substring(0, i).lowercase(Locale.ROOT) to i + 1
            if (!isAsciiLetter(c) && !isAsciiDigit(c) && c != '+' && c != '-' && c != '.') return null
        }
        return null
    }

    /** The first ':' outside brackets separates host and port. */
    private fun findPortSeparator(authority: String): Int {
        var inBrackets = false
        for (k in authority.indices) {
            when (authority[k]) {
                '[' -> inBrackets = true
                ']' -> inBrackets = false
                ':' -> if (!inBrackets) return k
            }
        }
        return -1
    }

    private fun parsePort(text: String): Int? {
        var port = 0
        for (c in text) {
            if (!isAsciiDigit(c)) return null
            port = port * 10 + (c - '0')
            if (port > 65535) return null
        }
        return port
    }

    private fun parseHost(input: String): NormalizedHost? {
        if (input.isEmpty()) return null

        if (input[0] == '[') {
            if (input.length < 2 || input[input.length - 1] != ']') return null
            val pieces = parseIPv6(input.substring(1, input.length - 1)) ?: return null
            return NormalizedHost(serializeIPv6(pieces), HostKind.IPv6)
        }

        var decoded = percentDecode(input)
        var strippedDot = false
        if (decoded.endsWith('.')) {
            decoded = decoded.substring(0, decoded.length - 1)
            strippedDot = true
        }

        val ascii: String
        if (decoded.all { it.code < 0x80 }) {
            ascii = decoded.lowercase(Locale.ROOT)
        } else {
            // UTS-46 (see IdnMapper). Invalid input returns null here, where .NET throws.
            val mapped = idn.toAscii(decoded) ?: return null
            var lowered = mapped.lowercase(Locale.ROOT)
            // UTS-46 maps ideographic and fullwidth full stops to '.'.
            if (!strippedDot && lowered.endsWith('.')) lowered = lowered.substring(0, lowered.length - 1)
            ascii = lowered
        }

        if (ascii.isEmpty()) return null
        for (c in ascii) {
            if (isForbiddenDomainCodePoint(c)) return null
        }

        if (endsInNumber(ascii)) {
            val address = parseIPv4(ascii) ?: return null
            val a = address
            return NormalizedHost("${a shr 24}.${(a shr 16) and 0xFF}.${(a shr 8) and 0xFF}.${a and 0xFF}", HostKind.IPv4)
        }

        return NormalizedHost(ascii, HostKind.Domain)
    }

    private fun isForbiddenDomainCodePoint(c: Char) =
        c.code <= 0x20 || c.code == 0x7F || c in "#%/:<>?@[\\]^|"

    // ---- IPv4 (URL Standard: "ends in a number" and the IPv4 parser) ----

    private fun endsInNumber(host: String): Boolean {
        var span = host
        if (span.last() == '.') {
            if (span.length == 1) return false
            span = span.substring(0, span.length - 1)
        }
        val last = span.substring(span.lastIndexOf('.') + 1)
        if (last.isEmpty()) return false
        if (last.all { it in '0'..'9' }) return true
        return last.length >= 2 && last[0] == '0' && (last[1] == 'x' || last[1] == 'X') &&
            last.substring(2).all { isAsciiHex(it) }
    }

    /** Returns the address as an unsigned 32-bit value held in a Long. */
    private fun parseIPv4(host: String): Long? {
        val parts = host.split('.')
        var count = parts.size
        if (count > 1 && parts[count - 1].isEmpty()) count--
        if (count == 0 || count > 4) return null

        val numbers = LongArray(4)
        for (i in 0 until count) numbers[i] = parseIPv4Number(parts[i]) ?: return null
        for (i in 0 until count - 1) {
            if (numbers[i] > 255) return null
        }
        if (numbers[count - 1] >= 1L shl (8 * (5 - count))) return null

        var result = numbers[count - 1]
        for (i in 0 until count - 1) result += numbers[i] shl (8 * (3 - i))
        return result and 0xFFFFFFFFL
    }

    private fun parseIPv4Number(text: String): Long? {
        if (text.isEmpty()) return null

        var radix = 10
        var digits = text
        if (digits.length >= 2 && digits[0] == '0' && (digits[1] == 'x' || digits[1] == 'X')) {
            radix = 16
            digits = digits.substring(2)
        } else if (digits.length >= 2 && digits[0] == '0') {
            radix = 8
            digits = digits.substring(1)
        }

        var value = 0L
        for (c in digits) {
            val digit = hexValue(c)
            if (digit < 0 || digit >= radix) return null
            // Saturate: anything this large fails the range checks anyway.
            value = if (value > (1L shl 40)) value else value * radix + digit
        }
        return value
    }

    // ---- IPv6 (URL Standard IPv6 parser and serializer) ----

    private fun parseIPv6(input: String): IntArray? {
        val address = IntArray(8)
        var pieceIndex = 0
        var compress = -1 // -1 means "no compress" (the C# int?)
        var p = 0

        if (charAt(input, p) == ':') {
            if (charAt(input, p + 1) != ':') return null
            p += 2
            pieceIndex++
            compress = pieceIndex
        }

        while (p < input.length) {
            if (pieceIndex == 8) return null
            if (input[p] == ':') {
                if (compress != -1) return null
                p++
                pieceIndex++
                compress = pieceIndex
                continue
            }

            var value = 0
            var length = 0
            while (length < 4 && p < input.length && isAsciiHex(input[p])) {
                value = value * 16 + hexValue(input[p])
                p++
                length++
            }

            if (charAt(input, p) == '.') {
                if (length == 0) return null
                p -= length
                if (pieceIndex > 6) return null

                var numbersSeen = 0
                while (p < input.length) {
                    var piece = -1 // -1 means "not started yet" (the C# int?)
                    if (numbersSeen > 0) {
                        if (input[p] == '.' && numbersSeen < 4) p++ else return null
                    }
                    if (!isAsciiDigit(charAt(input, p))) return null
                    while (isAsciiDigit(charAt(input, p))) {
                        val number = input[p] - '0'
                        piece = when {
                            piece == -1 -> number
                            piece == 0 -> return null
                            else -> piece * 10 + number
                        }
                        if (piece > 255) return null
                        p++
                    }
                    address[pieceIndex] = (address[pieceIndex] * 0x100 + piece) and 0xFFFF
                    numbersSeen++
                    if (numbersSeen == 2 || numbersSeen == 4) pieceIndex++
                }
                if (numbersSeen != 4) return null
                break
            }

            if (charAt(input, p) == ':') {
                p++
                if (p >= input.length) return null
            } else if (p < input.length) {
                return null
            }
            address[pieceIndex] = value
            pieceIndex++
        }

        if (compress != -1) {
            var swaps = pieceIndex - compress
            pieceIndex = 7
            while (pieceIndex != 0 && swaps > 0) {
                val other = compress + swaps - 1
                val t = address[pieceIndex]
                address[pieceIndex] = address[other]
                address[other] = t
                pieceIndex--
                swaps--
            }
        } else if (pieceIndex != 8) {
            return null
        }
        return address
    }

    /** Compresses the first longest run of two or more zero pieces, lowercase hex. */
    private fun serializeIPv6(address: IntArray): String {
        var compress = -1
        var bestLength = 1
        var i = 0
        while (i < 8) {
            if (address[i] != 0) {
                i++
                continue
            }
            var j = i
            while (j < 8 && address[j] == 0) j++
            if (j - i > bestLength) {
                compress = i
                bestLength = j - i
            }
            i = j
        }

        val sb = StringBuilder(39)
        var ignoreZero = false
        for (k in 0 until 8) {
            if (ignoreZero && address[k] == 0) continue
            ignoreZero = false
            if (k == compress) {
                sb.append(if (k == 0) "::" else ":")
                ignoreZero = true
                continue
            }
            sb.append(address[k].toString(16))
            if (k != 7) sb.append(':')
        }
        return sb.toString()
    }

    // ---- percent-encoding ----

    private fun percentDecode(input: String): String {
        if (!input.contains('%')) return input

        val bytes = java.io.ByteArrayOutputStream(input.length)
        var i = 0
        while (i < input.length) {
            val c = input[i]
            if (c == '%' && i + 2 < input.length && isAsciiHex(input[i + 1]) && isAsciiHex(input[i + 2])) {
                bytes.write(hexValue(input[i + 1]) * 16 + hexValue(input[i + 2]))
                i += 3
                continue
            }
            val length = surrogateLength(input, i)
            bytes.write(utf8Chunk(input, i, length))
            i += length
        }
        return String(bytes.toByteArray(), Charsets.UTF_8)
    }

    /** Percent-encodes what the URL Standard encodes in paths (or in special-scheme queries). Keeps escapes. */
    private fun percentEncode(input: String, inQuery: Boolean): String {
        if (input.none { mustEncode(it, inQuery) }) return input

        val sb = StringBuilder(input.length + 16)
        var i = 0
        while (i < input.length) {
            val c = input[i]
            if (!mustEncode(c, inQuery)) {
                sb.append(c)
                i++
                continue
            }
            val length = surrogateLength(input, i)
            for (b in utf8Chunk(input, i, length)) {
                sb.append('%').append(((b.toInt() and 0xFF).toString(16)).uppercase(Locale.ROOT).padStart(2, '0'))
            }
            i += length
        }
        return sb.toString()
    }

    private fun mustEncode(c: Char, inQuery: Boolean): Boolean =
        c.code < 0x20 || c.code > 0x7E || c == ' ' || c == '"' || c == '<' || c == '>' ||
            (if (inQuery) c == '\'' else c == '`' || c == '{' || c == '}')

    /** A surrogate pair is one code point, so it is encoded as one chunk, like .NET's UTF-8 encoder does. */
    private fun surrogateLength(s: String, i: Int): Int =
        if (s[i].isHighSurrogate() && i + 1 < s.length && s[i + 1].isLowSurrogate()) 2 else 1

    /**
     * UTF-8 bytes of one chunk. A lone surrogate becomes U+FFFD, as in .NET (Java would write '?').
     */
    private fun utf8Chunk(s: String, start: Int, length: Int): ByteArray =
        if (length == 1 && s[start].isSurrogate()) "�".toByteArray(Charsets.UTF_8)
        else s.substring(start, start + length).toByteArray(Charsets.UTF_8)

    private fun isSingleDotSegment(s: String) = s == "." || s.equals("%2e", ignoreCase = true)

    private fun isDoubleDotSegment(s: String) =
        s == ".." || s.equals(".%2e", ignoreCase = true) || s.equals("%2e.", ignoreCase = true) ||
            s.equals("%2e%2e", ignoreCase = true)

    // ---- character helpers (ASCII rules of the URL Standard, and .NET's char.IsWhiteSpace for Trim) ----

    private fun charAt(s: String, index: Int): Char = if (index < s.length) s[index] else '\u0000'

    private fun indexOfAnyFrom(s: String, from: Int, chars: String): Int {
        for (k in from until s.length) {
            if (s[k] in chars) return k
        }
        return -1
    }

    private fun isAsciiLetter(c: Char) = c in 'a'..'z' || c in 'A'..'Z'

    private fun isAsciiDigit(c: Char) = c in '0'..'9'

    private fun isAsciiHex(c: Char) = c in '0'..'9' || c in 'a'..'f' || c in 'A'..'F'

    private fun hexValue(c: Char): Int = when (c) {
        in '0'..'9' -> c - '0'
        in 'a'..'f' -> c - 'a' + 10
        in 'A'..'F' -> c - 'A' + 10
        else -> -1
    }

    private fun isCsWhitespace(c: Char): Boolean {
        if (c == ' ' || c in '\t'..'\r' || c == '\u0085' || c == ' ') return true
        val type = Character.getType(c)
        return type == Character.SPACE_SEPARATOR.toInt() ||
            type == Character.LINE_SEPARATOR.toInt() ||
            type == Character.PARAGRAPH_SEPARATOR.toInt()
    }

    private fun csTrim(s: String): String {
        var start = 0
        var end = s.length
        while (start < end && isCsWhitespace(s[start])) start++
        while (end > start && isCsWhitespace(s[end - 1])) end--
        return s.substring(start, end)
    }
}
