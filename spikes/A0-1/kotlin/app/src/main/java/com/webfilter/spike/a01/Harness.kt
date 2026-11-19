package com.webfilter.spike.a01

import android.app.admin.DevicePolicyManager
import android.content.Context
import android.os.Process
import android.os.UserHandle
import android.os.UserManager

/**
 * Runs one [Op] against the framework and returns a single line for the caller.
 * Every failure is reported with its exception class and, for user operations, the UserOperationException code.
 */
class Harness(private val context: Context) {

    private val dpm = context.getSystemService(DevicePolicyManager::class.java)
    private val um = context.getSystemService(UserManager::class.java)
    private val admin = AdminReceiver.component(context)
    private val prefs = context.getSharedPreferences("a01", Context.MODE_PRIVATE)

    fun run(op: Op, pkg: String): String = try {
        when (op) {
            Op.Status -> status()
            Op.Create -> create()
            Op.ListUsers -> listUsers()
            Op.Stop -> stopCreated()
            Op.RemoveCreated -> removeCreated()
            Op.Restrict -> restrict(true)
            Op.Unrestrict -> restrict(false)
            Op.Hide -> hide(pkg, true)
            Op.Unhide -> hide(pkg, false)
            Op.IsHidden -> "isHidden($pkg)=${dpm.isApplicationHidden(admin, pkg)}"
            Op.Restrictions -> "userRestrictions=${um.userRestrictions}"
        }
    } catch (e: UserManager.UserOperationException) {
        "ERR UserOperationException code=${e.userOperationResult} msg=${e.message}"
    } catch (e: SecurityException) {
        "ERR SecurityException msg=${e.message}"
    } catch (e: Exception) {
        "ERR ${e.javaClass.name} msg=${e.message}"
    }

    private fun status(): String {
        val owner = dpm.isDeviceOwnerApp(context.packageName)
        val adminActive = dpm.isAdminActive(admin)
        return "owner=$owner admin=$adminActive myUser=${idOf(Process.myUserHandle())} " +
            "profiles=${um.userProfiles.map { idOf(it) }} created=${createdIds()}"
    }

    private fun create(): String {
        val name = SpikeNames.newUserName(System.currentTimeMillis())
        val handle = dpm.createAndManageUser(
            admin,
            name,
            admin,
            null,
            DevicePolicyManager.SKIP_SETUP_WIZARD,
        ) ?: return "ERR createAndManageUser returned null for name=$name"
        remember(idOf(handle))
        return "OK created name=$name id=${idOf(handle)} handle=$handle"
    }

    private fun listUsers(): String =
        "profiles=${um.userProfiles.map { idOf(it) }} created=${createdIds()} " +
            "userRestrictions=${um.userRestrictions}"

    private fun stopCreated(): String {
        val out = createdIds().map { id ->
            "id=$id stopUser=${dpm.stopUser(admin, handleOf(id))}"
        }
        return if (out.isEmpty()) "no created users recorded" else out.joinToString("; ")
    }

    private fun removeCreated(): String {
        val ids = createdIds()
        val out = ids.map { id ->
            "id=$id removeUser=${dpm.removeUser(admin, handleOf(id))}"
        }
        prefs.edit().remove(KEY_CREATED).commit()
        return if (out.isEmpty()) "no created users recorded" else out.joinToString("; ")
    }

    private fun restrict(on: Boolean): String {
        if (on) {
            dpm.addUserRestriction(admin, UserManager.DISALLOW_ADD_USER)
        } else {
            dpm.clearUserRestriction(admin, UserManager.DISALLOW_ADD_USER)
        }
        return "DISALLOW_ADD_USER=$on userRestrictions=${um.userRestrictions}"
    }

    private fun hide(pkg: String, hidden: Boolean): String {
        val ok = dpm.setApplicationHidden(admin, pkg, hidden)
        return "setApplicationHidden($pkg,$hidden)=$ok isHidden=${dpm.isApplicationHidden(admin, pkg)}"
    }

    /**
     * UserHandle.getIdentifier() is hidden in the public SDK; toString() is "UserHandle{<id>}" on every release so far.
     * Logged raw on each call, so the device output shows whether this parse was right.
     */
    private fun idOf(handle: UserHandle): Int =
        Regex("""\{(\d+)\}""").find(handle.toString())?.groupValues?.get(1)?.toInt() ?: -1

    /** UserHandle.of(int) is hidden; the per-user UID range (AID_USER_OFFSET) is 100000. */
    private fun handleOf(id: Int): UserHandle = UserHandle.getUserHandleForUid(id * USER_UID_RANGE)

    private fun createdIds(): List<Int> =
        (prefs.getStringSet(KEY_CREATED, emptySet()) ?: emptySet()).mapNotNull { it.toIntOrNull() }.sorted()

    private fun remember(id: Int) {
        val updated = createdIds().map { it.toString() }.toMutableSet().apply { add(id.toString()) }
        prefs.edit().putStringSet(KEY_CREATED, updated).commit()
    }

    private companion object {
        const val KEY_CREATED = "created_ids"
        const val USER_UID_RANGE = 100_000
    }
}
