package com.webfilter.spike.a020.probe

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.system.ErrnoException
import android.system.Os
import android.system.OsConstants
import android.util.Log
import java.io.ByteArrayOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.URL
import java.security.cert.X509Certificate
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import kotlin.concurrent.thread

/**
 * Client-side probe (its own uid, no special permissions: INTERNET and ACCESS_NETWORK_STATE only).
 *   adb -s emulator-5590 shell am broadcast -n com.webfilter.spike.a020.probe/.ProbeReceiver --es op <op> ...
 * Every result is logged under tag A020P as "op=<op> uid=<uid> -> <result>".
 *
 * Ops: dns(host) raw(ip,port) http(url) nets | net_connect(sel,ip,port) net_open(sel,url) net_bindproc(sel,url)
 *      so_mark(value) udp_dns(ip,name,port) doh_sni(host,ip?,name,path) doh_url(host,name,path)
 * sel = wifi | cell | eth | any (non-VPN networks with INTERNET). Every net_* op also runs a control on the default route.
 */
class ProbeReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        // Keep-alive off: a pooled HTTP socket opened earlier would hide a new SYN (checked with the forwarder's SYN trace).
        System.setProperty("http.keepAlive", "false")
        // Fire and forget: a long op under goAsync() caused an ANR earlier, so the result goes to logcat.
        val op = intent.getStringExtra("op") ?: "dns"
        thread(name = "probe-$op") {
            val result = try {
                run(context, op, intent)
            } catch (e: Throwable) {
                "EXC ${e.javaClass.name}: ${e.message}"
            }
            Log.i(TAG, "op=$op uid=${android.os.Process.myUid()} -> $result")
        }
    }

    private fun run(ctx: Context, op: String, intent: Intent): String = when (op) {
        "dns" -> {
            val host = intent.getStringExtra("host") ?: "example.org"
            try {
                "OK ${InetAddress.getAllByName(host).joinToString { it.hostAddress ?: "?" }}"
            } catch (e: Exception) {
                "FAIL ${e.javaClass.simpleName}: ${e.message}"
            }
        }
        "raw" -> {
            val ip = intent.getStringExtra("ip") ?: "93.184.215.14"
            val port = intent.getIntExtra("port", 443)
            connectTimed { Socket().use { it.connect(InetSocketAddress(ip, port), 5000) } }
        }
        "http" -> {
            val url = intent.getStringExtra("url") ?: "https://example.org/"
            httpGet(URL(url).openConnection() as HttpURLConnection)
        }
        "nets" -> listNets(ctx)
        "net_dns" -> {
            // Network.getAllByName: the lookup is done for that network (netd), not through the app's default route.
            val sel = intent.getStringExtra("sel") ?: "wifi"
            val host = intent.getStringExtra("host") ?: "en.wikipedia.org"
            val cm = ctx.getSystemService(ConnectivityManager::class.java)
            pickNets(cm, sel).joinToString(" | ") { n ->
                try {
                    "net[${label(cm, n)}] getAllByName($host) OK " + n.getAllByName(host).joinToString { it.hostAddress ?: "?" }
                } catch (e: Exception) {
                    "net[${label(cm, n)}] getAllByName($host) FAIL ${e.javaClass.simpleName}: ${e.message}"
                }
            }
        }
        "net_connect" -> {
            val sel = intent.getStringExtra("sel") ?: "any"
            val ip = intent.getStringExtra("ip") ?: "93.184.215.14"
            val port = intent.getIntExtra("port", 80)
            val cm = ctx.getSystemService(ConnectivityManager::class.java)
            val lines = pickNets(cm, sel).map { n ->
                "net[${label(cm, n)}] " + connectTimed { n.socketFactory.createSocket().use { it.connect(InetSocketAddress(ip, port), 5000) } }
            }
            (lines + "control default-route " + connectTimed { Socket().use { it.connect(InetSocketAddress(ip, port), 5000) } })
                .joinToString(" | ")
        }
        "net_open" -> {
            val sel = intent.getStringExtra("sel") ?: "any"
            val url = intent.getStringExtra("url") ?: "http://example.org/"
            val cm = ctx.getSystemService(ConnectivityManager::class.java)
            val lines = pickNets(cm, sel).map { n ->
                "net[${label(cm, n)}] " + httpGet(n.openConnection(URL(url)) as HttpURLConnection)
            }
            (lines + "control default-route " + httpGet(URL(url).openConnection() as HttpURLConnection)).joinToString(" | ")
        }
        "net_bindproc" -> {
            val sel = intent.getStringExtra("sel") ?: "any"
            val url = intent.getStringExtra("url") ?: "http://example.org/"
            val cm = ctx.getSystemService(ConnectivityManager::class.java)
            val lines = pickNets(cm, sel).map { n ->
                cm.bindProcessToNetwork(n)
                try {
                    "net[${label(cm, n)}] bindProcess " + httpGet(URL(url).openConnection() as HttpURLConnection)
                } finally {
                    cm.bindProcessToNetwork(null)
                }
            }
            lines.joinToString(" | ")
        }
        "so_mark" -> {
            val value = intent.getIntExtra("value", 0x20000 or 1)
            val fd = Os.socket(OsConstants.AF_INET, OsConstants.SOCK_STREAM, 0)
            try {
                Os.setsockoptInt(fd, OsConstants.SOL_SOCKET, SO_MARK_LINUX, value)
                "SO_MARK=0x${Integer.toHexString(value)} SET (no error)"
            } catch (e: ErrnoException) {
                "SO_MARK=0x${Integer.toHexString(value)} FAIL errno=${e.errno} ${e.message}"
            } finally {
                Os.close(fd)
            }
        }
        "udp_dns" -> {
            val ip = intent.getStringExtra("ip") ?: "8.8.8.8"
            val name = intent.getStringExtra("name") ?: "example.org"
            val port = intent.getIntExtra("port", 53)
            val q = dnsQuery(name)
            try {
                DatagramSocket().use { s ->
                    s.soTimeout = 3000
                    s.connect(InetSocketAddress(ip, port))
                    s.send(DatagramPacket(q, q.size))
                    val buf = ByteArray(1500)
                    val p = DatagramPacket(buf, buf.size)
                    s.receive(p)
                    val rcode = buf[3].toInt() and 0x0F
                    val an = ((buf[6].toInt() and 0xFF) shl 8) or (buf[7].toInt() and 0xFF)
                    "REPLY from $ip rcode=$rcode answers=$an"
                }
            } catch (_: SocketTimeoutException) {
                "NO REPLY from $ip within 3 s"
            } catch (e: Exception) {
                "FAIL ${e.javaClass.simpleName}: ${e.message}"
            }
        }
        "doh_sni" -> {
            val host = intent.getStringExtra("host") ?: "dns.google"
            val ip = intent.getStringExtra("ip") ?: InetAddress.getByName(host).hostAddress
            val name = intent.getStringExtra("name") ?: "en.wikipedia.org"
            val path = intent.getStringExtra("path") ?: "/resolve"
            dohSni(host, ip, name, path)
        }
        "doh_url" -> {
            val host = intent.getStringExtra("host") ?: "dns.google"
            val name = intent.getStringExtra("name") ?: "en.wikipedia.org"
            val path = intent.getStringExtra("path") ?: "/resolve"
            val c = URL("https://$host$path?name=$name&type=A").openConnection() as HttpsURLConnection
            c.setRequestProperty("Accept", "application/dns-json")
            httpGet(c, withBody = true)
        }
        "http_host" -> {
            // Connect by IP with a Host header. sel=none: default route (through the tunnel).
            // sel=wifi|cell|any: bindProcessToNetwork to that network first, then a plain Socket (no protect, no mark).
            val sel = intent.getStringExtra("sel") ?: "none"
            val ip = intent.getStringExtra("ip") ?: return "missing ip"
            val host = intent.getStringExtra("host") ?: return "missing host"
            val path = intent.getStringExtra("path") ?: "/"
            val cm = ctx.getSystemService(ConnectivityManager::class.java)
            val nets = if (sel == "none") listOf(null) else pickNets(cm, sel)
            nets.joinToString(" | ") { n ->
                val tag = if (n == null) "default-route" else "net[${label(cm, n)}]"
                if (n != null) cm.bindProcessToNetwork(n)
                try {
                    val t0 = System.nanoTime()
                    Socket().use { s ->
                        s.connect(InetSocketAddress(ip, 80), 5000)
                        s.soTimeout = 6000
                        s.getOutputStream().write("GET $path HTTP/1.1\r\nHost: $host\r\nConnection: close\r\n\r\n".toByteArray())
                        val first = s.getInputStream().bufferedReader().readLine() ?: "(no status line)"
                        "$tag OK ip=$ip host=$host status=\"$first\" ms=${(System.nanoTime() - t0) / 1_000_000}"
                    }
                } catch (e: Exception) {
                    "$tag FAIL ip=$ip host=$host ${e.javaClass.simpleName}: ${e.message}"
                } finally {
                    if (n != null) cm.bindProcessToNetwork(null)
                }
            }
        }
        else -> "unknown op $op"
    }

    /** TLS to [ip] with SNI [host] (certificate not checked: this is a reachability and SNI probe only). */
    private fun dohSni(host: String, ip: String, name: String, path: String): String {
        val t0 = System.nanoTime()
        var stage = "connect"
        return try {
            val raw = Socket()
            raw.connect(InetSocketAddress(ip, 443), 5000)
            raw.soTimeout = 8000
            stage = "tls"
            val ssl = trustAll.socketFactory.createSocket(raw, host, 443, true) as SSLSocket
            ssl.startHandshake()
            stage = "http"
            val req = "GET $path?name=$name&type=A HTTP/1.1\r\nHost: $host\r\nAccept: application/dns-json\r\nConnection: close\r\n\r\n"
            ssl.outputStream.write(req.toByteArray())
            ssl.outputStream.flush()
            val text = String(ssl.inputStream.readBytes(), Charsets.UTF_8)
            ssl.close()
            val status = text.lineSequence().firstOrNull() ?: "(empty)"
            val body = text.substringAfter("\r\n\r\n", "").take(300).replace('\r', ' ').replace('\n', ' ')
            "OK ip=$ip sni=$host status=\"$status\" answerInBody=${text.contains("\"Answer\"")} ms=${(System.nanoTime() - t0) / 1_000_000} body=$body"
        } catch (e: Exception) {
            "FAIL at=$stage ip=$ip sni=$host ${e.javaClass.simpleName}: ${e.message} ms=${(System.nanoTime() - t0) / 1_000_000}"
        }
    }

    private val trustAll: SSLContext by lazy {
        SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            }), null)
        }
    }

    private fun connectTimed(block: () -> Unit): String {
        val t0 = System.nanoTime()
        return try {
            block()
            "CONNECTED in ${(System.nanoTime() - t0) / 1_000_000} ms"
        } catch (e: Exception) {
            "FAIL ${e.javaClass.simpleName}: ${e.message} after ${(System.nanoTime() - t0) / 1_000_000} ms"
        }
    }

    private fun httpGet(c: HttpURLConnection, withBody: Boolean = false): String = try {
        c.connectTimeout = 5000
        c.readTimeout = 8000
        c.useCaches = false
        val code = c.responseCode
        val raw = c.inputStream.use { it.readBytes() }
        c.disconnect()
        val snippet = if (withBody) " body=" + String(raw, Charsets.UTF_8).take(300).replace('\n', ' ') else ""
        "OK code=$code bytes=${raw.size}$snippet"
    } catch (e: Exception) {
        "FAIL ${e.javaClass.simpleName}: ${e.message}"
    }

    private fun transports(c: NetworkCapabilities?): String {
        if (c == null) return "none"
        val names = ArrayList<String>()
        if (c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) names += "wifi"
        if (c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) names += "cell"
        if (c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) names += "eth"
        if (c.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) names += "vpn"
        return names.joinToString("+").ifEmpty { "other" }
    }

    private fun label(cm: ConnectivityManager, n: Network): String =
        "handle=${n.networkHandle} ${transports(cm.getNetworkCapabilities(n))} iface=${cm.getLinkProperties(n)?.interfaceName}"

    private fun listNets(ctx: Context): String {
        val cm = ctx.getSystemService(ConnectivityManager::class.java)
        return cm.allNetworks.joinToString(" | ") { n ->
            val c = cm.getNetworkCapabilities(n)
            "${label(cm, n)} internet=${c?.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)} validated=${c?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)}"
        }
    }

    private fun pickNets(cm: ConnectivityManager, sel: String): List<Network> = cm.allNetworks.filter { n ->
        val c = cm.getNetworkCapabilities(n) ?: return@filter false
        if (c.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) return@filter false
        if (!c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) return@filter false
        when (sel) {
            "wifi" -> c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
            "cell" -> c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)
            "eth" -> c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
            else -> true
        }
    }

    /** A DNS A-record query for [name] (RD set, class IN). */
    private fun dnsQuery(name: String): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(0x12); out.write(0x34) // id
        out.write(0x01); out.write(0x00) // flags: RD
        out.write(0x00); out.write(0x01) // QDCOUNT 1
        repeat(6) { out.write(0x00) }    // AN, NS, AR = 0
        for (label in name.split('.')) {
            out.write(label.length)
            out.write(label.toByteArray(Charsets.US_ASCII))
        }
        out.write(0x00)
        out.write(0x00); out.write(0x01) // A
        out.write(0x00); out.write(0x01) // IN
        return out.toByteArray()
    }

    companion object {
        const val TAG = "A020P"
        // SO_MARK is hidden in the public SDK; 36 is the Linux value (asm-generic/socket.h).
        const val SO_MARK_LINUX = 36
    }
}
