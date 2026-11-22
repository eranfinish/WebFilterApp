package com.webfilter.spike.a24

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.os.Process
import android.os.UserManager
import java.net.InetAddress

/**
 * A0.24 operations. Each function returns one line of text. It goes to logcat (tag A24) and to the broadcast result.
 * The package name is new (com.webfilter.spike.a24): this spike installs a fresh Device Owner on API 37.
 */
object Harness {
    /** Test resolver standing in for the future hub (family-filter DNS over TLS). Mechanism check only. */
    const val PRIVATE_DNS_HOST = "family.cloudflare-dns.com"

    fun admin(ctx: Context) = ComponentName(ctx, AdminReceiver::class.java)

    fun dpm(ctx: Context): DevicePolicyManager = ctx.getSystemService(DevicePolicyManager::class.java)

    fun status(ctx: Context): String {
        val d = dpm(ctx)
        val pkg = ctx.packageName
        return "myUser=${Process.myUserHandle()} pkg=$pkg " +
            "isAdminActive=${d.isAdminActive(admin(ctx))} " +
            "isDeviceOwnerApp=${d.isDeviceOwnerApp(pkg)} " +
            "isProfileOwnerApp=${d.isProfileOwnerApp(pkg)}"
    }

    /**
     * The dedicated API that A0.22 found refused on a device with unaffiliated users.
     * Runs on a worker thread because DPM runs a connectivity check to the DoT server.
     */
    fun setPrivateDnsHostApi(ctx: Context): String {
        val d = dpm(ctx)
        var error: Throwable? = null
        val worker = Thread {
            try {
                d.setGlobalPrivateDnsModeSpecifiedHost(admin(ctx), PRIVATE_DNS_HOST)
            } catch (t: Throwable) {
                error = t
            }
        }
        worker.start()
        worker.join(30_000)
        val err = error
        return if (err != null) "setGlobalPrivateDnsModeSpecifiedHost THREW ${err.javaClass.name}: ${err.message}; ${dnsStatusLine(ctx)}"
        else "setGlobalPrivateDnsModeSpecifiedHost returned OK; ${dnsStatusLine(ctx)}"
    }

    fun dnsStatus(ctx: Context): String = dnsStatusLine(ctx)

    private fun dnsStatusLine(ctx: Context): String {
        val d = dpm(ctx)
        return "mode=${modeName(d.getGlobalPrivateDnsMode(admin(ctx)))} host=${d.getGlobalPrivateDnsHost(admin(ctx))}"
    }

    private fun modeName(m: Int): String = when (m) {
        DevicePolicyManager.PRIVATE_DNS_MODE_OFF -> "OFF"
        DevicePolicyManager.PRIVATE_DNS_MODE_OPPORTUNISTIC -> "OPPORTUNISTIC"
        DevicePolicyManager.PRIVATE_DNS_MODE_PROVIDER_HOSTNAME -> "PROVIDER_HOSTNAME"
        else -> "UNKNOWN($m)"
    }

    /** DevicePolicyManager.setGlobalSetting (Device Owner only). Returns the exception text, or OK. */
    fun setGlobal(ctx: Context, key: String, value: String): String {
        return try {
            dpm(ctx).setGlobalSetting(admin(ctx), key, value)
            "OK setGlobalSetting($key, $value) returned without exception"
        } catch (t: Throwable) {
            "THREW ${t.javaClass.name}: ${t.message}"
        }
    }

    /** Adds DISALLOW_CONFIG_PRIVATE_DNS from the Device Owner. Only this restriction is added. */
    fun restrictPrivateDns(ctx: Context): String {
        dpm(ctx).addUserRestriction(admin(ctx), UserManager.DISALLOW_CONFIG_PRIVATE_DNS)
        return "added DISALLOW_CONFIG_PRIVATE_DNS (caller user ${Process.myUserHandle()})"
    }

    /** Resolves a host from the calling user. Blocking call: run it off the main thread. */
    fun resolve(host: String): String {
        return try {
            val addrs = InetAddress.getAllByName(host).joinToString(",") { it.hostAddress ?: "?" }
            "resolve $host -> $addrs"
        } catch (t: Throwable) {
            "resolve $host THREW ${t.javaClass.name}: ${t.message}"
        }
    }
}
