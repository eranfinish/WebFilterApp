package com.webfilter.spike.a19

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.os.Process
import android.os.UserManager

/**
 * A0.22 operations. The package name stays com.webfilter.spike.a19 (same as A0.19) so the Device Owner on user 0 is kept.
 * Each function returns one line of text; it goes to logcat (tag A19) and to the broadcast result.
 */
object Harness {
    /** Test resolver for the mechanism only (family-filter DNS over TLS). */
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

    /** Sets global Private DNS to the host (strict mode: the host is required). Device Owner only. */
    fun setPrivateDns(ctx: Context): String {
        // DPM runs a network connectivity check to the DoT server; it must not run on the main thread.
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
        return if (err != null) "set FAILED ${err.javaClass.name}: ${err.message}; ${dnsStatusLine(ctx)}"
        else "set ${dnsStatusLine(ctx)}"
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

    /** Adds DISALLOW_CONFIG_PRIVATE_DNS from the Device Owner. Only this restriction is added in A0.22. */
    fun restrictPrivateDns(ctx: Context): String {
        dpm(ctx).addUserRestriction(admin(ctx), UserManager.DISALLOW_CONFIG_PRIVATE_DNS)
        return "added DISALLOW_CONFIG_PRIVATE_DNS (caller user ${Process.myUserHandle()})"
    }
}
