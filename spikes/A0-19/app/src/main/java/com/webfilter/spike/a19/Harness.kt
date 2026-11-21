package com.webfilter.spike.a19

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.os.Process
import android.os.UserHandle

/**
 * The operations the spike needs. Each returns one line of text, which goes to logcat and to the broadcast result.
 * Only public API is used. UserHandle.of and getIdentifier are hidden on API 36, so the id comes from toString()
 * ("UserHandle{10}"), and the handle is rebuilt with getUserHandleForUid(id * 100000), as in A0.1.
 */
object Harness {
    private const val PREFS = "a19"
    private const val KEY_IDS = "created_user_ids"

    fun admin(ctx: Context) = ComponentName(ctx, AdminReceiver::class.java)

    fun dpm(ctx: Context): DevicePolicyManager =
        ctx.getSystemService(DevicePolicyManager::class.java)

    /** Status of THIS process's user: our admin active, and whether this app is device owner or profile owner here. */
    fun status(ctx: Context): String {
        val d = dpm(ctx)
        val pkg = ctx.packageName
        return "myUser=${Process.myUserHandle()} pkg=$pkg " +
            "isAdminActive=${d.isAdminActive(admin(ctx))} " +
            "isDeviceOwnerApp=${d.isDeviceOwnerApp(pkg)} " +
            "isProfileOwnerApp=${d.isProfileOwnerApp(pkg)}"
    }

    /** createAndManageUser: our app becomes the profile owner of the new user. Only run from user 0 (device owner). */
    fun create(ctx: Context, name: String): String {
        val d = dpm(ctx)
        val a = admin(ctx)
        // LEAVE_ALL_SYSTEM_APPS_ENABLED keeps GMS, GSF, Play Store and Chrome in the child user (see A0.1: Chrome was missing otherwise).
        val flags = DevicePolicyManager.SKIP_SETUP_WIZARD or DevicePolicyManager.LEAVE_ALL_SYSTEM_APPS_ENABLED
        val handle = d.createAndManageUser(a, name, a, null, flags)
            ?: return "FAIL createAndManageUser returned null"
        val id = idOf(handle) ?: return "FAIL unparsed handle=$handle"
        remember(ctx, id)
        return "OK created name=$name id=$id handle=$handle"
    }

    fun start(ctx: Context, id: Int): String {
        val r = dpm(ctx).startUserInBackground(admin(ctx), handleFor(id))
        return "startUserInBackground id=$id result=$r"
    }

    fun switchTo(ctx: Context, id: Int): String {
        val r = dpm(ctx).switchUser(admin(ctx), handleFor(id))
        return "switchUser id=$id result=$r"
    }

    fun stop(ctx: Context, id: Int): String {
        val r = dpm(ctx).stopUser(admin(ctx), handleFor(id))
        return "stopUser id=$id result=$r"
    }

    fun remove(ctx: Context, id: Int): String {
        val r = dpm(ctx).removeUser(admin(ctx), handleFor(id))
        if (r) forget(ctx, id)
        return "removeUser id=$id result=$r"
    }

    /** Ids this app created, kept here because getUserProfiles() does not list secondary users. */
    fun created(ctx: Context): String = "created_ids=" + ids(ctx).joinToString(",")

    fun handleFor(id: Int): UserHandle = UserHandle.getUserHandleForUid(id * 100000)

    fun idOf(handle: UserHandle): Int? =
        Regex("""\{(\d+)\}""").find(handle.toString())?.groupValues?.get(1)?.toIntOrNull()

    private fun prefs(ctx: Context) = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private fun ids(ctx: Context): Set<String> = prefs(ctx).getStringSet(KEY_IDS, emptySet()) ?: emptySet()

    private fun remember(ctx: Context, id: Int) {
        prefs(ctx).edit().putStringSet(KEY_IDS, ids(ctx) + id.toString()).apply()
    }

    private fun forget(ctx: Context, id: Int) {
        prefs(ctx).edit().putStringSet(KEY_IDS, ids(ctx) - id.toString()).apply()
    }
}
