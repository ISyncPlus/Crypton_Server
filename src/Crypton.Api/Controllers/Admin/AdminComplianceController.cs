using Crypton.Api.Auth;
using Crypton.Api.Contracts;
using Crypton.Core.Aml;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Kyc;
using Crypton.Core.Settings;
using Crypton.Core.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers.Admin;

[Route("api/admin")]
[Authorize(Policy = Policies.Compliance)]
public sealed class AdminComplianceController(
    CryptonDbContext db,
    KycService kyc,
    SettingsService settings,
    ChainGatewayRegistry chains,
    AuditService audit,
    TimeProvider clock) : ApiControllerBase
{
    [HttpGet("kyc")]
    public async Task<PageDto<AdminKycSubmissionDto>> Submissions([FromQuery] KycSubmissionStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = from s in db.KycSubmissions.AsNoTracking().Include(s => s.Documents)
                    join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                    select new { s, u.Email };
        if (status is not null)
        {
            query = query.Where(x => x.s.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.s.Status == KycSubmissionStatus.Pending ? 0 : 1).ThenBy(x => x.s.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(x => ToDto(x.s, x.Email)).ToList().ToPage(request, total);
    }

    [HttpGet("kyc/{id:guid}")]
    public async Task<AdminKycSubmissionDto> Submission(Guid id, CancellationToken ct)
    {
        var row = await (from s in db.KycSubmissions.AsNoTracking().Include(s => s.Documents)
                         join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                         where s.Id == id
                         select new { s, u.Email }).FirstOrDefaultAsync(ct) ?? throw AppException.NotFound("Submission");
        return ToDto(row.s, row.Email);
    }

    /// <summary>Reveals the full ID number for manual verification. Every reveal is audited.</summary>
    [HttpPost("kyc/{id:guid}/reveal-id")]
    public async Task<object> RevealId(Guid id, CancellationToken ct)
    {
        var submission = await db.KycSubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound("Submission");
        await audit.RecordNowAsync("admin.kyc_id_revealed", submission.UserId, new AuditContext(CurrentUserId, HttpContext.Connection.RemoteIpAddress?.ToString()), "kyc_submission", id.ToString(), ct: ct);
        return new { idNumber = kyc.RevealIdNumber(submission) };
    }

    [HttpGet("kyc/documents/{documentId:guid}")]
    public async Task<IActionResult> Document(Guid documentId, CancellationToken ct)
    {
        var (content, contentType, fileName) = await kyc.OpenDocumentAsync(documentId, ct);
        Response.Headers.CacheControl = "no-store";
        return File(content, contentType, fileName);
    }

    [HttpPost("kyc/{id:guid}/approve")]
    public async Task<AdminKycSubmissionDto> Approve(Guid id, AdminNoteRequest request, CancellationToken ct)
    {
        await kyc.ApproveAsync(CurrentUserId, id, request.Note, ct);
        return await Submission(id, ct);
    }

    [HttpPost("kyc/{id:guid}/reject")]
    public async Task<AdminKycSubmissionDto> Reject(Guid id, AdminReasonRequest request, CancellationToken ct)
    {
        await kyc.RejectAsync(CurrentUserId, id, request.Reason, ct);
        return await Submission(id, ct);
    }

    [HttpGet("aml/alerts")]
    public async Task<PageDto<AdminAlertDto>> Alerts([FromQuery] AmlAlertStatus? status, [FromQuery] Guid? userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = from a in db.AmlAlerts.AsNoTracking()
                    join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                    select new { a, u.Email };
        if (status is not null)
        {
            query = query.Where(x => x.a.Status == status);
        }

        if (userId is not null)
        {
            query = query.Where(x => x.a.UserId == userId);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.a.Severity).ThenByDescending(x => x.a.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(x => new AdminAlertDto(x.a.Id, x.a.UserId, x.Email ?? "", x.a.RuleCode, x.a.Action, x.a.Severity, x.a.SubjectType, x.a.SubjectId, x.a.Summary,
            x.a.Details, x.a.Status, x.a.ResolutionNote, x.a.CreatedAt, x.a.ResolvedAt)).ToList().ToPage(request, total);
    }

    [HttpPost("aml/alerts/{id:guid}/resolve")]
    public async Task<IActionResult> ResolveAlert(Guid id, AdminResolveAlertRequest request, CancellationToken ct)
    {
        if (request.Status == AmlAlertStatus.Open)
        {
            throw AppException.Validation("Choose Dismissed or Confirmed.");
        }

        var alert = await db.AmlAlerts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw AppException.NotFound("Alert");
        alert.Status = request.Status;
        alert.ResolutionNote = request.Note;
        alert.ResolvedBy = CurrentUserId;
        alert.ResolvedAt = clock.GetUtcNow();
        audit.Record(AuditActions.AdminAmlAlertResolved, alert.UserId, new AuditContext(CurrentUserId, null), "aml_alert", id.ToString(), new { request.Status, request.Note });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("aml/rules")]
    public async Task<AmlSettings> Rules(CancellationToken ct) => await settings.GetAsync<AmlSettings>(ct);

    [HttpPut("aml/rules")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<AmlSettings> UpdateRules(AmlSettings request, CancellationToken ct)
    {
        var saved = await settings.SaveAsync(request, CurrentUserId, ct);
        await audit.RecordNowAsync(AuditActions.AdminSettingsChanged, null, new AuditContext(CurrentUserId, null), "settings", "aml", request, ct);
        return saved;
    }

    [HttpGet("aml/blocked-addresses")]
    public async Task<PageDto<BlockedAddress>> BlockedAddresses([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = db.BlockedAddresses.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            query = query.Where(b => b.Address.ToLower().Contains(term) || b.Reason.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return items.ToPage(request, total);
    }

    [HttpPost("aml/blocked-addresses")]
    public async Task<BlockedAddress> BlockAddress(AdminBlockAddressRequest request, CancellationToken ct)
    {
        var (network, address) = NormalizeBlockEntry(request.Network, request.Address);
        var existing = await db.BlockedAddresses.FirstOrDefaultAsync(b => b.Network == network && b.Address == address, ct);
        if (existing is not null)
        {
            return existing;
        }

        var entry = new BlockedAddress { Id = Ids.New(), Network = network, Address = address, Reason = request.Reason.Trim(), Source = "manual", CreatedBy = CurrentUserId, CreatedAt = clock.GetUtcNow() };
        db.BlockedAddresses.Add(entry);
        audit.Record(AuditActions.AdminBlockedAddressAdded, null, new AuditContext(CurrentUserId, null), "blocked_address", entry.Id.ToString(), new { network, address, request.Reason });
        await db.SaveChangesAsync(ct);
        return entry;
    }

    [HttpPost("aml/blocked-addresses/import")]
    public async Task<object> ImportBlockList(AdminImportBlockListRequest request, CancellationToken ct)
    {
        var lines = request.Addresses.Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (lines.Count > 10_000)
        {
            throw AppException.Validation("Import at most 10,000 addresses at a time.");
        }

        var added = 0;
        var invalid = new List<string>();
        var now = clock.GetUtcNow();
        foreach (var line in lines)
        {
            string network, address;
            try
            {
                (network, address) = NormalizeBlockEntry(request.Network, line);
            }
            catch (AppException)
            {
                invalid.Add(line);
                continue;
            }

            if (await db.BlockedAddresses.AnyAsync(b => b.Network == network && b.Address == address, ct) ||
                db.BlockedAddresses.Local.Any(b => b.Network == network && b.Address == address))
            {
                continue;
            }

            db.BlockedAddresses.Add(new BlockedAddress { Id = Ids.New(), Network = network, Address = address, Reason = request.Reason.Trim(), Source = request.Source ?? "import", CreatedBy = CurrentUserId, CreatedAt = now });
            added++;
        }

        audit.Record(AuditActions.AdminBlockedAddressAdded, null, new AuditContext(CurrentUserId, null), "blocked_address", "import", new { request.Network, added, invalid = invalid.Count });
        await db.SaveChangesAsync(ct);
        return new { added, invalid };
    }

    [HttpDelete("aml/blocked-addresses/{id:guid}")]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken ct)
    {
        var entry = await db.BlockedAddresses.FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw AppException.NotFound("Entry");
        db.BlockedAddresses.Remove(entry);
        audit.Record(AuditActions.AdminBlockedAddressRemoved, null, new AuditContext(CurrentUserId, null), "blocked_address", id.ToString(), new { entry.Network, entry.Address });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private (string Network, string Address) NormalizeBlockEntry(string network, string address)
    {
        var n = network.Trim().ToLowerInvariant();
        var gateway = chains.Find(n) ?? throw AppException.Validation("Network must be bitcoin or ethereum.");
        if (!gateway.IsValidAddress(address.Trim()))
        {
            throw AppException.Validation($"'{address}' is not a valid {n} address.", ErrorCodes.InvalidAddress);
        }

        return (n, AmlEngine.NormalizeForLookup(n, gateway.NormalizeAddress(address.Trim())));
    }

    private static AdminKycSubmissionDto ToDto(KycSubmission s, string? email) => new(s.Id, s.UserId, email ?? "", s.TargetTier, s.Status, s.Provider, s.FirstName, s.LastName,
        s.DateOfBirth, s.PhoneNumber, string.Join(", ", new[] { s.AddressLine, s.City, s.State }.Where(p => !string.IsNullOrWhiteSpace(p))), s.IdType, s.IdNumberMasked,
        s.DocumentKind, s.ProviderResult, s.RejectionReason, s.CreatedAt, s.ReviewedAt,
        s.Documents.OrderBy(d => d.Type).Select(d => new AdminKycDocumentDto(d.Id, d.Type, d.ContentType, d.SizeBytes)).ToList());
}
