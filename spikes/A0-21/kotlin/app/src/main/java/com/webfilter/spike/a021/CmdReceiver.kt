package com.webfilter.spike.a021

import android.app.admin.DevicePolicyManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import com.webfilter.spike.a021.vpn.TunVpnService

/**
 * Test driver, main process. Driven from adb:
 *   adb -s emulator-5580 shell am broadcast -W -n com.webfilter.spike.a021/.CmdReceiver --es op <op>
 * Ops: status | lockdown_on | lockdown_off | start | stop_self | stats
 */
class CmdReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val op = intent.getStringExtra("op") ?: "status"
        val dpm = context.getSystemService(DevicePolicyManager::class.java)
        val admin = AdminReceiver.component(context)
        val pkg = context.packageName
        val result = try {
            when (op) {
                "status" -> {
                    val isOwner = dpm.isDeviceOwnerApp(pkg)
                    val alwaysOn = dpm.getAlwaysOnVpnPackage(admin)
                    val lockdown = dpm.isAlwaysOnVpnLockdownEnabled(admin)
                    "owner=$isOwner alwaysOnPkg=$alwaysOn lockdown=$lockdown pid=${android.os.Process.myPid()}"
                }
                "lockdown_on" -> {
                    dpm.setAlwaysOnVpnPackage(admin, pkg, true)
                    "set always-on pkg=$pkg lockdown=true; readback pkg=${dpm.getAlwaysOnVpnPackage(admin)} lockdown=${dpm.isAlwaysOnVpnLockdownEnabled(admin)}"
                }
                "lockdown_off" -> {
                    dpm.setAlwaysOnVpnPackage(admin, null, false)
                    "always-on cleared; readback pkg=${dpm.getAlwaysOnVpnPackage(admin)}"
                }
                "start" -> {
                    context.startService(Intent(context, TunVpnService::class.java))
                    "startService requested"
                }
                "stop_self" -> {
                    context.sendBroadcast(Intent(TunVpnService.ACTION_STOP_SELF).setPackage(pkg))
                    "STOP_SELF broadcast sent"
                }
                "stats" -> {
                    context.sendBroadcast(Intent(TunVpnService.ACTION_STATS).setPackage(pkg))
                    "STATS broadcast sent"
                }
                "kill_vpn" -> {
                    context.sendBroadcast(Intent(TunVpnService.ACTION_KILL_SELF).setPackage(pkg))
                    "KILL_SELF broadcast sent"
                }
                "own_test" -> {
                    context.sendBroadcast(Intent(TunVpnService.ACTION_OWN_TEST).setPackage(pkg)
                        .putExtra("ip", intent.getStringExtra("ip")).putExtra("port", intent.getIntExtra("port", 8080)))
                    "OWN_TEST broadcast sent"
                }
                "rules" -> {
                    // DoH layers in the :vpn process: dohNames (DNS), dohIp (IP refuse), sni (ClientHello reset).
                    context.sendBroadcast(Intent(TunVpnService.ACTION_RULES).setPackage(pkg)
                        .putExtra("dohNames", intent.getBooleanExtra("dohNames", false))
                        .putExtra("dohIp", intent.getBooleanExtra("dohIp", false))
                        .putExtra("sni", intent.getBooleanExtra("sni", false))
                        .putExtra("ips", intent.getStringExtra("ips") ?: ""))
                    "RULES broadcast sent"
                }
                "chrome_doh" -> {
                    // Chrome managed policy through the device owner's app restrictions. mode = off | secure | automatic.
                    val mode = intent.getStringExtra("mode") ?: "off"
                    val chrome = "com.android.chrome"
                    val b = android.os.Bundle()
                    b.putString("DnsOverHttpsMode", mode)
                    if (mode == "secure") b.putString("DnsOverHttpsTemplates", "https://dns.google/dns-query")
                    dpm.setApplicationRestrictions(admin, chrome, b)
                    "chrome restrictions set mode=$mode readback=${dpm.getApplicationRestrictions(admin, chrome)}"
                }
                "chrome_clear" -> {
                    dpm.setApplicationRestrictions(admin, "com.android.chrome", android.os.Bundle())
                    "chrome restrictions cleared"
                }
                "sticky" -> {
                    // START_STICKY (true) or START_NOT_STICKY (false) for the next service start. Read by :vpn.
                    val on = intent.getBooleanExtra("on", true)
                    context.getSharedPreferences(Mech.PREFS, Context.MODE_PRIVATE).edit().putBoolean("sticky", on).commit()
                    "sticky=$on"
                }
                "watch_start" -> {
                    val mech = intent.getStringExtra("mech") ?: Mech.NONE
                    context.getSharedPreferences(Mech.PREFS, Context.MODE_PRIVATE).edit().putString("mech", mech).commit()
                    if (mech == Mech.ALWAYS) startAlwaysRecall(context)
                    if (mech == Mech.ALARM) armAlarm(context)
                    if (mech == Mech.JOB) scheduleJob(context)
                    if (mech == Mech.SVC || mech == Mech.ALWAYS) {
                        context.startForegroundService(Intent(context, WatchService::class.java).putExtra("mech", mech))
                    }
                    "watch started mech=$mech wall=${System.currentTimeMillis()}"
                }
                "watch_stop" -> {
                    stopAllWatchers(context)
                    "watch stopped wall=${System.currentTimeMillis()}"
                }
                else -> "unknown op $op"
            }
        } catch (e: Exception) {
            "EXC ${e.javaClass.name}: ${e.message}"
        }
        Log.i(TAG, "op=$op -> $result")
        resultData = result
    }

    companion object {
        const val TAG = "A021"
    }
}
