package webfilter.urls

enum class HostKind { Domain, IPv4, IPv6 }

/** A web URL after normalization. Same fields and Href rule as WebFilter.Core's NormalizedUrl. */
data class NormalizedUrl(
    val scheme: String,
    val host: String,
    val hostKind: HostKind,
    val port: Int?,
    val path: String,
    val query: String,
) {
    val href: String
        get() {
            val h = if (hostKind == HostKind.IPv6) "[$host]" else host
            val p = port?.let { ":$it" } ?: ""
            val q = if (query.isEmpty()) "" else "?$query"
            return "$scheme://$h$p$path$q"
        }
}

/** A host as written in a rule pattern, normalized the same way as a URL's host. */
data class NormalizedHost(val host: String, val kind: HostKind)

/**
 * IDNA to ASCII (UTS-46). Returns null where .NET would throw ArgumentException: invalid input.
 * The JVM test uses ICU4J; the device uses android.icu.text.IDNA.
 */
fun interface IdnMapper {
    fun toAscii(unicode: String): String?
}
