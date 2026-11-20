package com.webfilter.spike.a02

import android.app.admin.DeviceAdminReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/** Spike A0.2. `dpm set-device-owner` points at this class. */
class AdminReceiver : DeviceAdminReceiver() {
    override fun onEnabled(context: Context, intent: Intent) {
        Log.i(DnsVpnService.TAG, "onEnabled (device admin)")
    }
}
