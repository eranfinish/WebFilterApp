package com.webfilter.spike.a02.probe

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL

/**
 * Spike A0.2 second app. Requests, driven from adb (results are logged under tag "A02P" and returned as result data):
 *   am broadcast -W -n com.webfilter.spike.a02.probe/.ProbeReceiver --es op resolve --es arg blocked.example
 *   ops: ping | resolve <host> | http <url> | tcp <ip:port>
 */
class ProbeReceiver : BroadcastReceiver() {
    companion object {
        const val TAG = "A02P"
        fun now(): Long = System.currentTimeMillis()
    }

    override fun onReceive(context: Context, intent: Intent) {
        val op = intent.getStringExtra("op") ?: return
        val arg = intent.getStringExtra("arg") ?: ""
        val pending = goAsync()
        Thread {
            val t0 = now()
            val out = try {
                run(op, arg)
            } catch (e: Throwable) {
                "EXC ${e.javaClass.name}: ${e.message}"
            }
            Log.i(TAG, "op=$op arg=$arg t=$t0 ms=${now() - t0} -> $out")
            pending.resultData = out
            pending.finish()
        }.start()
    }

    private fun run(op: String, arg: String): String = when (op) {
        "ping" -> "pong uid=${android.os.Process.myUid()}"
        "resolve" -> resolve(arg)
        "http" -> http(arg)
        "tcp" -> tcp(arg)
        "watch" -> watch(arg)
        "watchdns" -> watchDns(arg)
        else -> "UNKNOWN op=$op"
    }

    /**
     * Sends a hand-built DNS query (A, name x<n>.blocked.example) to 10.111.0.53:53 every ~250 ms for <seconds>.
     * A reply of any kind counts as UP. Logs each UP/DOWN change with the device clock.
     */
    private fun watchDns(arg: String): String {
        val deadline = now() + arg.trim().toLong() * 1000
        var last: Boolean? = null
        var n = 0
        val events = StringBuilder()
        val dnsAddr = InetAddress.getByName("10.111.0.53")
        while (now() < deadline) {
            n++
            val q = buildQuery(n, "x$n.blocked.example")
            val up = try {
                DatagramSocket().use { s ->
                    s.soTimeout = 300
                    s.send(DatagramPacket(q, q.size, dnsAddr, 53))
                    s.receive(DatagramPacket(ByteArray(512), 512))
                }
                true
            } catch (e: Exception) {
                false
            }
            if (up != last) {
                val line = "WATCHDNS ${if (up) "UP" else "DOWN"} t=${now()}"
                Log.i(TAG, line)
                events.append(line).append("; ")
                last = up
            }
            Thread.sleep(250)
        }
        return "OK watchdns events=$events"
    }

    private fun buildQuery(id: Int, name: String): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        out.write(id ushr 8)
        out.write(id and 0xFF)
        out.write(0x01); out.write(0x00) // RD
        out.write(0x00); out.write(0x01) // QDCOUNT 1
        out.write(byteArrayOf(0, 0, 0, 0, 0, 0)) // ANCOUNT, NSCOUNT, ARCOUNT
        for (label in name.split('.')) {
            out.write(label.length)
            out.write(label.toByteArray())
        }
        out.write(0)
        out.write(0x00); out.write(0x01) // QTYPE A
        out.write(0x00); out.write(0x01) // QCLASS IN
        return out.toByteArray()
    }

    /**
     * Polls a TCP connect every ~250 ms for the given seconds and logs each UP/DOWN change with the device clock.
     * arg = "<ip> <port> <seconds>". Used for the fail-closed timing (Q2) and the reboot timing (Q1).
     */
    private fun watch(arg: String): String {
        val (ip, port, secs) = arg.split(' ')
        val deadline = now() + secs.toLong() * 1000
        var last: Boolean? = null
        val events = StringBuilder()
        while (now() < deadline) {
            val up = try {
                Socket().use { s -> s.connect(InetSocketAddress(ip, port.toInt()), 400) }
                true
            } catch (e: Exception) {
                false
            }
            if (up != last) {
                val line = "WATCH ${if (up) "UP" else "DOWN"} t=${now()}"
                Log.i(TAG, line)
                events.append(line).append("; ")
                last = up
            }
            Thread.sleep(250)
        }
        return "OK watch $ip:$port events=$events"
    }

    private fun resolve(host: String): String = try {
        val addrs = InetAddress.getAllByName(host).joinToString(",") { it.hostAddress ?: "?" }
        "OK addrs=$addrs"
    } catch (e: Exception) {
        "FAIL ${e.javaClass.simpleName}: ${e.message}"
    }

    private fun http(url: String): String = try {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 6000
        c.readTimeout = 6000
        try {
            "OK code=${c.responseCode}"
        } finally {
            c.disconnect()
        }
    } catch (e: Exception) {
        "FAIL ${e.javaClass.simpleName}: ${e.message}"
    }

    private fun tcp(hostPort: String): String = try {
        val (host, port) = hostPort.split(':')
        Socket().use { s ->
            s.connect(InetSocketAddress(host, port.toInt()), 4000)
            "OK connect"
        }
    } catch (e: Exception) {
        "FAIL ${e.javaClass.simpleName}: ${e.message}"
    }
}
