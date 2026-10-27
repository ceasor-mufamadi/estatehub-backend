using Cronos;
using EstateHub.Application.Cron;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Polly;

namespace EstateHub.Infrastructure.Cron
{
    public class CronJobHostedService : BackgroundService
    {
        private readonly IServiceProvider _provider;
        private readonly IConfiguration _config;
        private readonly ILogger<CronJobHostedService> _logger;
        private readonly List<CronJobSchedule> _schedules = new();

        public CronJobHostedService(IServiceProvider provider,
                                    IConfiguration config,
                                    ILogger<CronJobHostedService> logger)
        {
            _provider = provider;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            RegisterJobs();

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var nextSchedule = _schedules
                    .Where(s => s.NextRun != null)
                    .OrderBy(s => s.NextRun)
                    .FirstOrDefault();

                if (nextSchedule?.NextRun is not DateTime nextRun)
                {
                    _logger.LogWarning("No cron jobs scheduled");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var delay = nextRun - now;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);

                await RunDueJobs(stoppingToken);
            }
        }

        private async Task RunDueJobs(CancellationToken token)
        {
            var now = DateTime.UtcNow;

            var dueJobs = _schedules
                .Where(s => s.NextRun <= now)
                .ToList();

            foreach (var schedule in dueJobs)
            {
                if (schedule.Running)
                {
                    _logger.LogWarning(
                        "Job {job} already running. Skipping.",
                        schedule.Name);

                    continue;
                }

                schedule.Running = true;

                try
                {
                    using var scope = _provider.CreateScope();

                    var job = (ICronJob)
                        scope.ServiceProvider.GetRequiredService(schedule.JobType);

                    var retryPolicy = Policy
                        .Handle<Exception>()
                        .WaitAndRetryAsync(
                            schedule.RetryCount,
                            retryAttempt =>
                                TimeSpan.FromSeconds(
                                    schedule.RetryDelaySeconds),
                            (exception, delay, retryCount, context) =>
                            {
                                _logger.LogWarning(
                                    exception,
                                    "Retry {retry}/{maxRetries} for job {job} after {delay}",
                                    retryCount,
                                    schedule.RetryCount,
                                    schedule.Name,
                                    delay);
                            });

                    _logger.LogInformation(
                        "Running job {job}",
                        schedule.Name);

                    using var run = JobRunContext.Begin(schedule.Name);
                    var attempt = 0;
                    await retryPolicy.ExecuteAsync(async () =>
                    {
                        JobRunContext.Current!.Attempt = ++attempt;
                        await job.ExecuteAsync(token);
                    });

                    _logger.LogInformation(
                        "Completed job {job}",
                        schedule.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Job {job} failed after retries",
                        schedule.Name);
                }
                finally
                {
                    schedule.Running = false;

                    schedule.NextRun =
                        schedule.Expression.GetNextOccurrence(
                            DateTime.UtcNow,
                            schedule.TimeZoneInfo);

                    _logger.LogInformation(
                        "Next run for {job}: {nextRun}",
                        schedule.Name,
                        schedule.NextRun);
                }
            }
        }

        
        private void RegisterJobs()
        {
            var jobTypes = AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t =>
                    typeof(ICronJob).IsAssignableFrom(t) &&
                    !t.IsInterface &&
                    !t.IsAbstract)
                .ToList();

            foreach (var jobType in jobTypes)
            {
                var attribute = jobType.GetCustomAttribute<CronJobAttribute>();

                if (attribute == null)
                    continue;

                var section = _config.GetSection($"CronJobs:{attribute.Name}");

                var config = section.Get<CronJobConfig>();

                if (config == null)
                {
                    _logger.LogWarning(
                        "Missing config for job {job}",
                        attribute.Name);

                    continue;
                }

                if (!config.Enabled)
                {
                    _logger.LogInformation(
                        "Job {job} disabled",
                        attribute.Name);

                    continue;
                }

                TimeZoneInfo timezone = ResolveTimeZone("South Africa Standard Time");

                var expression = CronExpression.Parse(config.Cron);

                var nextRun = expression.GetNextOccurrence(
                    DateTime.UtcNow,
                    timezone);

                _schedules.Add(new CronJobSchedule
                {
                    Name = attribute.Name,
                    JobType = jobType,
                    Expression = expression,
                    NextRun = nextRun,
                    TimeZoneInfo = timezone,
                    RetryCount = config.RetryCount,
                    RetryDelaySeconds = config.RetryDelaySeconds
                });

                _logger.LogInformation(
                    "Registered job {job}. Cron: {cron}. TZ: {timezone}. Next: {nextRun}",
                    attribute.Name,
                    config.Cron,
                    timezone.DisplayName,
                    nextRun);
            }
        }

        private TimeZoneInfo ResolveTimeZone(string timezone)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch
            {
                var mappings = new Dictionary<string, string>
                {
                    ["South Africa Standard Time"] = "Africa/Johannesburg"
                };

                if (mappings.TryGetValue(timezone, out var linuxTz))
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(linuxTz);
                }

                return TimeZoneInfo.Utc;
            }
        }
    }
}