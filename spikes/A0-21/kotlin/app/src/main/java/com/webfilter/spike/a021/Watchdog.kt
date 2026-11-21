package com.webfilter.spike.a021

import android.app.AlarmManager
import android.app.ActivityManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.app.admin.DevicePolicyManager
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.IBinder
import android.util.Log
import com.webfilter.spike.a021.vpn.TunVpnService
import kotlin.concurrent.thread

/**
 * Restart mechanisms for the tunnel (spike A0.21). They run in the MAIN process (the DPC), never in :vpn.
 * mech = svc (bound foreground service, reacts to onServiceDisconnected) | alarm (exact alarm every 1 s) |
 *        job (JobScheduler one-shot chain, 1 s latency) | always (setAlwaysOnVpnPackage re-called every 1 s) | none.
 * The mechanism choice lives in SharedPreferences, so a restarted main process keeps it.
 */
object Mech {
    const val SVC = "svc"
    const val ALARM = "alarm"
    const val JOB = "job"
    const val ALWAYS = "always"
    const val NONE = "none"
    const val TAG = "A021W"
    const val PREFS = "a021"
}

fun prefs(ctx: Context) = ctx.getSharedPreferences(Mech.PREFS, Context.MODE_PRIVATE)

fun currentMech(ctx: Context): String = prefs(ctx).getString("mech", Mech.NONE) ?: Mech.NONE

/** True when a process whose name ends with ":vpn" of this app is alive (the app may list its own processes). */
fun vpnAlive(ctx: Context): Boolean {
    val am = ctx.getSystemService(ActivityManager::class.java)
    return am.runningAppProcesses.orEmpty().any { it.processName.endsWith(":vpn") }
}

fun startVpnFromMain(ctx: Context, why: String) {
    Log.i(Mech.TAG, "startService from main why=$why wall=${System.currentTimeMillis()}")
    ctx.startService(Intent(ctx, TunVpnService::class.java))
}

/** Foreground service in the main process. Keeps the DPC process alive for the svc mechanism. */
class WatchService : Service() {
    @Volatile private var bound = false

    private val conn = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
            Log.i(Mech.TAG, "svc connected to vpn wall=${System.currentTimeMillis()}")
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            Log.i(Mech.TAG, "svc vpn disconnected wall=${System.currentTimeMillis()}")
            startVpnFromMain(this@WatchService, "svc-disconnect")
            bindVpn()
        }
    }

    private fun bindVpn() {
        bound = bindService(Intent(this, com.webfilter.spike.a021.vpn.WatchTarget::class.java), conn, BIND_AUTO_CREATE)
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val mech = intent?.getStringExtra("mech") ?: currentMech(this)
        startForegroundCompat()
        Log.i(Mech.TAG, "WatchService start mech=$mech wall=${System.currentTimeMillis()}")
        if (mech == Mech.SVC && !bound) bindVpn()
        return START_STICKY
    }

    private fun startForegroundCompat() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel("a021-watch", "a021 watch", NotificationManager.IMPORTANCE_LOW))
        val n = Notification.Builder(this, "a021-watch")
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle("A0.21 watchdog")
            .build()
        startForeground(1, n)
    }

    override fun onDestroy() {
        if (bound) try { unbindService(conn) } catch (_: Exception) {}
        bound = false
        super.onDestroy()
    }
}

/** Alarm chain: each alarm checks :vpn and re-arms itself while mech == alarm. */
class WatchAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (currentMech(context) != Mech.ALARM) return
        if (!vpnAlive(context)) startVpnFromMain(context, "alarm")
        armAlarm(context)
    }
}

fun alarmPendingIntent(ctx: Context): PendingIntent =
    PendingIntent.getBroadcast(ctx, 0, Intent(ctx, WatchAlarmReceiver::class.java), PendingIntent.FLAG_IMMUTABLE)

fun armAlarm(ctx: Context) {
    val am = ctx.getSystemService(AlarmManager::class.java)
    val at = System.currentTimeMillis() + 1000
    try {
        am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, alarmPendingIntent(ctx))
    } catch (e: SecurityException) {
        // targetSdk 36 needs SCHEDULE_EXACT_ALARM for exact alarms (special access, not granted here): inexact fallback.
        Log.i(Mech.TAG, "exact alarm refused (${e.message?.take(60)}), inexact fallback wall=${System.currentTimeMillis()}")
        am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, alarmPendingIntent(ctx))
    }
}

/** Job chain: one-shot job with a 1 s minimum latency that re-schedules itself while mech == job. */
class WatchJob : JobService() {
    override fun onStartJob(params: JobParameters?): Boolean {
        if (currentMech(this) == Mech.JOB) {
            if (!vpnAlive(this)) startVpnFromMain(this, "job")
            scheduleJob(this)
        }
        return false
    }

    override fun onStopJob(params: JobParameters?): Boolean = false
}

fun scheduleJob(ctx: Context) {
    val js = ctx.getSystemService(JobScheduler::class.java)
    val info = JobInfo.Builder(21, ComponentName(ctx, WatchJob::class.java))
        .setMinimumLatency(1000)
        .setOverrideDeadline(2000)
        .build()
    js.schedule(info)
}

/** setAlwaysOnVpnPackage re-called every second (main process thread, while mech == always). */
fun startAlwaysRecall(ctx: Context) {
    thread(name = "always-recall", isDaemon = true) {
        val dpm = ctx.getSystemService(DevicePolicyManager::class.java)
        val admin = AdminReceiver.component(ctx)
        while (currentMech(ctx) == Mech.ALWAYS) {
            try {
                dpm.setAlwaysOnVpnPackage(admin, ctx.packageName, true)
            } catch (e: Exception) {
                Log.w(Mech.TAG, "always recall failed ${e.javaClass.simpleName}: ${e.message}")
            }
            Thread.sleep(1000)
        }
    }
}

/** After a cold boot, restart the stored watchdog mechanism (the mech choice is in SharedPreferences). */
class WatchBootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return
        val mech = currentMech(context)
        Log.i(Mech.TAG, "boot completed, stored mech=$mech wall=${System.currentTimeMillis()}")
        when (mech) {
            Mech.ALARM -> armAlarm(context)
            Mech.JOB -> scheduleJob(context)
            Mech.ALWAYS -> startAlwaysRecall(context)
        }
        if (mech == Mech.SVC || mech == Mech.ALWAYS) {
            context.startForegroundService(Intent(context, WatchService::class.java).putExtra("mech", mech))
        }
    }
}

fun stopAllWatchers(ctx: Context) {
    prefs(ctx).edit().putString("mech", Mech.NONE).commit()
    ctx.getSystemService(AlarmManager::class.java).cancel(alarmPendingIntent(ctx))
    ctx.getSystemService(JobScheduler::class.java).cancelAll()
    ctx.stopService(Intent(ctx, WatchService::class.java))
}
