package com.webfilter.spike.a01

/**
 * Commands the test harness sends with `adb shell am broadcast -n .../.CmdReceiver --es op <wire>`.
 * Pure Kotlin (no Android types) so the parsing is covered by JVM unit tests.
 */
sealed class Op(val wire: String) {
    object Status : Op("status")
    object Create : Op("create")
    object ListUsers : Op("list")
    object Stop : Op("stop")
    object RemoveCreated : Op("remove")
    object Restrict : Op("restrict")
    object Unrestrict : Op("unrestrict")
    object Hide : Op("hide")
    object Unhide : Op("unhide")
    object IsHidden : Op("ishidden")
    object Restrictions : Op("restrictions")

    companion object {
        val ALL: List<Op> = listOf(
            Status, Create, ListUsers, Stop, RemoveCreated,
            Restrict, Unrestrict, Hide, Unhide, IsHidden, Restrictions,
        )

        fun parse(raw: String?): Op? = ALL.firstOrNull { it.wire == raw }
    }
}

object SpikeNames {
    const val USER_NAME_PREFIX = "spike-kid"

    /** Preinstalled browser on the emulator image (plan item 17). */
    const val DEFAULT_PACKAGE = "com.android.chrome"

    fun newUserName(nowMillis: Long): String = "$USER_NAME_PREFIX-$nowMillis"

    fun isSpikeUser(name: String?): Boolean = name != null && name.startsWith(USER_NAME_PREFIX)
}
