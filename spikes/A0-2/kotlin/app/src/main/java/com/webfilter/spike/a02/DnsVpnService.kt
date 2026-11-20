package com.webfilter.spike.a02

import android.content.Intent
import android.net.VpnService
import android.os.IBinder
import android.os.ParcelFileDescriptor
import android.os.Process
import android.util.Log
import com.webfilter.spike.a02.dns.DnsMessage
import com.webfilter.spike.a02.dns.Ipv4Udp
import com.webfilter.spike.a02.dns.Rules
import java.io.FileInputStream
import java.io.FileOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.Executors

/**
 * Spike A0.2 (throwaway). A DNS-only VpnService. Only 10.111.0.53/32 is routed into the tun, so only
 * DNS queries to that address reach us. Blocked names get NXDOMAIN. Other names are forwarded to
 * UPSTREAM through a protect()ed socket. Without lockdown, other traffic keeps the normal network.
 * With always-on lockdown it is refused instead (see REPORT.md): the design does not hold under lockdown.
 */
class DnsVpnService : VpnService() {
    companion object {
        const val TAG = "A02"
        const val ACTION_STOP = "com.webfilter.spike.a02.STOP"
        const val TUN_IP = "10.111.0.1"
        const val DNS_IP = "10.111.0.53"
        private val DNS_BYTES = byteArrayOf(10, 111, 0, 53)
        /** The network's DNS as reported by LinkProperties (DnsAddresses: [/10.0.2.3] on this emulator). The real agent must read it from LinkProperties, not hard-code it. */
        const val UPSTREAM = "10.0.2.3"

        /** True while the tun is open. Read by the harness status op. */
        @Volatile
        var isRunning = false
            private set

        fun now(): Long = System.currentTimeMillis()
    }

    private var tun: ParcelFileDescriptor? = null
    private val pool = Executors.newCachedThreadPool()

