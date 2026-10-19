using WebFilter.Admin.ViewModels;

namespace WebFilter.Admin.Tests;

public sealed class RulePatternValidatorTests
{
    [Theory]
    [InlineData("example.com", "domain", "example.com")]
    [InlineData("  Example.COM.  ", "domain", "example.com")]
    [InlineData("https://www.example.com/", "domain", "www.example.com")]
    [InlineData("http://example.com", "domain", "example.com")]
    [InlineData("192.168.1.10", "domain", "192.168.1.10")]
    [InlineData("כאן.קום", "domain", "כאן.קום")]
    [InlineData("*.example.com", "wildcard", "*.example.com")]
    [InlineData("*.Sub.Example.com", "wildcard", "*.sub.example.com")]
    [InlineData("example.com/r/learn", "prefix", "example.com/r/learn")]
    [InlineData("HTTPS://Example.com/Path/", "prefix", "example.com/Path/")]
    [InlineData(@"^https?://([a-z]+\.)?example\.com/", "regex", @"^https?://([a-z]+\.)?example\.com/")]
    public void Accepts_and_normalizes_valid_patterns(string input, string type, string expected)
    {
        Assert.True(RulePatternValidator.TryNormalize(input, type, out var pattern, out var error), error);
        Assert.Equal(expected, pattern);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null, "domain")]
    [InlineData("exa mple.com", "domain")]
    [InlineData("localhost:8080", "domain")]
    [InlineData("example", "domain")]
    [InlineData("https://example.com/page?x=1", "domain")]
    [InlineData("*.1.2.3.4", "wildcard")]
    [InlineData("*.example.com/path", "wildcard")]
    [InlineData("/path-only", "prefix")]
    [InlineData("*.example.com/path", "prefix")]
    [InlineData(@"(a)\1", "regex")]
    public void Rejects_invalid_patterns(string? input, string type)
    {
        Assert.False(RulePatternValidator.TryNormalize(input, type, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Unknown_types_are_a_programming_error() =>
        Assert.Throws<ArgumentException>(() => RulePatternValidator.TryNormalize("example.com", "glob", out _, out _));
}
