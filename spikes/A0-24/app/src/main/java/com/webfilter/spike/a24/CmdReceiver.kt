package com.webfilter.spike.a24

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import kotlin.concurrent.thread

/**
 * adb entry point (A0.24). Examples:
 *   adb -s emulator-5580 shell am broadcast -W -n com.webfilter.spike.a24/.CmdReceiver --es op status
 *   adb -s emulator-5580 shell am broadcast -W -n com.webfilter.spike.a24/.CmdReceiver --es op resolve --es host www.ynet.co.il
 * Ops: status, dns-hostname-api, dns-status, global-set (key, value), restrict-private-dns, resolve (host).
 * Each result is logged under tag A24 and returned as the broadcast result data.
 */
class CmdReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val op = intent.getStringExtra("op") ?: "status"
        if (op == "resolve") {
            // DNS lookups block: run them on a worker thread and finish the broadcast when done.
            val pending = goAsync()
            val host = intent.getStringExtra("host") ?: ""
            thread {
                val out = try {
                    Harness.resolve(host)
                } catch (e: Throwable) {
                    "EXC ${e.javaClass.name}: ${e.message}"
                }
                Log.i(TAG, "op=$op result=$out")
                pending.resultData = out
                pending.finish()
            }
            return
        }
        val out = try {
            when (op) {
                "status" -> Harness.status(context)
                "dns-hostname-api" -> Harness.setPrivateDnsHostApi(context)
                "dns-status" -> Harness.dnsStatus(context)
                "restrict-private-dns" -> Harness.restrictPrivateDns(context)
                "global-set" -> Harness.setGlobal(
                    context,
                    intent.getStringExtra("key") ?: "",
                    // am drops an empty extra value, so "<empty>" stands for "".
                    (intent.getStringExtra("value") ?: "").let { if (it == "<empty>") "" else it },
                )
                else -> "unknown op=$op"
            }
        } catch (e: Throwable) {
            "EXC ${e.javaClass.name}: ${e.message}"
        }
        Log.i(TAG, "op=$op result=$out")
        resultData = out
    }

    private companion object {
        const val TAG = "A24"
    }
}
