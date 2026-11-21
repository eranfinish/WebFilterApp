package com.webfilter.spike.a03b

import android.app.admin.DevicePolicyManager
import android.content.Context
import android.content.RestrictionsManager
import android.os.Bundle
import org.json.JSONArray

/**
 * Sets Chrome managed policies as Device Owner through DevicePolicyManager.setApplicationRestrictions.
 * t0 values are wall-clock milliseconds (System.currentTimeMillis) so host-side probe times can be compared.
 */
class Harness(private val context: Context) {

    private val dpm = context.getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager
    private val admin = AdminReceiver.component(context)

    fun run(a: Map<String, String?>): String = try {
        dispatch(a)
    } catch (e: Throwable) {
        "EXC ${e.javaClass.name}: ${e.message}"
    }

    private fun dispatch(a: Map<String, String?>): String = when (a["op"]) {
        "status" -> "owner=${dpm.isDeviceOwnerApp(context.packageName)} admin=${dpm.isAdminActive(admin)} t=${System.currentTimeMillis()}"
        "chrome_set" -> chromeSet(a["mode"] ?: "array", a["blocklist"], a["allowlist"])
        "chrome_clear" -> {
            val t0 = System.currentTimeMillis()
            dpm.setApplicationRestrictions(admin, CHROME, Bundle())
            "CLEARED t0=$t0"
        }
        "chrome_read" -> "restrictions=" + bundleToString(dpm.getApplicationRestrictions(admin, CHROME))
        "manifest" -> manifestDump()
        else -> "ERR unknown op '${a["op"]}'"
    }

    private fun chromeSet(mode: String, block: String?, allow: String?): String {
        val bl = split(block)
        val al = split(allow)
        val b = Bundle()
        if (mode == "array") {
            b.putStringArray("URLBlocklist", bl.toTypedArray())
            if (al.isNotEmpty()) b.putStringArray("URLAllowlist", al.toTypedArray())
        } else {
            b.putString("URLBlocklist", JSONArray(bl).toString())
            if (al.isNotEmpty()) b.putString("URLAllowlist", JSONArray(al).toString())
        }
        val t0 = System.currentTimeMillis()
        dpm.setApplicationRestrictions(admin, CHROME, b)
        return "SET mode=$mode t0=$t0 blocklist=$bl allowlist=$al"
    }

    private fun manifestDump(): String {
        val rm = context.getSystemService(Context.RESTRICTIONS_SERVICE) as RestrictionsManager
        val entries = rm.getManifestRestrictions(CHROME) ?: return "manifest=null"
        return entries.joinToString(" | ") { "${it.key}:type=${it.type}" }.ifEmpty { "manifest=empty" }
    }

    private fun split(s: String?): List<String> =
        s?.split(",")?.map { it.trim() }?.filter { it.isNotEmpty() } ?: emptyList()

    private fun bundleToString(b: Bundle?): String {
        if (b == null) return "null"
        return b.keySet().joinToString(", ") { k ->
            val v = b.get(k)
            "$k=" + when (v) {
                is Array<*> -> v.joinToString(prefix = "[", postfix = "]")
                else -> v.toString()
            }
        }
    }

    companion object {
        const val CHROME = "com.android.chrome"
    }
}
