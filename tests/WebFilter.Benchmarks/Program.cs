using System.Diagnostics;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;

// Decision latency for a large policy (MVP-PLAN §2.4: p99 < 2 ms with 10,000 rules).
// Run: dotnet run -c Release --project tests/WebFilter.Benchmarks
const int Warmup = 5_000, Measured = 100_000;
const double TargetP99Ms = 2.0;

var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
var config = new FamilyConfig
{
    TimeZone = zone,
    Members = [new FamilyMember("kid", "Kid", Role.Child, 10, FilterMode.Blocklist, YouTubeMode.Restricted)],
    Devices = [new Device("pc", "windows", "agent", [new DeviceAccount("S-1-5-21-1", "kid", "kid", false)])],
};
for (var i = 0; i < 7_000; i++) config.Rules.Add(new Rule($"g{i}", null, $"site{i}.example", PatternType.Domain, RuleAction.Block));
for (var i = 0; i < 1_500; i++) config.Rules.Add(new Rule($"m{i}", "kid", $"ok{i}.example", PatternType.Domain, RuleAction.Allow));
for (var i = 0; i < 1_000; i++) config.Rules.Add(new Rule($"w{i}", null, $"*.w{i}.example", PatternType.Wildcard, RuleAction.Block));
for (var i = 0; i < 400; i++) config.Rules.Add(new Rule($"p{i}", null, $"p{i}.example/path{i}/", PatternType.Prefix, RuleAction.Block));
for (var i = 0; i < 100; i++) config.Rules.Add(new Rule($"r{i}", null, $"^https?://([a-z0-9-]+\\.)*rx{i}[a-z]*\\.example/", PatternType.Regex, RuleAction.Block));
config.Categories["big"] = Enumerable.Range(0, 100_000).Select(i => $"c{i}.example").ToList();
config.MemberCategories.Add(new MemberCategory("kid", "big", RuleAction.Block));

var hub = new HubCore(config);
var agent = new AgentCore("pc");
agent.Apply(hub.Snapshot());
var account = agent.AccountOf("kid");
var now = DateTimeOffset.Parse("2026-11-01T16:00:00+02:00");

var random = new Random(42);
string RandomUrl() => random.Next(8) switch
{
    0 => $"https://www.site{random.Next(7_000)}.example/page",
    1 => $"https://ok{random.Next(1_500)}.example/",
    2 => $"https://a.w{random.Next(1_000)}.example/",
    3 => $"https://p{random.Next(400)}.example/path{random.Next(400)}/x",
    4 => $"https://www.rx{random.Next(100)}abc.example/",
    5 => $"https://c{random.Next(100_000)}.example/",
    6 => $"http://192.168.{random.Next(255)}.{random.Next(255)}/",
    _ => $"https://unlisted{random.Next(1_000_000)}.example/a/b?q=1",
};
var urls = Enumerable.Range(0, 20_000).Select(_ => RandomUrl()).ToArray();

var cold = Stopwatch.StartNew();
agent.DecideUrl(account, urls[0], now);
cold.Stop();

for (var i = 0; i < Warmup; i++) agent.DecideUrl(account, urls[i % urls.Length], now);

var samples = new double[Measured];
for (var i = 0; i < Measured; i++)
{
    var start = Stopwatch.GetTimestamp();
    agent.DecideUrl(account, urls[i % urls.Length], now);
    samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}
Array.Sort(samples);
double P(double q) => samples[(int)Math.Min(samples.Length - 1, Math.Ceiling(q * samples.Length) - 1)];

Console.WriteLine($"rules: {config.Rules.Count:N0} + {config.Categories["big"].Count:N0} category domains");
Console.WriteLine($"first decision (builds the index): {cold.Elapsed.TotalMilliseconds:F1} ms");
Console.WriteLine($"decisions measured: {Measured:N0}");
Console.WriteLine($"p50 {P(0.50) * 1000:F1} µs · p95 {P(0.95) * 1000:F1} µs · p99 {P(0.99) * 1000:F1} µs · max {samples[^1]:F2} ms");
var pass = P(0.99) < TargetP99Ms;
Console.WriteLine(pass ? $"PASS: p99 < {TargetP99Ms} ms" : $"FAIL: p99 ≥ {TargetP99Ms} ms");
return pass ? 0 : 1;
