package com.webfilter.spike.a02.dns

/** Hard-coded rules for the spike. These are not the real Decider. */
object Rules {
    /** Two test names (.example, .test) and one real domain. A name matches a rule when it equals it or ends with ".rule". */
    val BLOCKED = listOf("blocked.example", "blocked.test", "wikipedia.org")

    fun isBlocked(name: String): Boolean {
        val n = name.trimEnd('.')
        return BLOCKED.any { n == it || n.endsWith(".$it") }
    }
}
