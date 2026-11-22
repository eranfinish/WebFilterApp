package com.webfilter.spike.a25

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.os.Process
import android.os.UserHandle

/**
 * Operations for spike A0.25. Each returns one line of text, which goes to logcat (tag A25) and to the broadcast result.
 * Only public API. UserHandle.of and getIdentifier are hidden on API 36, so the id comes from toString(),
 * and the handle is rebuilt with getUserHandleForUid(id * 100000), as in A0.1 and A0.19.
 */
object Harness {
    /** Affiliation id set on both the device owner (user 0) and the child's profile owner, so cross-user binding is allowed. */
    const val AFFILIATION_ID = "webfilter-a25-affiliation"

    fun admin(ctx: Context) = ComponentName(ctx, AdminReceiver::class.java)

    fun dpm(ctx: Context): DevicePolicyManager =
        ctx.getSystemService(DevicePolicyManager::class.java)

    /** Status of THIS process's user: our admin active, owner flags, affiliation. */
    fun status(ctx: Context): String {
        val d = dpm(ctx)
        val pkg = ctx.packageName
        return "myUser=${Process.myUserHandle()} pkg=$pkg " +
            "isAdminActive=${d.isAdminActive(admin(ctx))} " +
            "isDeviceOwnerApp=${d.isDeviceOwnerApp(pkg)} " +
            "isProfileOwnerApp=${d.isProfileOwnerApp(pkg)} " +
            "isAffiliatedUser=${d.isAffiliatedUser()}"
    }

    /** createAndManageUser: our app becomes the profile owner of the new user. Run from user 0 (device owner). */
    fun create(ctx: Context, name: String): String {
        val d = dpm(ctx)
        val a = admin(ctx)
        // LEAVE_ALL_SYSTEM_APPS_ENABLED keeps GMS, GSF, Play Store and Chrome in the child user (A0.1 / A0.19).
        val flags = DevicePolicyManager.SKIP_SETUP_WIZARD or DevicePolicyManager.LEAVE_ALL_SYSTEM_APPS_ENABLED
        val handle = d.createAndManageUser(a, name, a, null, flags)
            ?: return "FAIL createAndManageUser returned null"
        val id = idOf(handle) ?: return "FAIL unparsed handle=$handle"
        return "OK created id=$id handle=$handle"
    }

    /** Sets the affiliation id for the user this process runs in. Run once in user 0 and once in the child. */
    fun affiliate(ctx: Context): String {
        val d = dpm(ctx)
        d.setAffiliationIds(admin(ctx), setOf(AFFILIATION_ID))
        return "setAffiliationIds user=${Process.myUserHandle()} isAffiliatedUser=${d.isAffiliatedUser()}"
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

    fun handleFor(id: Int): UserHandle = UserHandle.getUserHandleForUid(id * 100000)

    fun idOf(handle: UserHandle): Int? =
        Regex("""\{(\d+)\}""").find(handle.toString())?.groupValues?.get(1)?.toIntOrNull()
}
