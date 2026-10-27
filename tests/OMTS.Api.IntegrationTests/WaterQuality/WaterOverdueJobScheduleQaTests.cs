using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Infrastructure.Jobs;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of OMTS-214 item 3: the shared <see cref="DailyJobHostedService"/> schedule driven by a manual clock (its timers
/// fire only when the test moves the clock), so "no double run", "catch up once", failures, shutdown and the zone
/// license reminder's unchanged schedule are checked without waiting for real time.
/// </summary>
public class WaterOverdueJobScheduleQaTests
{
    private static readonly DateOnly Day = new(2026, 10, 22);

    private static DateTimeOffset Local(int hour, int minute = 0, int days = 0) =>
        new(Day.AddDays(days).ToDateTime(new TimeOnly(hour, minute)), BusinessClock.UtcOffset);

    [Fact]
    public async Task Start_before_the_run_time_waits_for_it_and_runs_once_a_day()
    {
        var clock = new ManualClock(Local(5, 0));
        var job = new CountingJob(new Schedule(new TimeOnly(6, 0), CatchUpOnStart: true), clock);

        await job.StartAsync(CancellationToken.None);
        try
        {
            await clock.WaitForTimerAsync();
            Assert.Empty(job.Runs);

            await clock.AdvanceToAsync(Local(5, 59, 0));
            Assert.Empty(job.Runs);
            await clock.AdvanceToAsync(Local(6, 0));
            await job.WaitForRunsAsync(1);
            await clock.WaitForTimerAsync();

            await clock.AdvanceToAsync(Local(5, 59, days: 1));
            Assert.Single(job.Runs);
            await clock.AdvanceToAsync(Local(6, 0, days: 1));
            await job.WaitForRunsAsync(2);

            Assert.Equal(new[] { Day, Day.AddDays(1) }, job.Runs);
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Start_after_the_run_time_catches_up_once_and_next_runs_tomorrow()
    {
        var clock = new ManualClock(Local(8, 0));
        var job = new CountingJob(new Schedule(new TimeOnly(6, 0), CatchUpOnStart: true), clock);

        await job.StartAsync(CancellationToken.None);
        try
        {
            await job.WaitForRunsAsync(1);
            await clock.WaitForTimerAsync();
            await clock.AdvanceToAsync(Local(23, 59));
            await clock.AdvanceToAsync(Local(5, 59, days: 1));
            Assert.Single(job.Runs);

            await clock.AdvanceToAsync(Local(6, 0, days: 1));
            await job.WaitForRunsAsync(2);

            Assert.Equal(new[] { Day, Day.AddDays(1) }, job.Runs);
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Without_catch_up_a_start_after_the_run_time_waits_until_tomorrow()
    {
        var clock = new ManualClock(Local(8, 0));
        var job = new CountingJob(new Schedule(new TimeOnly(6, 0), CatchUpOnStart: false), clock);

        await job.StartAsync(CancellationToken.None);
        try
        {
            await clock.WaitForTimerAsync();
            Assert.Equal(Local(6, 0, days: 1), clock.NextDueAt);
            Assert.Empty(job.Runs);
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
    }

    // A failed run is logged (with the job name) and the job keeps its schedule.
    [Fact]
    public async Task Failed_run_is_logged_and_the_next_day_still_runs()
    {
        var clock = new ManualClock(Local(8, 0));
        var logs = new CapturingLoggerProvider();
        var job = new CountingJob(new Schedule(new TimeOnly(6, 0), CatchUpOnStart: true), clock, logs.CreateLogger("qa"), failFirst: true);

        await job.StartAsync(CancellationToken.None);
        try
        {
            await job.WaitForRunsAsync(1);
            await clock.WaitForTimerAsync();
            await clock.AdvanceToAsync(Local(6, 0, days: 1));
            await job.WaitForRunsAsync(2);
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }

        Assert.Contains(logs.Lines, l => l.StartsWith("Error: QA job run failed.") && l.Contains("simulated failure"));
        Assert.True(job.ExecuteTask!.IsCompleted);
    }

    // Stopping the host while the job waits, or during a run that honours the token, ends it without an error line.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_while_waiting_or_running_ends_the_job_without_an_error(bool duringRun)
    {
        var clock = new ManualClock(duringRun ? Local(8, 0) : Local(5, 0));
        var logs = new CapturingLoggerProvider();
        var job = new CountingJob(new Schedule(new TimeOnly(6, 0), CatchUpOnStart: true), clock, logs.CreateLogger("qa"), blockRuns: duringRun);

        await job.StartAsync(CancellationToken.None);
        if (duringRun)
        {
            await job.WaitForRunsAsync(1);
        }
        else
        {
            await clock.WaitForTimerAsync();
        }

        var stop = job.StopAsync(CancellationToken.None);
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10))));

        Assert.True(job.ExecuteTask!.IsCompleted);
        Assert.DoesNotContain(logs.Lines, l => l.StartsWith("Error") || l.StartsWith("Critical"));
    }

    // The zone license reminder uses the same base class: 07:00, no run before, one run at it.
    [Fact]
    public async Task Zone_license_reminder_keeps_its_07_00_schedule_on_the_shared_base()
    {
        await using var api = new WaterApiFactory();
        var clock = new ManualClock(Local(6, 0));
        var logs = new CapturingLoggerProvider();
        var hosted = new ZoneLicenseReminderHostedService(
            api.Services.GetRequiredService<ZoneLicenseReminderJob>(),
            Options.Create(new ZoneLicenseReminderOptions { CatchUpOnStart = true }),
            clock,
            new TypedLogger<ZoneLicenseReminderHostedService>(logs.CreateLogger("license")));

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await clock.WaitForTimerAsync();
            Assert.Equal(Local(7, 0), clock.NextDueAt);
            Assert.DoesNotContain(logs.Lines, l => l.Contains("Zone license reminder sent"));

            await clock.AdvanceToAsync(Local(7, 0));
            await WaitUntilAsync(() => logs.Lines.Count(l => l.Contains("Zone license reminder sent")) == 1);
            await clock.WaitForTimerAsync();
            Assert.Equal(Local(7, 0, days: 1), clock.NextDueAt);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    // One hosted service per job: a second registration would run every reminder twice a day.
    [Fact]
    public async Task Each_daily_job_is_registered_once_with_its_own_options()
    {
        await using var api = new WaterApiFactory();

        var hosted = api.Services.GetServices<IHostedService>().ToList();

        Assert.Single(hosted, h => h is WaterMeasurementOverdueJob);
        Assert.Single(hosted, h => h is ZoneLicenseReminderHostedService);
        var water = new WaterMeasurementOverdueOptions();
        var license = new ZoneLicenseReminderOptions();
        Assert.Equal((new TimeOnly(6, 0), true), (water.RunAt, water.CatchUpOnStart));
        Assert.Equal((new TimeOnly(7, 0), true), (license.RunAt, license.CatchUpOnStart));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Condition not reached within 15 seconds.");
            await Task.Delay(20);
        }
    }

    private sealed record Schedule(TimeOnly RunAt, bool CatchUpOnStart) : IDailyJobOptions;

    /// <summary>A daily job that records the business date of each run; it can fail its first run or block until stopped.</summary>
    private sealed class CountingJob(IDailyJobOptions options, TimeProvider clock, ILogger? logger = null, bool failFirst = false, bool blockRuns = false)
        : DailyJobHostedService(options, clock, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
        private readonly ConcurrentQueue<DateOnly> _runs = new();

        public IReadOnlyList<DateOnly> Runs => _runs.ToList();

        protected override string JobName => "QA job";

        public async Task WaitForRunsAsync(int count) => await WaitUntilAsync(() => _runs.Count >= count);

        protected override async Task RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
        {
            _runs.Enqueue(today);
            if (failFirst && _runs.Count == 1)
            {
                throw new InvalidOperationException("simulated failure");
            }

            if (blockRuns)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>A clock that moves only when told to; its timers (Task.Delay with this provider) fire when it passes their due time.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>Due time of the earliest pending timer, or null.</summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            lock (_gate)
            {
                return _timers.Where(t => t.DueAt is not null).Select(t => t.DueAt).Min();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Waits until a timer is pending (the job computed its next delay).</summary>
    public async Task WaitForTimerAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (NextDueAt is null)
        {
            Assert.True(DateTime.UtcNow < until, "No timer was created within 15 seconds.");
            await Task.Delay(20);
        }
    }

    /// <summary>Moves the clock to <paramref name="instant"/> and fires every timer due by then.</summary>
    public async Task AdvanceToAsync(DateTimeOffset instant)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now = instant;
            due = _timers.Where(t => t.DueAt is { } at && at <= instant).ToList();
            foreach (var timer in due)
            {
                timer.DueAt = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }

        // Let the continuations run before the caller looks.
        await Task.Delay(50);
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                if (!clock._timers.Contains(this))
                {
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                DueAt = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
