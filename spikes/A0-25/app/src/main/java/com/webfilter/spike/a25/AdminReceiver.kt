package com.webfilter.spike.a25

import android.app.admin.DeviceAdminReceiver
import android.content.Context
import android.content.Intent
import android.os.Process
import android.util.Log

/** Device admin receiver. Device owner (user 0) and profile owner (child user) are set on this component. */
class AdminReceiver : DeviceAdminReceiver() {
    override fun onEnabled(context: Context, intent: Intent) {
        Log.i(TAG, "onEnabled user=${Process.myUserHandle()}")
    }

    override fun onProfileProvisioningComplete(context: Context, intent: Intent) {
        Log.i(TAG, "onProfileProvisioningComplete user=${Process.myUserHandle()}")
    }

    private companion object {
        const val TAG = "A25"
    }
}
