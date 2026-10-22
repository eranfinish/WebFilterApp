using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WebFilter.Service.Network;

namespace WebFilter.Service.Tests;

/// <summary>
/// The C# side of <c>WebFilter.Wfp.Native.dll</c>, without loading it: these tests never call into WFP (the dev PC must not
/// get filters). The DLL itself is exercised in the lab VM by tests/simulation/lab/Test-LabWfp.ps1.
/// </summary>
public sealed partial class NativeWfpEngineTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx"))) dir = dir.Parent ?? throw new InvalidOperationException("WebFilter.slnx not found");
        return dir.FullName;
    }

    private static string NativeDir => Path.Combine(RepoRoot(), "src", "WebFilter.Wfp.Native");

    [GeneratedRegex(@"WF_API\s+(\w+)\s*\(([^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex Export();

    [Fact]
    public void Every_import_matches_an_export_of_the_native_header()
    {
        var header = File.ReadAllText(Path.Combine(NativeDir, "WfpApi.h"));
        var exports = Export().Matches(header).ToDictionary(
            m => m.Groups[1].Value,
            m => m.Groups[2].Value.Trim() is "void" or "" ? 0 : m.Groups[2].Value.Split(',').Length);

        var imports = typeof(NativeWfpEngine).GetNestedType("Native", BindingFlags.NonPublic)!
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() is not null)
            .ToList();

        Assert.Equal(exports.Keys.Order(), imports.Select(m => m.Name).Order());
        foreach (var import in imports)
        {
            var attribute = import.GetCustomAttribute<DllImportAttribute>()!;
            Assert.Equal(NativeWfpEngine.LibraryName, attribute.Value);
            Assert.True(attribute.ExactSpelling);
            Assert.Equal(typeof(uint), import.ReturnType); // DWORD
            Assert.Equal(exports[import.Name], import.GetParameters().Length);
            Assert.NotNull(import.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()); // only from the service's own folder
        }
    }

    [Fact]
    public void The_native_project_builds_the_library_the_service_loads()
    {
        var project = XDocument.Load(Path.Combine(NativeDir, "WebFilter.Wfp.Native.vcxproj"));
        XNamespace ms = "http://schemas.microsoft.com/developer/msbuild/2003";
        var targetName = project.Descendants(ms + "TargetName").Single().Value;
        Assert.Equal(NativeWfpEngine.LibraryName, targetName + ".dll");
        Assert.Equal("DynamicLibrary", project.Descendants(ms + "ConfigurationType").Single().Value);
        // A static CRT: the target PC has no Visual C++ redistributable.
        Assert.All(project.Descendants(ms + "RuntimeLibrary"), r => Assert.DoesNotContain("DLL", r.Value, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\0", new string[0])]
    [InlineData("S-1-5-21-1-1001\0\0", new[] { "S-1-5-21-1-1001" })]
    [InlineData("S-1-5-21-1-1001\0S-1-5-21-1-1002\0\0garbage", new[] { "S-1-5-21-1-1001", "S-1-5-21-1-1002" })]
    [InlineData("S-1-5-21-1-1001", new[] { "S-1-5-21-1-1001" })]
    [InlineData("", new string[0])]
    public void The_account_list_is_read_up_to_the_empty_entry(string buffer, string[] expected) =>
        Assert.Equal(expected, NativeWfpEngine.ParseMultiString(buffer));
}
