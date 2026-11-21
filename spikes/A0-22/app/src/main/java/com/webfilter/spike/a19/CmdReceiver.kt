package com.webfilter.spike.a19

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * adb entry point (A0.22). Example:
 *   adb -s emulator-5590 shell am broadcast -n com.webfilter.spike.a19/.CmdReceiver --es op dns-status
 * Ops: status, dns-set, dns-status, restrict-private-dns. Each result is logged under tag A19 and set as the result data.
 */
class CmdReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val op = intent.getStringExtra("op") ?: "status"
        val out = try {
            when (op) {
                "status" -> Harness.status(context)
                "dns-set" -> Harness.setPrivateDns(context)
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
        const val TAG = "A19"
    }
}
