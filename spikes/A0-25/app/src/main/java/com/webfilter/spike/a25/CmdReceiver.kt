package com.webfilter.spike.a25

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * adb entry point. Example:
 *   adb -s emulator-5590 shell am broadcast -W --user 0 -n com.webfilter.spike.a25/.CmdReceiver --es op status
 * Each result is logged under tag A25 and set as the broadcast result data.
 */
class CmdReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val op = intent.getStringExtra("op") ?: "status"
        val id = intent.getStringExtra("id")?.toIntOrNull()
        val out = try {
            when (op) {
                "status" -> Harness.status(context)
                "create" -> Harness.create(context, intent.getStringExtra("name") ?: "a25-child")
                "affiliate" -> Harness.affiliate(context)
                "start" -> Harness.start(context, requireNotNull(id) { "need --es id" })
                "switch" -> Harness.switchTo(context, requireNotNull(id) { "need --es id" })
                "stop" -> Harness.stop(context, requireNotNull(id) { "need --es id" })
                else -> "unknown op=$op"
            }
        } catch (e: Throwable) {
            "EXC ${e.javaClass.name}: ${e.message}"
        }
        Log.i(TAG, "op=$op result=$out")
        resultData = out
    }

    private companion object {
        const val TAG = "A25"
    }
}
