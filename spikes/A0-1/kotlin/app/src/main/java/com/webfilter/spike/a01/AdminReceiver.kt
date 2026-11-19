package com.webfilter.spike.a01

import android.app.admin.DeviceAdminReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.Log

/** The device admin / owner component. `dpm set-device-owner` points at this class. */
class AdminReceiver : DeviceAdminReceiver() {

    override fun onEnabled(context: Context, intent: Intent) {
        Log.i(TAG, "onEnabled: device admin active")
    }

    override fun onDisabled(context: Context, intent: Intent) {
        Log.i(TAG, "onDisabled: device admin inactive")
    }

    companion object {
        const val TAG = "A01"

        fun component(context: Context): ComponentName = ComponentName(context, AdminReceiver::class.java)
    }
}
