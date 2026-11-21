package com.webfilter.spike.a19

import android.app.admin.DeviceAdminReceiver
import android.util.Log

/** Device admin receiver. Device owner and profile owner are set on this component. */
class AdminReceiver : DeviceAdminReceiver() {
    override fun onEnabled(context: android.content.Context, intent: android.content.Intent) {
        Log.i(TAG, "onEnabled user=${android.os.Process.myUserHandle()}")
    }

    override fun onProfileProvisioningComplete(context: android.content.Context, intent: android.content.Intent) {
        Log.i(TAG, "onProfileProvisioningComplete user=${android.os.Process.myUserHandle()}")
    }

    private companion object {
        const val TAG = "A19"
    }
}
