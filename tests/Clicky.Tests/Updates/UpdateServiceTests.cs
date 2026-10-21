using Clicky.Core.Settings;
using Clicky.Core.Updates;
using Clicky.Tests.Memory;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Updates;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeBackend _backend = new();
    private readonly List<UpdateService> _services = [];

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task Development_build_never_checks()
    {
        _backend.IsInstalled = false;
        var service = Create();
        service.Start();
        _time.Advance(UpdateService.FirstCheckDelay);
        await service.CheckNowAsync();

        Assert.Equal(UpdatePhase.NotInstalled, service.Status.Phase);
        Assert.Equal(0, _backend.Checks);
        Assert.False(service.RestartToUpdate());
    }

    [Fact]
    public async Task Scheduled_check_downloads_and_waits_for_restart()
    {
        _backend.Latest = "0.2.0";
        var service = Create();
        var phases = Record(service);
        service.Start();

        _time.Advance(UpdateService.FirstCheckDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _backend.Checks);

        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => service.Status.Phase == UpdatePhase.ReadyToRestart);

        Assert.Equal("0.2.0", service.Status.AvailableVersion);
        Assert.Equal(100, service.Status.Progress);
        Assert.Equal<UpdatePhase>([UpdatePhase.Checking, UpdatePhase.Downloading, UpdatePhase.ReadyToRestart], phases.Distinct());
        Assert.False(_backend.Applied);

        Assert.True(service.RestartToUpdate());
        Assert.True(_backend.Applied);
    }

    [Fact]
    public async Task Checks_again_every_interval_until_something_is_found()
    {
        var service = Create();
        service.Start();

        _time.Advance(UpdateService.FirstCheckDelay);
        await WaitFor(() => service.Status.Phase == UpdatePhase.UpToDate);
        Assert.Equal(1, _backend.Checks);
        Assert.Equal(_time.GetUtcNow(), service.Status.LastChecked);

        _backend.Latest = "0.3.0";
        _time.Advance(UpdateService.CheckInterval);
        await WaitFor(() => service.Status.Phase == UpdatePhase.ReadyToRestart);
        Assert.Equal(2, _backend.Checks);

        // Once downloaded, later checks are skipped: the restart applies what is already here.
        _time.Advance(UpdateService.CheckInterval);
        await service.CheckNowAsync();
        Assert.Equal(2, _backend.Checks);
        Assert.Equal(1, _backend.Downloads);
    }

    [Fact]
    public async Task Auto_check_off_skips_the_schedule_but_not_the_button()
    {
        _backend.Latest = "0.2.0";
        var service = Create(new UpdateSettings(AutoCheck: false));
        service.Start();
        _time.Advance(UpdateService.FirstCheckDelay + UpdateService.CheckInterval);
        await Task.Delay(50);
        Assert.Equal(0, _backend.Checks);

        await service.CheckNowAsync();
        Assert.Equal(UpdatePhase.ReadyToRestart, service.Status.Phase);
    }

    [Fact]
    public async Task Auto_download_off_reports_the_version_and_the_button_downloads_it()
    {
        _backend.Latest = "0.2.0";
        var service = Create(new UpdateSettings(AutoDownload: false));
        service.Start();
        _time.Advance(UpdateService.FirstCheckDelay);
        await WaitFor(() => service.Status.Phase == UpdatePhase.Available);
        Assert.Equal("0.2.0", service.Status.AvailableVersion);
        Assert.Equal(0, _backend.Downloads);

        await service.CheckNowAsync();
        Assert.Equal(UpdatePhase.ReadyToRestart, service.Status.Phase);
        Assert.Equal(1, _backend.Downloads);
    }

    [Fact]
    public async Task A_failed_check_is_reported_and_the_next_one_recovers()
    {
        _backend.CheckError = new HttpRequestException("No such host is known.");
        var service = Create();

        await service.CheckNowAsync();
        Assert.Equal(UpdatePhase.Failed, service.Status.Phase);
        Assert.Contains("couldn't reach the update server", service.Status.Describe(), StringComparison.Ordinal);

        _backend.CheckError = null;
        await service.CheckNowAsync();
        Assert.Equal(UpdatePhase.UpToDate, service.Status.Phase);
        Assert.Null(service.Status.Error);
    }

    [Fact]
    public async Task A_failed_download_is_reported()
    {
        _backend.Latest = "0.2.0";
        _backend.DownloadError = new IOException("Disk full");
        var service = Create();

        await service.CheckNowAsync();

        Assert.Equal(UpdatePhase.Failed, service.Status.Phase);
        Assert.Equal("Disk full", service.Status.Error);
        Assert.False(service.RestartToUpdate());
    }

    [Fact]
    public async Task Concurrent_requests_share_one_check()
    {
        _backend.Latest = "0.2.0";
        _backend.Gate = new TaskCompletionSource();
        var service = Create();

        var first = service.CheckNowAsync();
        var second = service.CheckNowAsync();
        Assert.Same(first, second);

        _backend.Gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, _backend.Checks);
        Assert.Equal(1, _backend.Downloads);
    }

    [Fact]
    public void An_update_downloaded_last_session_is_ready_at_startup()
    {
        _backend.PendingVersion = "0.2.0";
        var service = Create();

        Assert.Equal(UpdatePhase.ReadyToRestart, service.Status.Phase);
        Assert.Equal("0.2.0", service.Status.AvailableVersion);
        Assert.True(service.RestartToUpdate());
    }

    [Fact]
    public async Task Updater_that_fails_to_start_leaves_the_app_running()
    {
        _backend.Latest = "0.2.0";
        _backend.ApplyError = new InvalidOperationException("Update.exe not found");
        var service = Create();
        await service.CheckNowAsync();

        Assert.False(service.RestartToUpdate());
        Assert.Equal(UpdatePhase.Failed, service.Status.Phase);
        Assert.Equal("Update.exe not found", service.Status.Error);
    }

    [Fact]
    public async Task Progress_is_reported_while_downloading()
    {
        _backend.Latest = "0.2.0";
        _backend.ProgressSteps = [10, 55, 100];
        var service = Create();
        var progress = new List<int>();
        service.StatusChanged += (_, s) =>
        {
            if (s.Phase == UpdatePhase.Downloading)
            {
                lock (progress)
                {
                    progress.Add(s.Progress);
                }
            }
        };

        await service.CheckNowAsync();

        Assert.Equal<int>([0, 10, 55, 100], progress);
        Assert.Equal("Version 0.2.0 is ready. Update restarts Clicky into it.", service.Status.Describe());
    }

    private UpdateService Create(UpdateSettings? updates = null)
    {
        var settings = new FakeSettingsStore(ClickySettings.Default with { Updates = updates ?? new UpdateSettings() });
        var service = new UpdateService(_backend, settings, NullLogger<UpdateService>.Instance, _time);
        _services.Add(service);
        return service;
    }

    private static List<UpdatePhase> Record(UpdateService service)
    {
        var phases = new List<UpdatePhase>();
        service.StatusChanged += (_, s) =>
        {
            lock (phases)
            {
                phases.Add(s.Phase);
            }
        };
        return phases;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeBackend : IUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;

        public string CurrentVersion => "0.1.0";

        public string? PendingVersion { get; set; }

        public string? Latest { get; set; }

        public Exception? CheckError { get; set; }

        public Exception? DownloadError { get; set; }

        public Exception? ApplyError { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public int[] ProgressSteps { get; set; } = [];

        public int Checks { get; private set; }

        public int Downloads { get; private set; }

        public bool Applied { get; private set; }

        public async Task<string?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return CheckError is null ? Latest : throw CheckError;
        }

        public Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            if (DownloadError is not null)
            {
                throw DownloadError;
            }

            foreach (var step in ProgressSteps)
            {
                progress(step);
            }

            PendingVersion = Latest;
            return Task.CompletedTask;
        }

        public void ApplyAfterExit()
        {
            if (ApplyError is not null)
            {
                throw ApplyError;
            }

            Applied = true;
        }
    }
}
