namespace WebFilter.Admin.Services;

public sealed record AdminClientOptions
{
    /// <summary>Matches the service's <c>ServiceOptions.AdminPipeName</c> default.</summary>
    public const string DefaultPipeName = "WebFilter.Admin";

    public string PipeName { get; init; } = DefaultPipeName;

    /// <summary>How long to wait for the pipe to accept a connection before reporting "service not running".</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for a reply before giving up and dropping the connection.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
