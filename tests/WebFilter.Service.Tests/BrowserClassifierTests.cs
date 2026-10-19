using WebFilter.Service.Guard;

namespace WebFilter.Service.Tests;

/// <summary>ProcessGuard's browser test against the Edge installed on this machine and copies of it.</summary>
public sealed class BrowserClassifierTests : IDisposable
{
    private const string Edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private static readonly string[] Supported = [Edge, @"C:\Program Files\Google\Chrome\Application\chrome.exe"];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-guard-" + Guid.NewGuid().ToString("N"));

    public BrowserClassifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string CopyOfEdge(string name)
    {
        var path = Path.Combine(_dir, name);
        File.Copy(Edge, path);
        return path;
    }

    [Fact]
    public void The_installed_signed_edge_is_supported()
    {
        if (!File.Exists(Edge)) return;
        Assert.Equal("Microsoft Corporation", Authenticode.SignerOrganization(Edge));
        Assert.Equal(ProgramKind.SupportedBrowser, BrowserClassifier.Classify(Edge, Supported));
    }

    [Fact]
    public void Edge_copied_elsewhere_or_renamed_is_unsupported()
    {
        if (!File.Exists(Edge)) return;
        Assert.Equal(ProgramKind.UnsupportedBrowser, BrowserClassifier.Classify(CopyOfEdge("msedge.exe"), Supported));
        Assert.Equal(ProgramKind.UnsupportedBrowser, BrowserClassifier.Classify(CopyOfEdge("homework.exe"), Supported));
        Assert.Equal(ProgramKind.UnsupportedBrowser, BrowserClassifier.Classify(Edge, [@"C:\Program Files\Google\Chrome\Application\chrome.exe"]));
    }

    [Fact]
    public void A_tampered_copy_has_no_valid_signature()
    {
        if (!File.Exists(Edge)) return;
        var path = CopyOfEdge("msedge.exe");
        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 4096;
            var b = stream.ReadByte();
            stream.Position = 4096;
            stream.WriteByte((byte)(b ^ 0xFF));
        }
        Assert.Null(Authenticode.SignerOrganization(path));
    }

    [Fact]
    public void Other_browsers_by_file_name_are_unsupported_and_ordinary_programs_are_not_browsers()
    {
        var fake = Path.Combine(_dir, "firefox.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), fake);
        Assert.Equal(ProgramKind.UnsupportedBrowser, BrowserClassifier.Classify(fake, Supported));
        Assert.Equal(ProgramKind.NotABrowser, BrowserClassifier.Classify(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Supported));
        Assert.Null(Authenticode.SignerOrganization(Environment.ProcessPath!.Replace("testhost", "missing", StringComparison.Ordinal)));
    }
}
