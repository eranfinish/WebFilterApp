package webfilter.urls

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import kotlin.test.Test
import kotlin.test.assertTrue

/**
 * Differential test: every record in corpus/url-corpus.json was produced by the C# UrlNormalizer
 * (spikes/A0-7/corpus-gen). The Kotlin port must reproduce every field of every record.
 */
class CorpusTest {

    @Test
    fun matchesTheCSharpReference() {
        // Gradle runs tests with the project directory as the working directory.
        val corpus = File("../corpus/url-corpus.json")
        assertTrue(corpus.exists(), "missing corpus: ${corpus.absolutePath}")
        val records = JSONArray(corpus.readText(Charsets.UTF_8))

        val normalizer = UrlNormalizer(Icu4jIdnMapper())
        val mismatches = mutableListOf<String>()
        for (i in 0 until records.length()) {
            mismatches += compare(records.getJSONObject(i), normalizer)
        }

        assertTrue(
            mismatches.isEmpty(),
            "${mismatches.size} mismatching fields out of ${records.length()} records; first 80:\n" +
                mismatches.take(80).joinToString("\n"),
        )
    }

    private fun compare(record: JSONObject, normalizer: UrlNormalizer): List<String> {
        val input = record.getString("input")
        val label = JSONObject.quote(input)
        val problems = mutableListOf<String>()

        val expectedWeb = record.getBoolean("webUrl")
        val actualWeb = normalizer.isWebUrl(input)
        if (expectedWeb != actualWeb) problems += "$label webUrl: expected $expectedWeb, got $actualWeb"

        val expectedUrl = if (record.isNull("url")) null else record.getJSONObject("url")
        val actualUrl = normalizer.tryNormalize(input)
        if (expectedUrl == null) {
            if (actualUrl != null) problems += "$label url: expected null, got ${actualUrl.href}"
        } else if (actualUrl == null) {
            problems += "$label url: expected ${expectedUrl.getString("href")}, got null"
        } else {
            fun check(field: String, expected: Any?, actual: Any?) {
                if (expected != actual) problems += "$label url.$field: expected $expected, got $actual"
            }
            check("scheme", expectedUrl.getString("scheme"), actualUrl.scheme)
            check("host", expectedUrl.getString("host"), actualUrl.host)
            check("kind", expectedUrl.getString("kind"), actualUrl.hostKind.name)
            check("port", if (expectedUrl.isNull("port")) null else expectedUrl.getInt("port"), actualUrl.port)
            check("path", expectedUrl.getString("path"), actualUrl.path)
            check("query", expectedUrl.getString("query"), actualUrl.query)
            check("href", expectedUrl.getString("href"), actualUrl.href)
        }

        val expectedHost = if (record.isNull("hostOnly")) null else record.getJSONObject("hostOnly")
        val actualHost = normalizer.tryNormalizeHost(input)
        if (expectedHost == null) {
            if (actualHost != null) problems += "$label hostOnly: expected null, got ${actualHost.host}"
        } else if (actualHost == null) {
            problems += "$label hostOnly: expected ${expectedHost.getString("host")}, got null"
        } else {
            if (expectedHost.getString("host") != actualHost.host) {
                problems += "$label hostOnly.host: expected ${expectedHost.getString("host")}, got ${actualHost.host}"
            }
            if (expectedHost.getString("kind") != actualHost.kind.name) {
                problems += "$label hostOnly.kind: expected ${expectedHost.getString("kind")}, got ${actualHost.kind.name}"
            }
        }
        return problems
    }
}
