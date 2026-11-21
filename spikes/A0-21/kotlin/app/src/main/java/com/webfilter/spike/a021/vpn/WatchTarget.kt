package com.webfilter.spike.a021.vpn

import android.app.Service
import android.content.Intent
import android.os.Binder
import android.os.IBinder

/**
 * Bind target for the svc watchdog (spike A0.21). Lives in the :vpn process, like the tunnel, with no permission,
 * because TunVpnService requires BIND_VPN_SERVICE, which this app does not hold. Binding it lets the main process
 * learn, through onServiceDisconnected, when :vpn dies.
 */
class WatchTarget : Service() {
    private val binder = object : Binder() {}

    override fun onBind(intent: Intent?): IBinder = binder
}
