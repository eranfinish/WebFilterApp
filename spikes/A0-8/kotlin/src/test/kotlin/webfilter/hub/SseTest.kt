package webfilter.hub

import okhttp3.Request
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Criterion 2: a change on the hub reaches the agent's open /v1/changes stream within five seconds.
 * The hub pushes "event: version" on every settings change (HubServer.cs, /v1/changes).
 */
class SseTest {
    @Test
    fun hubChangeArrivesOnTheOpenStreamWithinFiveSeconds() {
        HubProcess.start().use { hub ->
            val paired = pairDevice(hub.address, hub.code)
            val client = HubHttp.client(paired.ca, paired.identity)
            val request = Request.Builder().url("https://${hub.address}/v1/changes").header("Accept", "text/event-stream").build()

            client.newCall(request).execute().use { response ->
                assertEquals(200, response.code)
                assertTrue(response.header("Content-Type").orEmpty().startsWith("text/event-stream"))
                val lines = LinkedBlockingQueue<String>()
                val source = response.body!!.source()
                thread(isDaemon = true, name = "sse-reader") {
                    while (true) lines.put(source.readUtf8Line() ?: break)
                }

                assertEquals(": connected", lines.poll(10, TimeUnit.SECONDS), "the stream must open before the change")

                val started = System.nanoTime()
                val answer = hub.command("push")
                val version = answer.substringAfter("version=").toInt()

                val deadline = started + TimeUnit.SECONDS.toNanos(5)
                var event: String? = null
                var data: String? = null
                while (System.nanoTime() < deadline && data == null) {
                    val line = lines.poll(deadline - System.nanoTime(), TimeUnit.NANOSECONDS) ?: break
                    if (line == "event: version") event = line
                    if (event != null && line.startsWith("data: ")) data = line
                }
                val elapsedMs = (System.nanoTime() - started) / 1_000_000
                println("SSE: push answered '$answer', event received after $elapsedMs ms")

                assertEquals("event: version", event, "no 'version' event within 5 s")
                assertEquals("data: {\"version\":$version}", data)
                assertTrue(elapsedMs < 5_000)
            }
        }
    }
}
