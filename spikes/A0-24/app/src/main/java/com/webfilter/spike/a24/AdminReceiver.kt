package com.webfilter.spike.a24

import android.app.admin.DeviceAdminReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/** Device admin receiver. The Device Owner is set on this component. */
class AdminReceiver : DeviceAdminReceiver() {
    override fun onEnabled(context: Context, intent: Intent) {
        Log.i(TAG, "onEnabled user=${android.os.Process.myUserHandle()}")
    }

    private companion object {
        const val TAG = "A24"
    }
}
