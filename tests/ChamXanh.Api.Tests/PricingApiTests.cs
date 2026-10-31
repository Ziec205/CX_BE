using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Pricing;

namespace ChamXanh.Api.Tests;

[Collection(ApiCollection.Name)]
public class PricingApiTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    [Fact]
    public async Task Health_and_seeded_public_pricing()
    {
        var client = Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var current = await (await client.GetAsync("/api/pricing/current")).EnsureOk();
        Assert.Equal(1, current.GetProperty("version").GetInt32());
        Assert.Equal(5, current.GetProperty("topUpPackages").GetArrayLength());
    }

    [Fact]
    public async Task Admin_endpoints_require_admin_with_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync("/api/admin/price-books")).StatusCode);
        var (member, _) = await Member();
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/admin/price-books")).StatusCode);
        var editor = await Admin("editor.a", AdminRoles.Editor); // Biên tập không có quyền bảng giá
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/price-books")).StatusCode);
        var marketing = await Admin("mkt.a", AdminRoles.Marketing);
        Assert.Equal(HttpStatusCode.OK, (await marketing.GetAsync("/api/admin/price-books")).StatusCode);
    }

    [Fact]
    public async Task Full_flow_marketing_drafts_superadmin_approves_and_marketing_cannot_approve()
    {
        var maker = await Admin("marketing.lan", AdminRoles.Marketing);
        var checker = await SuperAdmin();

        var draft = await (await maker.PostAsJsonAsync("/api/admin/price-books", new { sourceVersion = (int?)null })).EnsureOk<PriceBook>();
        Assert.Equal(2, draft.Version);
        Assert.Equal("marketing.lan", draft.CreatedBy);

        draft.TopUpPackages.First(p => p.Code == "RUNG").BonusXu = 150;
        (await maker.PutAsJsonAsync($"/api/admin/price-books/{draft.Id}", draft, Json)).EnsureSuccessStatusCode();
        await (await maker.PostAsync($"/api/admin/price-books/{draft.Id}/submit", null)).EnsureOk();

        // Marketing không có quyền duyệt (RBAC 02 §4.1)
        var denied = await maker.PostAsJsonAsync($"/api/admin/price-books/{draft.Id}/approve", new { effectiveFrom = (DateTime?)null, warningsAcknowledged = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        (await checker.PostAsJsonAsync($"/api/admin/price-books/{draft.Id}/approve", new { effectiveFrom = (DateTime?)null, warningsAcknowledged = false })).EnsureSuccessStatusCode();

        var books = await (await checker.GetAsync("/api/admin/price-books")).EnsureOk<List<PriceBook>>();
        Assert.Equal(PriceBookStatus.Active, books.Single(b => b.Version == 2).Status);
        Assert.Equal(PriceBookStatus.Expired, books.Single(b => b.Version == 1).Status);

        var current = await (await Anonymous().GetAsync("/api/pricing/current")).EnsureOk();
        Assert.Equal(150, current.GetProperty("topUpPackages").EnumerateArray().First(p => p.GetProperty("code").GetString() == "RUNG").GetProperty("bonusXu").GetInt64());
    }

    [Fact]
    public async Task Super_admin_cannot_approve_own_draft()
    {
        var super = await SuperAdmin();
        var draft = await (await super.PostAsJsonAsync("/api/admin/price-books", new { sourceVersion = (int?)null })).EnsureOk<PriceBook>();
        (await super.PostAsync($"/api/admin/price-books/{draft.Id}/submit", null)).EnsureSuccessStatusCode();
        var self = await super.PostAsJsonAsync($"/api/admin/price-books/{draft.Id}/approve", new { effectiveFrom = (DateTime?)null, warningsAcknowledged = true });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("SELF_APPROVAL", await self.ErrorCode());
    }

    [Fact]
    public async Task Submit_blocked_by_errors_and_approve_needs_warning_ack()
    {
        var maker = await Admin("mkt.b", AdminRoles.Marketing);
        var checker = await SuperAdmin();
        var draft = await (await maker.PostAsJsonAsync("/api/admin/price-books", new { sourceVersion = (int?)null })).EnsureOk<PriceBook>();

        draft.TopUpPackages[0].BonusXu = 40; // vượt 50% Xu nạp
        await maker.PutAsJsonAsync($"/api/admin/price-books/{draft.Id}", draft, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.PostAsync($"/api/admin/price-books/{draft.Id}/submit", null)).StatusCode);

        draft.TopUpPackages[0].BonusXu = 0;
        draft.TopUpPackages.Single(p => p.Code == "RUNG").BonusXu = 10; // gói lớn thưởng ít hơn gói nhỏ: cảnh báo
        await maker.PutAsJsonAsync($"/api/admin/price-books/{draft.Id}", draft, Json);
        (await maker.PostAsync($"/api/admin/price-books/{draft.Id}/submit", null)).EnsureSuccessStatusCode();

        var noAck = await checker.PostAsJsonAsync($"/api/admin/price-books/{draft.Id}/approve", new { effectiveFrom = (DateTime?)null, warningsAcknowledged = false });
        Assert.Equal("WARNINGS_NOT_ACKNOWLEDGED", await noAck.ErrorCode());
        (await checker.PostAsJsonAsync($"/api/admin/price-books/{draft.Id}/approve", new { effectiveFrom = (DateTime?)null, warningsAcknowledged = true })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Restore_old_version_creates_new_draft_and_actions_are_audited()
    {
        var maker = await Admin("mkt.d", AdminRoles.Marketing);
        var draft = await (await maker.PostAsJsonAsync("/api/admin/price-books", new { sourceVersion = 1 })).EnsureOk<PriceBook>();
        Assert.Equal(PriceBookStatus.Draft, draft.Status);
        Assert.Equal("Khôi phục từ v1", draft.ChangeNote);

        var logs = await (await (await SuperAdmin()).GetAsync($"/api/admin/audit?targetType=priceBook&targetId={draft.Id}")).EnsureOk();
        Assert.Contains(logs.EnumerateArray(), l => l.GetProperty("action").GetString() == "pricing.create_draft"
            && l.GetProperty("actorName").GetString() == "mkt.d");
    }
}
