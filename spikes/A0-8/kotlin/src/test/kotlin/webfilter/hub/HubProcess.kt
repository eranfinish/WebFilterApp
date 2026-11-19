package webfilter.hub

import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.File
import java.nio.file.Files
import java.util.concurrent.TimeUnit

/**
 * The real hub (FamilyService + HubServer, as HubSyncTests runs it) in its own process, started from spikes/A0-8/server.
 * Each test starts its own hub, so each has its own CA. Build the harness first:
 *   dotnet build spikes/A0-8/server/A0_8Hub.csproj
 */
class HubProcess private constructor(
    private val process: Process,
    private val reader: BufferedReader,
    private val writer: BufferedWriter,
    val port: Int,
    var code: String,
) : AutoCloseable {
    val address: String get() = "127.0.0.1:$port"

    /** Sends one command line and returns the harness's answer line. */
    fun command(line: String): String {
        writer.write(line)
        writer.newLine()
        writer.flush()
        return reader.readLine() ?: error("the hub harness closed its output after '$line'")
    }

    /** A new pairing code. The previous one is dropped, as HubPairing.Create does. */
    fun newCode(): String {
        val answer = command("newcode")
        check(answer.startsWith("CODE ")) { "unexpected answer to newcode: $answer" }
        code = answer.removePrefix("CODE ")
        return code
    }

    override fun close() {
        try {
            writer.write("quit")
            writer.newLine()
            writer.flush()
        } catch (_: Exception) {
            // already gone
        }
        if (!process.waitFor(15, TimeUnit.SECONDS)) process.destroyForcibly()
    }

    companion object {
        fun start(): HubProcess {
            val projectDir = File(System.getProperty("user.dir"))
            val dll = File(projectDir, "../server/bin/Debug/net10.0-windows/A0_8Hub.dll").canonicalFile
            val fixture = File(projectDir, "../../../tests/simulation/fixtures/family-base.json").canonicalFile
            check(dll.exists()) { "harness not built: ${dll.path}" }
            check(fixture.exists()) { "family fixture not found: ${fixture.path}" }

            val dataDir = Files.createTempDirectory("a0-8-hub-").toFile()
            val dotnet = System.getenv("A0_8_DOTNET") ?: "dotnet"
            val process = ProcessBuilder(dotnet, dll.path, "serve", "0", dataDir.path, fixture.path)
                .redirectError(File(dataDir, "harness-stderr.log"))
                .start()
            val reader = process.inputStream.bufferedReader()
            val writer = process.outputStream.bufferedWriter()
            val ready = reader.readLine() ?: error("the harness exited before READY (see ${dataDir.path}\\harness-stderr.log)")
            val match = Regex("""READY port=(\d+) code=(\S+)""").matchEntire(ready) ?: error("unexpected first line: $ready")
            return HubProcess(process, reader, writer, match.groupValues[1].toInt(), match.groupValues[2])
        }
    }
}
