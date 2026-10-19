using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace WebFilter.Service.Tests;

/// <summary>
/// Windows restarts the installed service only when its process ends while the service is running. These tests run the real
/// host with a recording exit instead of <see cref="Environment.Exit(int)"/>; the service itself is checked in the lab.
/// </summary>
public sealed class FaultExitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly List<int> _exits = [];
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Exit(int code)
    {
        lock (_exits) _exits.Add(code);
        _exited.TrySetResult(code);
    }

    private IHost Build(bool windowsService, params BackgroundService[] services)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        foreach (var service in services) builder.Services.AddSingleton<IHostedService>(service);
        builder.Services.AddFaultExit(windowsService, Exit);
        return builder.Build();
    }

    [Fact]
    public async Task As_a_service_a_failed_background_service_ends_the_process_without_stopping_the_host()
    {
        var failing = new Failing();
        var idle = new Idle();
        using var host = Build(windowsService: true, failing, idle);
        await host.StartAsync();

        failing.Fail(new InvalidOperationException("boom"));

        Assert.Equal(FaultExit.ExitCode, await _exited.Task.WaitAsync(Wait));
        // No clean stop began, so WindowsServiceLifetime never reports SERVICE_STOPPED before the process ends.
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.False(idle.ExecuteTask!.IsCompleted);
        await host.StopAsync();
        Assert.Equal(FaultExit.ExitCode, Assert.Single(_exits));
    }

    [Fact]
    public async Task As_a_service_a_background_service_that_fails_while_the_host_starts_also_ends_the_process()
    {
        using var host = Build(windowsService: true, new FailsAtOnce(), new Idle());
        await host.StartAsync();

        Assert.Equal(FaultExit.ExitCode, await _exited.Task.WaitAsync(Wait));
        await host.StopAsync();
    }

    [Fact]
    public async Task As_a_service_a_background_service_canceled_by_its_own_token_counts_as_a_failure()
    {
        // The host treats this as a failure too (only a cancellation during a stop is not one).
        var failing = new Failing();
        using var host = Build(windowsService: true, failing);
        await host.StartAsync();

        failing.Fail(new OperationCanceledException(new CancellationToken(canceled: true)));

        Assert.Equal(FaultExit.ExitCode, await _exited.Task.WaitAsync(Wait));
        await host.StopAsync();
    }

    [Fact]
    public async Task As_a_service_a_requested_stop_stays_clean_even_when_a_service_throws_while_stopping()
    {
        var throwsOnStop = new ThrowsOnStop();
        var idle = new Idle();
        using var host = Build(windowsService: true, throwsOnStop, idle);
        await host.StartAsync();

        await host.StopAsync();

        Assert.True(throwsOnStop.ExecuteTask!.IsFaulted);
        Assert.True(idle.ExecuteTask!.IsCanceled);
        await Task.Delay(200);
        Assert.Empty(_exits);
    }

    [Fact]
    public async Task Outside_a_service_a_failed_background_service_stops_the_host_as_before()
    {
        var failing = new Failing();
        using var host = Build(windowsService: false, failing, new Idle());
        Assert.Equal(BackgroundServiceExceptionBehavior.StopHost, host.Services.GetRequiredService<IOptions<HostOptions>>().Value.BackgroundServiceExceptionBehavior);
        var run = host.RunAsync();

        failing.Fail(new InvalidOperationException("boom"));

        await run.WaitAsync(Wait);
        Assert.Empty(_exits);
    }

    [Fact]
    public void The_registration_the_service_uses_changes_nothing_outside_a_Windows_service()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var before = builder.Services.Count;

        builder.Services.AddFaultExit();

        Assert.Equal(before, builder.Services.Count);
        using var host = builder.Build();
        Assert.Equal(BackgroundServiceExceptionBehavior.StopHost, host.Services.GetRequiredService<IOptions<HostOptions>>().Value.BackgroundServiceExceptionBehavior);
    }

    private sealed class Failing : BackgroundService
    {
        private readonly TaskCompletionSource _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail(Exception ex) => _failure.SetException(ex);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) => await _failure.Task.WaitAsync(stoppingToken);
    }

    private sealed class FailsAtOnce : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => throw new IOException("All pipe instances are busy.");
    }

    private sealed class Idle : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private sealed class ThrowsOnStop : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { throw new ObjectDisposedException("pipe"); }
        }
    }
}
