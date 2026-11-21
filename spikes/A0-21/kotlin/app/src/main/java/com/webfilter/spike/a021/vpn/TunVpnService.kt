package com.webfilter.spike.a021.vpn

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.util.Log
import com.webfilter.spike.a021.dns.Policy
import java.io.FileInputStream
import java.io.FileOutputStream
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.concurrent.thread

/**
 * Full-tunnel VpnService (spike A0.21). Runs in its own process (":vpn"), so `kill <pid>` and `am force-stop`
 * can be tested separately. Every IPv4 and IPv6 packet of the device goes into the tun; [Forwarder] answers or relays it.
 */
class TunVpnService : VpnService() {
    private var pfd: ParcelFileDescriptor? = null
    private var forwarder: Forwarder? = null
    private var output: FileOutputStream? = null

    private val statsReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action == ACTION_STATS) {
                val f = forwarder
                Log.i(TAG, "STATS pid=${android.os.Process.myPid()} running=${f != null} ${f?.stats() ?: ""}")
            } else if (intent.action == ACTION_STOP_SELF) {
                Log.i(TAG, "STOP_SELF requested by control receiver")
                stopSelf()
            } else if (intent.action == ACTION_KILL_SELF) {
                // Crash simulation (Q2): the VPN process ends itself, the same as an external kill of :vpn.
                Log.i(TAG, "KILL_SELF pid=${android.os.Process.myPid()} wall=${System.currentTimeMillis()}")
                android.os.Process.killProcess(android.os.Process.myPid())
            } else if (intent.action == ACTION_OWN_TEST) {
                val ip = intent.getStringExtra("ip") ?: "10.0.2.2"
                val port = intent.getIntExtra("port", 8080)
                thread(name = "own-test", isDaemon = true) { ownTest(ip, port) }
            } else if (intent.action == ACTION_RULES) {
                // DoH layers (A0.21). Set from the DPC; off by default.
                Policy.dohNames = intent.getBooleanExtra("dohNames", false)
                Policy.dohIp = intent.getBooleanExtra("dohIp", false)
                Policy.sni = intent.getBooleanExtra("sni", false)
                Policy.dohIps = intent.getStringExtra("ips").orEmpty().split(',').map { it.trim() }.filter { it.isNotEmpty() }.toSet()
                Log.i(TAG, "RULES applied ${Policy.summary()}")
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        val filter = IntentFilter().apply { addAction(ACTION_STATS); addAction(ACTION_STOP_SELF); addAction(ACTION_OWN_TEST); addAction(ACTION_KILL_SELF); addAction(ACTION_RULES) }
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(statsReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
        else registerReceiver(statsReceiver, filter)
        Log.i(TAG, "onCreate pid=${android.os.Process.myPid()} uptimeMs=${android.os.SystemClock.elapsedRealtime()} wall=${System.currentTimeMillis()}")
    }

    /** Watchdog binder: the main process binds with WATCH_ACTION and learns when this process dies. */
    private val watchBinder = object : android.os.Binder() {}

    override fun onBind(intent: Intent?): android.os.IBinder? {
        if (intent?.action == WATCH_ACTION) return watchBinder
        return super.onBind(intent)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // START_FLAG_RETRY (4) is set when the system restarts a service after its process died.
        val retry = (flags and START_FLAG_RETRY) != 0
        Log.i(TAG, "onStartCommand startId=$startId flags=$flags retry=$retry running=${pfd != null} wall=${System.currentTimeMillis()}")
        if (pfd == null) startTunnel()
        val sticky = getSharedPreferences("a021", MODE_PRIVATE).getBoolean("sticky", true)
        return if (sticky) START_STICKY else START_NOT_STICKY
    }

    @Synchronized
    fun startTunnel(): Boolean {
        if (pfd != null) return true
        Log.i(TAG, "upstream dns at start=${realDnsServer()} uptimeMs=${android.os.SystemClock.elapsedRealtime()}")
        val b = Builder()
            .setSession("A0.21 full tunnel")
            .setMtu(MTU)
            .addAddress(MY_IP4, 32)
            .addAddress(MY_IP6, 128)
            .addRoute("0.0.0.0", 0)
            .addRoute("::", 0)
            .addDnsServer(FAKE_DNS)
        val fd = b.establish()
        if (fd == null) {
            Log.i(TAG, "establish returned null (no consent?)")
            return false
        }
        pfd = fd
        val input = FileInputStream(fd.fileDescriptor)
        val out = FileOutputStream(fd.fileDescriptor)
        output = out
        val sink = object : Forwarder.Sink {
            override fun output(pkt: ByteArray) {
                synchronized(out) { out.write(pkt) }
            }
        }
        val sockets = object : Forwarder.Sockets {
            // A socket that is not protected must never be used: its traffic would enter our own tun.
            override fun tcp(): Socket {
                // A fresh Socket() has no native fd until bind/connect, so bind first; otherwise protect() returns false.
                val s = Socket()
                s.bind(InetSocketAddress(0))
                if (!protect(s)) { s.close(); throw java.io.IOException("protect(Socket) failed") }
                return s
            }
            override fun udp(): DatagramSocket {
                val s = DatagramSocket()
                if (!protect(s)) { s.close(); throw java.io.IOException("protect(DatagramSocket) failed") }
                return s
            }
        }
        val f = Forwarder(
            myIp = ip4(MY_IP4), fakeDnsIp = ip4(FAKE_DNS), upstreamDns = { lazyUpstream() },
            mtu = MTU, sockets = sockets, sink = sink,
            log = { Log.i(TAG, it) },
        )
        forwarder = f
        Log.i(TAG, "establish ok pid=${android.os.Process.myPid()} uptimeMs=${android.os.SystemClock.elapsedRealtime()} wall=${System.currentTimeMillis()}")
        // Self-test: can this process protect its own sockets right now? (diagnoses the protect-failure flood)
        val selfSock = Socket()
        val selfUnbound = protect(selfSock) // expected false: no fd before bind (documents the cause)
        selfSock.close()
        val boundSock = Socket()
        boundSock.bind(InetSocketAddress(0))
        val selfProtect = protect(boundSock)
        boundSock.close()
        Log.i(TAG, "SELFTEST protect(unbound Socket)=$selfUnbound protect(bound Socket)=$selfProtect")
        val selfDg = DatagramSocket()
        val selfDgProtect = protect(selfDg)
        selfDg.close()
        Log.i(TAG, "SELFTEST protect(Socket)=$selfProtect protect(DatagramSocket)=$selfDgProtect")
        thread(name = "tun-reader", isDaemon = true) {
            val buf = ByteArray(32768)
            try {
                while (true) {
                    val n = input.read(buf)
                    if (n < 0) break
                    if (n > 0) {
                        try { f.onPacket(buf, n) } catch (e: Exception) {
                            Log.w(TAG, "onPacket failed: ${e.javaClass.simpleName} ${e.message}")
                        }
                    }
                }
                Log.i(TAG, "tun read returned EOF")
            } catch (e: Exception) {
                Log.i(TAG, "tun reader stopped: ${e.javaClass.simpleName} ${e.message}")
            }
        }
        return true
    }

    /**
     * Q3: which routes can this process use to reach an outside host while the tunnel is up under lockdown?
     * (a) protect()ed socket on the default path, (b) socket bound to the Wi-Fi Network object (no protect),
     * (c) plain socket on the default path (goes into our own tun and is relayed by Forwarder).
     */
    private fun ownTest(ip: String, port: Int) {
        val f = forwarder
        val before = f?.stats() ?: "no-forwarder"
        fun attempt(label: String, mk: () -> Socket) {
            val c0 = forwarder?.stats() ?: "no-forwarder"
            val t0 = System.nanoTime()
            try {
                mk().use { it.connect(InetSocketAddress(ip, port), 4000) }
                Log.i(TAG, "OWN $label OK in ${(System.nanoTime() - t0) / 1_000_000} ms")
            } catch (e: Exception) {
                Log.i(TAG, "OWN $label FAIL ${e.javaClass.simpleName}: ${e.message}")
            }
            Log.i(TAG, "OWN $label counters before=[$c0] after=[${forwarder?.stats() ?: "no-forwarder"}]")
        }
        // protect() needs an fd: bind before protect (see the SELFTEST lines).
        attempt("protected") { Socket().also { it.bind(InetSocketAddress(0)); protectOrLog(it) } }
        attempt("plain-default-route") { Socket() }
        val cm = getSystemService(ConnectivityManager::class.java)
        val wifi = cm.allNetworks.firstOrNull { n ->
            cm.getNetworkCapabilities(n)?.let { it.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI) } == true
        }
        if (wifi == null) Log.i(TAG, "OWN bound-wifi: no wifi network object")
        else attempt("bound-to-wifi-network") { wifi.socketFactory.createSocket() }
        Log.i(TAG, "OWN counters before=[$before] after=[${forwarder?.stats() ?: "no-forwarder"}]")
    }

    private fun protectOrLog(s: Socket) {
        if (!protect(s)) Log.w(TAG, "protect(Socket) failed")
    }

    private fun protectOrLog(s: DatagramSocket) {
        if (!protect(s)) Log.w(TAG, "protect(DatagramSocket) failed")
    }

    /**
     * The DNS server of an underlying (non-VPN) network with INTERNET, read from LinkProperties (not hard-coded).
     * Not cached at start: at boot the network may have no DNS yet, so the forwarder asks again per query.
     */
    private fun realDnsServer(): InetAddress? {
        val cm = getSystemService(ConnectivityManager::class.java)
        for (n in cm.allNetworks) {
            val caps = cm.getNetworkCapabilities(n) ?: continue
            if (caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN)) continue
            if (!caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)) continue
            val lp = cm.getLinkProperties(n) ?: continue
            val dns = lp.dnsServers.firstOrNull { it.address.size == 4 }
            if (dns != null) return dns
        }
        return null
    }

    @Volatile private var upstreamCache: InetSocketAddress? = null

    private fun lazyUpstream(): InetSocketAddress? {
        upstreamCache?.let { return it }
        val a = realDnsServer() ?: return null
        val s = InetSocketAddress(a, 53)
        upstreamCache = s
        Log.i(TAG, "upstream dns resolved $a uptimeMs=${android.os.SystemClock.elapsedRealtime()}")
        return s
    }

    /** Idempotent: safe from onRevoke and onDestroy. Does not touch the receiver (that belongs to onDestroy). */
    @Synchronized
    private fun stopTunnel() {
        if (pfd == null && forwarder == null) return
        Log.i(TAG, "stopTunnel pid=${android.os.Process.myPid()} uptimeMs=${android.os.SystemClock.elapsedRealtime()}")
        forwarder?.shutdown()
        forwarder = null
        try { pfd?.close() } catch (_: Exception) {}
        pfd = null
        output = null
    }

    override fun onDestroy() {
        Log.i(TAG, "onDestroy pid=${android.os.Process.myPid()} uptimeMs=${android.os.SystemClock.elapsedRealtime()}")
        stopTunnel()
        try { unregisterReceiver(statsReceiver) } catch (_: Exception) {}
        super.onDestroy()
    }

    override fun onRevoke() {
        Log.i(TAG, "onRevoke pid=${android.os.Process.myPid()}")
        stopTunnel()
        super.onRevoke()
    }

    companion object {
        const val TAG = "A021"
        const val ACTION_STATS = "com.webfilter.spike.a021.STATS"
        const val ACTION_STOP_SELF = "com.webfilter.spike.a021.STOP_SELF"
        const val ACTION_OWN_TEST = "com.webfilter.spike.a021.OWN_TEST"
        const val ACTION_KILL_SELF = "com.webfilter.spike.a021.KILL_SELF"
        const val ACTION_RULES = "com.webfilter.spike.a021.RULES"
        const val WATCH_ACTION = "com.webfilter.spike.a021.WATCH"
        const val MTU = 1400
        const val MY_IP4 = "10.8.0.2"
        const val MY_IP6 = "fd00:8::2"
        const val FAKE_DNS = "10.8.0.1"

        fun ip4(s: String): Int {
            val p = s.split('.').map { it.toInt() }
            return (p[0] shl 24) or (p[1] shl 16) or (p[2] shl 8) or p[3]
        }
    }
}
