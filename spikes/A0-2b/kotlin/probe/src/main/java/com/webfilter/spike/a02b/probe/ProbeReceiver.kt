package com.webfilter.spike.a02b.probe

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL
import kotlin.concurrent.thread

/**
 * Client-side probe (uid of this app). Driven from adb:
 *   am broadcast -W -n com.webfilter.spike.a02b.probe/.ProbeReceiver --es op dns --es host example.org
 * Ops: dns (host) | http (url) | raw (ip, port) | bench (url, n) | rtt (url, n)
 * Results go to logcat under tag A02P and to the broadcast result data.
 */
class ProbeReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        // Fire and forget: the broadcast returns at once (a long op under goAsync() caused an ANR at ~10 s).
        // The result is written to logcat under tag A02P as "op=<op> -> <result>".
        val op = intent.getStringExtra("op") ?: "dns"
        thread(name = "probe-$op") {
            val result = try {
                run(op, intent)
            } catch (e: Throwable) {
                "EXC ${e.javaClass.name}: ${e.message}"
            }
            Log.i(TAG, "op=$op -> $result")
        }
    }

    private fun run(op: String, intent: Intent): String = when (op) {
        "dns" -> {
            val host = intent.getStringExtra("host") ?: "example.org"
            try {
                "OK ${InetAddress.getAllByName(host).joinToString { it.hostAddress ?: "?" }}"
            } catch (e: Exception) {
                "FAIL ${e.javaClass.simpleName}: ${e.message}"
            }
        }
        "raw" -> {
            val ip = intent.getStringExtra("ip") ?: "172.66.147.243"
            val port = intent.getIntExtra("port", 443)
            val t0 = System.nanoTime()
            try {
                Socket().use { it.connect(InetSocketAddress(ip, port), 5000) }
                "CONNECTED in ${(System.nanoTime() - t0) / 1_000_000} ms"
            } catch (e: Exception) {
                "FAIL ${e.javaClass.simpleName}: ${e.message} after ${(System.nanoTime() - t0) / 1_000_000} ms"
            }
        }
        "http" -> {
            val url = intent.getStringExtra("url") ?: "https://example.org/"
            try {
                val c = URL(url).openConnection() as HttpURLConnection
                c.connectTimeout = 8000; c.readTimeout = 8000
                val code = c.responseCode
                val bytes = c.inputStream.use { it.readBytes().size }
                c.disconnect()
                "OK code=$code bytes=$bytes"
            } catch (e: Exception) {
                "FAIL ${e.javaClass.simpleName}: ${e.message}"
            }
        }
        "rtt" -> {
            val url = intent.getStringExtra("url") ?: return "missing url"
            val n = intent.getIntExtra("n", 100)
            val samples = ArrayList<Double>()
            var fails = 0
            repeat(n) {
                val t0 = System.nanoTime()
                try {
                    val c = URL(url).openConnection() as HttpURLConnection
                    c.useCaches = false
                    c.connectTimeout = 8000; c.readTimeout = 8000
                    c.inputStream.use { it.readBytes() }
                    c.disconnect()
                    samples.add((System.nanoTime() - t0) / 1e6)
                } catch (e: Exception) {
                    fails++
                }
            }
            val s = samples.sorted()
            fun pct(p: Double) = if (s.isEmpty()) -1.0 else s[((s.size - 1) * p).toInt()]
            "RTT n=$n ok=${s.size} fail=$fails p50=${"%.1f".format(pct(0.5))}ms p95=${"%.1f".format(pct(0.95))}ms"
        }
        "bench" -> {
            val url = intent.getStringExtra("url") ?: return "missing url"
            val n = intent.getIntExtra("n", 3)
            val mbps = ArrayList<Double>()
            var fails = 0
            repeat(n) {
                val t0 = System.nanoTime()
                try {
                    val c = URL(url).openConnection() as HttpURLConnection
                    c.useCaches = false
                    c.connectTimeout = 8000; c.readTimeout = 20000
                    val buf = ByteArray(65536)
                    var total = 0L
                    c.inputStream.use { ins -> while (true) { val r = ins.read(buf); if (r < 0) break; total += r } }
                    c.disconnect()
                    val secs = (System.nanoTime() - t0) / 1e9
                    mbps.add(total / 1e6 / secs)
                    Log.i(TAG, "bench run bytes=$total secs=${"%.2f".format(secs)}")
                } catch (e: Exception) {
                    fails++
                    Log.i(TAG, "bench run failed ${e.javaClass.simpleName}: ${e.message}")
                }
            }
            val s = mbps.sorted()
            "BENCH n=$n ok=${s.size} fail=$fails median=${"%.2f".format(if (s.isEmpty()) -1.0 else s[s.size / 2])} MB/s runs=${mbps.map { "%.2f".format(it) }}"
        }
        else -> "unknown op $op"
    }

    companion object {
        const val TAG = "A02P"
    }
}