    override fun onCreate() {
        super.onCreate()
        Log.i(TAG, "onCreate pid=${Process.myPid()} uid=${Process.myUid()} t=${now()}")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i(TAG, "onStartCommand action=${intent?.action} startId=$startId t=${now()}")
        if (intent?.action == ACTION_STOP) {
            stopTunnel("stop-request")
            stopSelf()
            return START_NOT_STICKY
        }
        startTunnel()
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? {
        Log.i(TAG, "onBind action=${intent?.action} t=${now()}")
        startTunnel()
        return super.onBind(intent)
    }

    override fun onRevoke() {
        Log.w(TAG, "onRevoke t=${now()}")
        stopTunnel("revoke")
        super.onRevoke()
    }

    override fun onDestroy() {
        Log.i(TAG, "onDestroy t=${now()}")
        stopTunnel("destroy")
        pool.shutdownNow()
        super.onDestroy()
    }

    @Synchronized
    private fun startTunnel() {
        if (isRunning) return
        val pfd: ParcelFileDescriptor? = try {
            Builder()
                .setSession("A0-2 DNS")
                .addAddress(TUN_IP, 32)
                .addDnsServer(DNS_IP)
                .addRoute(DNS_IP, 32)
                .setMtu(1280)
                .setBlocking(true)
                .establish()
        } catch (e: Exception) {
            Log.e(TAG, "establish threw ${e.javaClass.name}: ${e.message}")
            null
        }
        if (pfd == null) {
            Log.e(TAG, "establish() returned null (VPN consent missing or revoked) t=${now()}")
            return
        }
        tun = pfd
        isRunning = true
        Log.i(TAG, "establish ok fd=${pfd.fd} t=${now()}")
        Thread({ readLoop(pfd) }, "a02-tun").start()
    }

    private fun readLoop(pfd: ParcelFileDescriptor) {
        val input = FileInputStream(pfd.fileDescriptor)
        val output = FileOutputStream(pfd.fileDescriptor)
        val buf = ByteArray(32767)
        try {
            while (true) {
                val n = input.read(buf)
                if (n < 0) break
                if (n == 0) continue
                handle(buf.copyOf(n), output)
            }
        } catch (e: Exception) {
            Log.w(TAG, "read loop ended: ${e.javaClass.simpleName}: ${e.message} t=${now()}")
        }
        Log.i(TAG, "read loop exit t=${now()}")
    }

    /** One-line summary of a packet for the drop log: IP version, protocol, destination and destination port. */
    private fun describe(p: ByteArray): String {
        if (p.isEmpty()) return "empty"
        val ver = (p[0].toInt() and 0xFF) ushr 4
        if (ver != 4 || p.size < 24) {
            val hex = p.take(16).joinToString(" ") { String.format("%02x", it.toInt() and 0xFF) }
            return "ver=$ver len=${p.size} head=[$hex]"
        }
        val proto = p[9].toInt() and 0xFF
        val dst = p.copyOfRange(16, 20).joinToString(".") { (it.toInt() and 0xFF).toString() }
        val dport = ((p[22].toInt() and 0xFF) shl 8) or (p[23].toInt() and 0xFF)
        return "ver=4 proto=$proto dst=$dst dport=$dport len=${p.size}"
    }

    private fun handle(packet: ByteArray, output: FileOutputStream) {
        val dg = Ipv4Udp.parse(packet)
        if (dg == null) {
            Log.i(TAG, "drop: not an IPv4 UDP packet (${describe(packet)}) t=${now()}")
            return
        }
        if (dg.dstPort != 53) {
            Log.i(TAG, "drop: UDP port ${dg.dstPort} (not DNS) (${describe(packet)}) t=${now()}")
            return
        }
        val q = DnsMessage.parseQuestion(dg.payload)
        if (q == null) {
            Log.i(TAG, "drop: unparseable DNS query t=${now()}")
            return
        }
        if (Rules.isBlocked(q.name)) {
            Log.i(TAG, "DNS name=${q.name} type=${q.qtype} action=BLOCK t=${now()}")
            reply(output, dg, DnsMessage.nxdomainFor(dg.payload, q))
        } else {
            pool.execute {
                val resp = forward(dg.payload)
                Log.i(TAG, "DNS name=${q.name} type=${q.qtype} action=FORWARD ok=${resp != null} t=${now()}")
                if (resp != null) reply(output, dg, resp)
            }
        }
    }

    /** Sends the query to UPSTREAM through a protect()ed socket, so it leaves on the underlying network. */
    private fun forward(query: ByteArray): ByteArray? {
        return try {
            DatagramSocket().use { s ->
                if (!protect(s)) {
                    Log.e(TAG, "protect() failed t=${now()}")
                    return@use null
                }
                s.soTimeout = 3000
                s.connect(InetSocketAddress(InetAddress.getByName(UPSTREAM), 53))
                s.send(DatagramPacket(query, query.size))
                val rb = ByteArray(4096)
                val p = DatagramPacket(rb, rb.size)
                s.receive(p)
                rb.copyOf(p.length)
            }
        } catch (e: Exception) {
            Log.w(TAG, "forward failed ${e.javaClass.simpleName}: ${e.message} t=${now()}")
            null
        }
    }

    /** Writes a DNS answer back into the tun as IPv4/UDP, from the DNS address to the querying app. */
    private fun reply(output: FileOutputStream, dg: Ipv4Udp.Datagram, payload: ByteArray) {
        val frame = Ipv4Udp.build(
            srcIp = DNS_BYTES,
            dstIp = dg.srcIp,
            srcPort = 53,
            dstPort = dg.srcPort,
            payload = payload,
        )
        synchronized(output) { output.write(frame) }
    }

    @Synchronized
    private fun stopTunnel(reason: String) {
        if (!isRunning) {
            Log.i(TAG, "stopTunnel($reason): not running t=${now()}")
            return
        }
        isRunning = false
        try {
            tun?.close()
        } catch (e: Exception) {
            Log.w(TAG, "close tun: ${e.message}")
        }
        tun = null
        Log.i(TAG, "stopTunnel reason=$reason t=${now()}")
    }
}

