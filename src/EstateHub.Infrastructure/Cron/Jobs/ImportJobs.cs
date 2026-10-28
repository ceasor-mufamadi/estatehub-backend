using EstateHub.Application.Auth;
using EstateHub.Application.Cron;
using EstateHub.Application.Imports;
using EstateHub.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EstateHub.Infrastructure.Cron.Jobs;

/// <summary>
/// ⏰ Every 10 minutes: sends imports' waiting invitations as the estate's hourly cap frees up (decided
/// 2026-10-28: keep the cap, to keep SMS costs down). Sent on behalf of whoever committed the import,
/// only while they are still an active member whose role may invite; otherwise the batch waits and the
/// import page says why (the rest can be invited by hand from People).
/// </summary>
[CronJob("ImportInvitationsJob")]
public class ImportInvitationsJob : ICronJob
{
    private readonly EstateJobRunner _estates;
    private readonly ILogger<ImportInvitationsJob> _logger;

    public ImportInvitationsJob(EstateJobRunner estates, ILogger<ImportInvitationsJob> logger)
    {
        _estates = estates;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var sent = 0;
        await _estates.ForEachEstateAsync(null, async (services, estateId, ct) =>
        {
            var imports = services.GetRequiredService<IImportRepository>();
            foreach (var batchId in await imports.BatchesWithPendingInvitesAsync(ct))
                sent += await SendAsync(services, estateId, batchId, ct);
        }, cancellationToken);
        _logger.LogInformation("ImportInvitationsJob: {Sent} invitations handled", sent);
    }

    private static async Task<int> SendAsync(IServiceProvider estateScope, Guid estateId, Guid batchId, CancellationToken ct)
    {
        var imports = estateScope.GetRequiredService<IImportRepository>();
        var batch = await imports.GetAsync(batchId, ct);
        if (batch?.CommittedByUserId is not { } userId)
            return 0;

        // Acting for a person: only while they still belong here with a role that may invite.
        var membership = await estateScope.GetRequiredService<IMembersRepository>().FindMembershipAsync(userId, ct);
        if (membership is not { IsActive: true } || !RolePermissions.For(membership.Role).Contains(Permissions.ImportsRun))
        {
            const string paused = "Invitations paused: the person who ran this import can no longer import here. Invite the rest from People.";
            if (batch.Message != paused)
            {
                batch.Message = paused;
                await imports.SaveChangesAsync(ct);
            }
            return 0;
        }

        // A fresh scope acting as them (ICurrentUser is resolved once per scope).
        using var scope = estateScope.GetRequiredService<IServiceScopeFactory>().CreateScope();
        scope.ServiceProvider.GetRequiredService<Application.Tenancy.ITenantContext>().Use(estateId);
        scope.ServiceProvider.GetRequiredService<CallerOverride>().User = new SnapshotCurrentUser(userId.ToString(), estateId, membership.Role);
        return await scope.ServiceProvider.GetRequiredService<IImportService>().SendPendingInvitationsAsync(batchId, ct);
    }
}
