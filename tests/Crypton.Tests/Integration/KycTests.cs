using System.Net.Http.Headers;
using Crypton.Api.Contracts;
using Crypton.Core.Domain;
using Crypton.Tests.Infrastructure;

namespace Crypton.Tests.Integration;

[Collection(ApiCollection.Name)]
public class KycTests(CryptonFactory factory)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x01, 0x02, 0x03];

    private static object Tier1(string idNumber, string dob = "1991-04-12") => new
    {
        firstName = "Adaora",
        lastName = "Okafor",
        dateOfBirth = dob,
        phoneNumber = "0803 123 4567",
        addressLine = "12 Admiralty Way",
        city = "Lekki",
        state = "Lagos",
        idType = "NIN",
        idNumber,
    };

    [Fact]
    public async Task Tier1_manual_review_then_approval_unlocks_limits()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var idNumber = RandomId();
        var submission = await user.Client.PostOk<KycSubmissionDto>("/api/kyc/tier1", Tier1(idNumber));
        Assert.Equal(KycSubmissionStatus.Pending, submission.Status);

        var status = await user.Client.GetOk<KycStatusResponse>("/api/kyc");
        Assert.False(status.CanSubmitTier1);
        Assert.Equal(3, status.Tiers.Count);

        var duplicatePending = await user.Client.PostProblem("/api/kyc/tier1", Tier1(idNumber));
        Assert.Equal("invalid_state", duplicatePending.Code);

        var admin = await TestAccounts.AdminAsync(factory);
        var queue = await admin.Client.GetOk<PageDto<AdminKycSubmissionDto>>("/api/admin/kyc?status=Pending&pageSize=200");
        var item = Assert.Single(queue.Items, s => s.Id == submission.Id);
        Assert.EndsWith(idNumber[^4..], item.IdNumberMasked);
        Assert.Equal("+2348031234567", item.PhoneNumber);

        await admin.Client.PostOk<AdminKycSubmissionDto>($"/api/admin/kyc/{submission.Id}/approve", new { note = "Matched NIMC slip" });
        var me = await user.Client.GetOk<MeResponse>("/api/me");
        Assert.Equal(1, me.User.KycTier);
        Assert.Equal("Adaora", me.User.FirstName);
        Assert.Contains(me.Limits, l => l.Kind == Crypton.Core.Kyc.LimitKind.FiatDeposit && l.DailyLimitNgn > 0);

        // The same identity cannot verify another account.
        var other = await TestAccounts.CreateAsync(factory);
        var reused = await other.Client.PostProblem("/api/kyc/tier1", Tier1(idNumber));
        Assert.Equal("identity_in_use", reused.Code);
    }

    [Fact]
    public async Task Tier1_validates_age_and_id_format()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var minor = await user.Client.PostProblem("/api/kyc/tier1", Tier1(RandomId(), DateTime.UtcNow.AddYears(-16).ToString("yyyy-MM-dd")));
        Assert.Equal("validation_error", minor.Code);
        var shortId = await user.Client.PostProblem("/api/kyc/tier1", Tier1("12345"));
        Assert.Equal("validation_error", shortId.Code);
    }

    [Fact]
    public async Task Tier2_documents_are_reviewed()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1);

        using (var bad = Form("passport", ("idFront", "notes.txt", "hello"u8.ToArray()), ("selfie", "me.png", Png), ("proofOfAddress", "bill.png", Png)))
        {
            using var response = await user.Client.PostAsync("/api/kyc/tier2", bad);
            var problem = await Http.ReadProblem(response);
            Assert.Equal("validation_error", problem.Code);
        }

        KycSubmissionDto submission;
        using (var form = Form("passport", ("idFront", "passport.png", Png), ("selfie", "selfie.png", Png), ("proofOfAddress", "bill.png", Png)))
        {
            using var response = await user.Client.PostAsync("/api/kyc/tier2", form);
            response.EnsureSuccessStatusCode();
            submission = (await System.Text.Json.JsonSerializer.DeserializeAsync<KycSubmissionDto>(await response.Content.ReadAsStreamAsync(), Http.Json))!;
        }

        Assert.Equal(2, submission.TargetTier);
        var admin = await TestAccounts.AdminAsync(factory);
        var detail = await admin.Client.GetOk<AdminKycSubmissionDto>($"/api/admin/kyc/{submission.Id}");
        Assert.Equal(3, detail.Documents.Count);

        using (var file = await admin.Client.GetAsync($"/api/admin/kyc/documents/{detail.Documents[0].Id}"))
        {
            file.EnsureSuccessStatusCode();
            Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Png, await file.Content.ReadAsByteArrayAsync());
        }

        // Users cannot fetch documents through admin routes.
        using (var denied = await user.Client.GetAsync($"/api/admin/kyc/documents/{detail.Documents[0].Id}"))
        {
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        }

        await admin.Client.PostOk<AdminKycSubmissionDto>($"/api/admin/kyc/{submission.Id}/approve", new { note = "OK" });
        Assert.Equal(2, (await user.Client.GetOk<MeResponse>("/api/me")).User.KycTier);
    }

    [Fact]
    public async Task Rejection_allows_resubmission()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var first = await user.Client.PostOk<KycSubmissionDto>("/api/kyc/tier1", Tier1(RandomId()));
        var admin = await TestAccounts.AdminAsync(factory);
        await admin.Client.PostOk<AdminKycSubmissionDto>($"/api/admin/kyc/{first.Id}/reject", new { reason = "Name does not match the ID." });
        var status = await user.Client.GetOk<KycStatusResponse>("/api/kyc");
        Assert.True(status.CanSubmitTier1);
        Assert.Equal("Name does not match the ID.", status.Submissions[0].RejectionReason);
        await user.Client.PostOk<KycSubmissionDto>("/api/kyc/tier1", Tier1(RandomId()));
    }

    private static string RandomId() => string.Concat(Enumerable.Range(0, 11).Select(_ => Random.Shared.Next(0, 10)));

    private static MultipartFormDataContent Form(string kind, params (string Field, string FileName, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent { { new StringContent(kind), "documentKind" } };
        foreach (var (field, name, bytes) in files)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, field, name);
        }

        return form;
    }
}
