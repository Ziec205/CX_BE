using System.Net;
using System.Net.Http.Json;
using ChamXanh.Api.Common.Auth;

namespace ChamXanh.Api.Tests;

[Collection(ApiCollection.Name)]
public class AdminConsoleTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    [Fact]
    public async Task Community_posts_list_requires_permission_and_moderating_missing_post_is_404()
    {
        var mod = await Admin("mod.community", AdminRoles.Moderator);
        Assert.Equal(HttpStatusCode.Forbidden, (await mod.GetAsync("/api/admin/community/posts")).StatusCode);

        var admin = await SuperAdmin();
        var list = await (await admin.GetAsync("/api/admin/community/posts?q=khong-co-bai-nay")).EnsureOk();
        Assert.Equal(0, list.GetProperty("total").GetInt32());

        var res = await admin.PostAsJsonAsync("/api/admin/community/posts/000000000000000000000000/moderate", new { status = "Hidden", reason = "test" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Plan_payments_list_is_admin_only()
    {
        var admin = await SuperAdmin();
        var list = await (await admin.GetAsync("/api/admin/plans/payments?status=Paid&q=khong-co-ai")).EnsureOk();
        Assert.Equal(0, list.GetProperty("total").GetInt32());
        var anon = Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/plans/payments")).StatusCode);
    }
}
