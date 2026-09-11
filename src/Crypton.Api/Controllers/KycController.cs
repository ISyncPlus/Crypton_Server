using Crypton.Api.Contracts;
using Crypton.Api.Infrastructure;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Kyc;
using Crypton.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers;

[Route("api/kyc")]
public sealed class KycController(KycService kyc, SettingsService settings, CryptonDbContext db) : ApiControllerBase
{
    [HttpGet]
    public async Task<KycStatusResponse> Status(CancellationToken ct)
    {
        var userId = CurrentUserId;
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var submissions = await db.KycSubmissions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
        var tiers = (await settings.GetAsync<KycLimitSettings>(ct)).Tiers.OrderBy(t => t.Tier).ToList();
        var pending1 = submissions.Any(s => s.TargetTier == 1 && s.Status == KycSubmissionStatus.Pending);
        var pending2 = submissions.Any(s => s.TargetTier == 2 && s.Status == KycSubmissionStatus.Pending);
        return new KycStatusResponse(user.KycTier, tiers, submissions.Select(s => s.ToDto()).ToList(),
            user.KycTier == 0 && !pending1, user.KycTier == 1 && !pending2);
    }

    [HttpPost("tier1")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<KycSubmissionDto> Tier1(Tier1RequestDto request, CancellationToken ct) =>
        (await kyc.SubmitTier1Async(CurrentUserId, new Tier1Request(request.FirstName, request.LastName, request.DateOfBirth, request.PhoneNumber,
            request.AddressLine, request.City, request.State, request.IdType, request.IdNumber), HttpContext.ClientIp(), ct)).ToDto();

    [HttpPost("tier2")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    [RequestSizeLimit(25_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<KycSubmissionDto> Tier2([FromForm] string documentKind, IFormFile? idFront, IFormFile? idBack, IFormFile? selfie, IFormFile? proofOfAddress, CancellationToken ct)
    {
        var uploads = new List<KycUpload>();
        void Add(KycDocumentType type, IFormFile? file)
        {
            if (file is not null)
            {
                uploads.Add(new KycUpload(type, file.FileName, file.Length, file.OpenReadStream));
            }
        }

        Add(KycDocumentType.IdFront, idFront);
        Add(KycDocumentType.IdBack, idBack);
        Add(KycDocumentType.Selfie, selfie);
        Add(KycDocumentType.ProofOfAddress, proofOfAddress);
        if (string.IsNullOrWhiteSpace(documentKind))
        {
            throw AppException.Validation("Choose the type of ID document.");
        }

        return (await kyc.SubmitTier2Async(CurrentUserId, documentKind, uploads, HttpContext.ClientIp(), ct)).ToDto();
    }
}
