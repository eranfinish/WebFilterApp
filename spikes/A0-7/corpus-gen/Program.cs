using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebFilter.Core.Urls;

// Spike A0.7. Writes spikes/A0-7/corpus/url-corpus.json: for every input string, the output of the C#
// UrlNormalizer (TryNormalize, TryNormalizeHost, IsWebUrl). The Kotlin port must reproduce every record.
//
// Inputs come from three places: every "url" in the simulation scenarios, every string literal in the
// UrlNormalizerTests InlineData lines, and a fixed list of edge cases written here.
//
// Usage: dotnet run -- <repo root>

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: corpus-gen <repo root>");
    return 2;
}

var repo = Path.GetFullPath(args[0]);
var inputs = new SortedSet<string>(StringComparer.Ordinal);

// 1. "url" values in the scenarios.
var scenarioRoot = Path.Combine(repo, "tests", "simulation", "scenarios");
var urlField = new Regex("\"url\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
foreach (var file in Directory.EnumerateFiles(scenarioRoot, "*.json", SearchOption.AllDirectories))
{
    foreach (Match m in urlField.Matches(File.ReadAllText(file)))
        inputs.Add(JsonUnescape(m.Groups[1].Value));
}

// 2. String literals on the InlineData lines of the URL normalizer tests. The C# escapes used there
//    (\\, \", \n, \t) are the same as JSON escapes, so a JSON string parse gives the C# value.
var testFile = Path.Combine(repo, "tests", "WebFilter.Core.Tests", "Urls", "UrlNormalizerTests.cs");
var literal = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
foreach (var line in File.ReadLines(testFile))
{
    if (!line.TrimStart().StartsWith("[InlineData(", StringComparison.Ordinal))
        continue;
    foreach (Match m in literal.Matches(line))
        inputs.Add(JsonUnescape(m.Groups[1].Value));
}

// 3. Edge cases. Each one is a claim about a specific rule of the URL Standard or of .NET's IDN.
foreach (var e in EdgeCaseList.Items)
    inputs.Add(e);

var records = new List<object>(inputs.Count);
var urlOkCount = 0;
var hostOkCount = 0;
foreach (var input in inputs)
{
    var urlOk = UrlNormalizer.TryNormalize(input, out var url);
    var hostOk = UrlNormalizer.TryNormalizeHost(input, out var host, out var hostKind);
    if (urlOk) urlOkCount++;
    if (hostOk) hostOkCount++;
    records.Add(new
    {
        input,
        webUrl = UrlNormalizer.IsWebUrl(input),
        url = urlOk
            ? (object)new { scheme = url!.Scheme, host = url.Host, kind = url.HostKind.ToString(), port = url.Port, path = url.Path, query = url.Query, href = url.Href }
            : null,
        hostOnly = hostOk
            ? (object)new { host, kind = hostKind.ToString() }
            : null,
    });
}

var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "corpus");
outDir = Path.GetFullPath(outDir);
Directory.CreateDirectory(outDir);
var outFile = Path.Combine(outDir, "url-corpus.json");

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
File.WriteAllText(outFile, JsonSerializer.Serialize(records, options) + "\n");

Console.WriteLine($"inputs: {inputs.Count}");
Console.WriteLine($"url ok: {urlOkCount}");
Console.WriteLine($"host ok: {hostOkCount}");
Console.WriteLine($"written: {outFile}");
return 0;

static string JsonUnescape(string raw) =>
    JsonSerializer.Deserialize<string>("\"" + raw + "\"") ?? "";

static class EdgeCaseList
{
public static readonly string[] Items =
[
    // IDN: sharp s, final sigma, dotted forms, ZWJ/ZWNJ, fullwidth and ideographic full stops
    "https://straße.example/",
    "https://ς.example/",
    "https://ΓΑΣ.example/",
    "https://a‍b.example/",
    "https://a‌b.example/",
    "https://ｅｘａｍｐｌｅ。com/",
    "https://example。com/",
    "https://EXAMPLE.com。/",
    "https://Ⅰ.example/",
    "https://ﬁle.example/",
    "https://\U0001F600.example/",
    "https://a.\U0001F600.example/",
    "https://üüü.ü/",
    // percent-encoding: invalid UTF-8 in host and in path and query
    "https://%FF.example/",
    "https://%C3.example/",
    "https://example.com/%FF",
    "https://example.com/?q=%FF",
    "https://example.com/%zz",
    "https://ex%41mple.com/",
    // ports
    "https://example.com:443./",
    "https://example.com:0/",
    "https://example.com:65535/",
    "https://example.com:65536/",
    "https://example.com:/",
    // IPv4 boundaries and notations
    "http://0x100.1/",
    "http://4294967295/",
    "http://4294967296/",
    "http://1.2.3/",
    "http://1.2.3.4.",
    "http://01.02.03.04/",
    "http://0777777777777/",
    "http://1..2/",
    "http://0x7f000001/",
    "http://0xffffffffff/",
    "http://256/",
    "http://0.0.0.0/",
    // IPv6 with embedded IPv4 and zone ids
    "http://[::ffff:1.2.3.4]/",
    "http://[1::2.3.4.5]/",
    "http://[ffff::]/",
    "http://[1:2:3:4:5:6:1.2.3.4]/",
    "http://[::1]%25eth0/",
    "http://[::1%25eth0]/",
    // paths and whitespace
    "https://example.com/a?b#c d",
    "https://example.com/ /x",
    "https://example.com/a\\..\\b",
    "https://example.com/\u007f",
    "https://example.com/ x",
    "https://example.com/\u0000x",
    "https://ab.example/%E2%82%AC/€",
    "https://example.com/a/%2E/b",
    "https://example.com/a/%2e%2e",
    // non-web and malformed
    "HTTPS://İ.example/",
    "https://İstanbul.example/",
    "https:",
    "https:/",
    "http:",
    "1http://example.com/",
    "+https://example.com/",
    // host-only edge cases
    ".",
    "..",
    "a..b",
    "a.b.c.d.e.f.g.h",
    "xn--",
    "xn--ls8h.example",
    "ß.example",
    "ς",
    "Ⅰ",
    "ﬁ",
    "İ",
    "ＡＢ.Ｃ",
    "Ab.。CD",
    "[::1]:80",
    "[::1]x",
    "::ffff:1.2.3.4",
    // bidi (CHECK_BIDI): a Hebrew label, and LTR and RTL letters in one label
    "https://שלום.example/",
    "https://aש.example/",
    // hyphens in a non-ASCII label (hasErrors would also count these; the URL Standard does not)
    "https://ab--ü.example/",
    "https://-ü.example/",
    "https://ü-.example/",
    // lengths: a non-ASCII label over 63 characters, and an empty label
    "https://" + new string('ü', 70) + ".example/",
    "https://ü..example/",
];
}
