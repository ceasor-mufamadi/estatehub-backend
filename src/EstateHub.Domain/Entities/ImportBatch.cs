namespace EstateHub.Domain.Entities;

/// <summary>
/// One spreadsheet import of units, owners and tenants (slice 3.3). The upload is parsed and checked
/// into a preview (nothing else is saved); a commit creates everything in one transaction, then
/// invitations go out in batches within the estate's hourly cap (<see cref="ImportInvite"/>).
/// The batch keeps the parsed rows, problems and outcome as the import's trail.
/// </summary>
public class ImportBatch : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public string FileName { get; set; } = string.Empty;

    /// <summary>See <see cref="ImportStatuses"/>.</summary>
    public string Status { get; set; } = ImportStatuses.Ready;

    public int RowCount { get; set; }

    /// <summary>The parsed rows (JSON), re-checked at commit.</summary>
    public string RowsJson { get; set; } = "[]";

    /// <summary>Problems found (JSON), by row and column.</summary>
    public string ProblemsJson { get; set; } = "[]";

    /// <summary>Counts from the preview, or from the commit once done (JSON).</summary>
    public string SummaryJson { get; set; } = "{}";

    /// <summary>Last progress or outcome message shown to staff.</summary>
    public string? Message { get; set; }

    public bool SendInvitations { get; set; }

    public Guid CreatedByUserId { get; set; }
    public Guid? CommittedByUserId { get; set; }

    /// <summary>The committer's role then: invitations are sent on their behalf, within what that role may grant.</summary>
    public string? CommittedByRole { get; set; }

    public DateTime? CommittedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime? LastInviteBatchAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class ImportStatuses
{
    /// <summary>Checked, no problems: can be committed.</summary>
    public const string Ready = "ready";

    /// <summary>Has problems: fix the file and upload again.</summary>
    public const string Invalid = "invalid";

    public const string Queued = "queued";
    public const string Running = "running";

    /// <summary>Everything created (invitations may still be going out in batches).</summary>
    public const string Done = "done";

    /// <summary>Nothing was created (re-check found problems, or an error rolled it back).</summary>
    public const string Failed = "failed";

    public const string Discarded = "discarded";

    public static bool IsActive(string status) => status is Queued or Running;
}

/// <summary>A portal invitation an import will send, waiting for room under the estate's hourly cap.</summary>
public class ImportInvite : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public Guid ImportBatchId { get; set; }
    public Guid PersonId { get; set; }

    /// <summary>pending, sent, linked (already had an account) or failed.</summary>
    public string Status { get; set; } = ImportInviteStatuses.Pending;

    public string? Message { get; set; }
    public DateTime? SentAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class ImportInviteStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Linked = "linked";
    public const string Failed = "failed";
}
