package com.webfilter.spike.a020.dns

/**
 * Switches for the DoH layers (spike A0.20). Set from the DPC through the ACTION_RULES broadcast, read by the
 * forwarder in the :vpn process. Defaults are off, so the baseline behaves as in A0.2b.
 *  - dohNames: DNS answers NXDOMAIN for the DoH hostnames (layer "dns").
 *  - dohIp:    TCP SYN to a listed DoH IP is refused (layer "ip"; the IPs are resolved at test time and passed in).
 *  - sni:      TLS ClientHello whose SNI is a blocked name or a DoH hostname is reset (layer "sni").
 */
object Policy {
    @Volatile var dohNames = false
    @Volatile var dohIp = false
    @Volatile var sni = false
    @Volatile var dohIps: Set<String> = emptySet()

    val dohHosts: List<String> = listOf("dns.google", "cloudflare-dns.com", "dns.quad9.net")

    fun summary(): String = "dohNames=$dohNames dohIp=$dohIp sni=$sni dohIps=${dohIps.sorted()}"
}
