package com.webfilter.spike.a021

import android.app.admin.DeviceAdminReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.Log

/** Device admin / Device Owner receiver. `dpm set-device-owner` points at this class. */
class AdminReceiver : DeviceAdminReceiver() {
    override fun onEnabled(context: Context, intent: Intent) {
        Log.i(TAG, "onEnabled")
    }

    override fun onDisabled(context: Context, intent: Intent) {
        Log.i(TAG, "onDisabled")
    }

    companion object {
        const val TAG = "A021"
        fun component(context: Context) = ComponentName(context, AdminReceiver::class.java)
    }
}
