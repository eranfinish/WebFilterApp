package com.webfilter.spike.a03b

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * Test harness entry point. Every call logs one line under tag A03B (read with `adb logcat -d -s A03B`).
 * Extras: `op` (status | chrome_set | chrome_clear | chrome_read | manifest), `mode` (array | json),
 * `blocklist` and `allowlist` (comma-separated rules). An empty allowlist is NOT sent (A0.3 bug: `--es allowlist ""` fails).
 */
class CmdReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val op = intent.getStringExtra("op") ?: "status"
        val args = mapOf(
            "op" to op,
            "mode" to intent.getStringExtra("mode"),
            "blocklist" to intent.getStringExtra("blocklist"),
            "allowlist" to intent.getStringExtra("allowlist"),
        )
        val result = Harness(context).run(args)
        Log.i(AdminReceiver.TAG, "op=$op -> $result")
        resultData = result
    }
}
