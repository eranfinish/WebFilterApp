package com.webfilter.spike.a02

import android.app.admin.DevicePolicyManager
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.VpnService
import android.os.Process
import android.os.UserManager
import android.util.Log

/**
 * Spike A0.2 test harness, driven from adb:
 *   adb -s <serial> shell am broadcast -W -n com.webfilter.spike.a02/.CmdReceiver --es op <op> [--es arg <arg>]
 * Every op logs one line under tag "A02" and returns the same text as the broadcast result data.
 */
class CmdReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val op = intent.getStringExtra("op") ?: "status"
        val arg = intent.getStringExtra("arg")
        // Some DPC calls (e.g. setGlobalPrivateDnsModeSpecifiedHost) must not run on the main thread.
        val pending = goAsync()
        Thread {
            val out = try {
                run(context, op, arg)
            } catch (e: Throwable) {
                "EXC ${e.javaClass.name}: ${e.message}"
            }
            Log.i(DnsVpnService.TAG, "op=$op arg=$arg t=${DnsVpnService.now()} -> $out")
            pending.resultData = out
            if (op == "crash") {
                Process.killProcess(Process.myPid())
            }
            pending.finish()
        }.start()
    }

    private fun run(context: Context, op: String, arg: String?): String {
        val dpm = context.getSystemService(DevicePolicyManager::class.java)
        val admin = ComponentName(context, AdminReceiver::class.java)
        val pkg = context.packageName
        return when (op) {
            "status" -> status(context, dpm, admin, pkg)
            "consent" -> {
                val intent = VpnService.prepare(context)
                "prepare=" + if (intent == null) "null(granted)" else "intent(consent-needed)"
            }
            // Always-on with lockdown: no network while the VPN is down.
            "alwayson_on" -> setAlwaysOn(context, dpm, admin, pkg, lockdown = true)
            "alwayson_nolock" -> setAlwaysOn(context, dpm, admin, pkg, lockdown = false)
            "alwayson_off" -> {
                dpm.setAlwaysOnVpnPackage(admin, null, false)
                "OK always-on cleared"
            }
            "start" -> {
                context.startService(Intent(context, DnsVpnService::class.java))
                "OK start-requested"
            }
            "stopvpn" -> {
                context.startService(Intent(context, DnsVpnService::class.java).setAction(DnsVpnService.ACTION_STOP))
                "OK stop-requested"
            }
            "crash" -> {
                "OK crashing pid=${Process.myPid()}"
            }
            "restrict" -> {
                dpm.addUserRestriction(admin, restrictionKey(arg))
                "OK restricted $arg"
            }
            "unrestrict" -> {
                dpm.clearUserRestriction(admin, restrictionKey(arg))
                "OK unrestricted $arg"
            }
            "restrictions" -> "restrictions=" + dpm.getUserRestrictions(admin).toString()
            "privdns_host" -> {
                dpm.setGlobalPrivateDnsModeSpecifiedHost(admin, requireNotNull(arg) { "arg (host) required" })
                "OK global private dns strict host=$arg"
            }
            "privdns_opp" -> {
                dpm.setGlobalPrivateDnsModeOpportunistic(admin)
                "OK global private dns opportunistic"
            }
            "create_user" -> {
                val h = dpm.createAndManageUser(admin, "a02-kid-" + DnsVpnService.now(), admin, null, DevicePolicyManager.SKIP_SETUP_WIZARD)
                "OK created handle=$h"
            }
            else -> "UNKNOWN op=$op"
        }
    }

    private fun status(context: Context, dpm: DevicePolicyManager, admin: ComponentName, pkg: String): String {
        val um = context.getSystemService(UserManager::class.java)
        return buildString {
            append("pid=").append(Process.myPid())
            append(" uid=").append(Process.myUid())
            append(" deviceOwner=").append(dpm.isDeviceOwnerApp(pkg))
            append(" profileOwner=").append(dpm.isProfileOwnerApp(pkg))
            append(" alwaysOnPkg=").append(dpm.getAlwaysOnVpnPackage(admin))
            append(" lockdown=").append(dpm.isAlwaysOnVpnLockdownEnabled(admin))
            append(" vpnRunning=").append(DnsVpnService.isRunning)
            append(" profiles=").append(um.userProfiles.size)
        }
    }

    private fun setAlwaysOn(context: Context, dpm: DevicePolicyManager, admin: ComponentName, pkg: String, lockdown: Boolean): String {
        val consent = if (VpnService.prepare(context) == null) "granted" else "needed"
        dpm.setAlwaysOnVpnPackage(admin, pkg, lockdown) // throws NameNotFoundException or UnsupportedOperationException on failure
        val readBackPkg = dpm.getAlwaysOnVpnPackage(admin)
        val readBackLockdown = dpm.isAlwaysOnVpnLockdownEnabled(admin)
        return "OK set always-on lockdown=$lockdown consent=$consent readBackPkg=$readBackPkg readBackLockdown=$readBackLockdown"
    }

    private fun restrictionKey(arg: String?): String = when (arg) {
        "config_vpn" -> UserManager.DISALLOW_CONFIG_VPN
        "config_private_dns" -> UserManager.DISALLOW_CONFIG_PRIVATE_DNS
        else -> throw IllegalArgumentException("unknown restriction arg=$arg")
    }
}
