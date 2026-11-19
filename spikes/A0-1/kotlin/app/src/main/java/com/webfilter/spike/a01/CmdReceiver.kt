package com.webfilter.spike.a01

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * Test harness entry point. Every call logs one line under tag A01 (read with `adb logcat -d -s A01`).
 * Extras: `op` (see [Op]), `pkg` (package for hide/unhide/ishidden, default [SpikeNames.DEFAULT_PACKAGE]).
 */
class CmdReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val raw = intent.getStringExtra("op")
        val op = Op.parse(raw)
        val pkg = intent.getStringExtra("pkg") ?: SpikeNames.DEFAULT_PACKAGE
        val result = if (op == null) {
            "ERR unknown op '$raw'; valid: ${Op.ALL.joinToString(",") { it.wire }}"
        } else {
            Harness(context).run(op, pkg)
        }
        Log.i(AdminReceiver.TAG, "op=$raw pkg=$pkg -> $result")
        resultData = result
    }
}
