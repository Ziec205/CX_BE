using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Agent;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace ChamXanh.Api.Tests;

[Collection(ApiCollection.Name)]
public class AgentGatewayTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    const string Body = "Nội dung bài thử đủ dài để qua kiểm tra độ dài năm mươi ký tự của bài viết Khám phá.";

    async Task<(string Id, string Token)> NewToken(object request)
    {
        var res = await (await (await SuperAdmin()).PostAsJsonAsync("/api/admin/agent-tokens", request)).EnsureOk();
        return (res.GetProperty("id").GetString()!, res.GetProperty("token").GetString()!);
    }

    static Task<HttpResponseMessage> Rpc(HttpClient c, string method, object? @params = null) =>
        c.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params });

    /// <summary>Gọi công cụ, trả về (có lỗi, nội dung văn bản).</summary>
    async Task<(bool IsError, string Text)> Call(string token, string tool, object? args = null)
    {
        var res = await (await Rpc(WithToken(token), "tools/call", new { name = tool, arguments = args ?? new { } })).EnsureOk();
        if (res.TryGetProperty("error", out var e)) return (true, e.GetProperty("message").GetString()!);
        var r = res.GetProperty("result");
        return (r.TryGetProperty("isError", out var x) && x.GetBoolean(), r.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(Anonymous(), "tools/list")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(WithToken("cxa_sai"), "tools/list")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken("cxa_sai").GetAsync("/api/admin/audit")).StatusCode);
    }

    [Fact]
    public async Task Revoked_and_expired_tokens_stop_immediately()
    {
        var (id, token) = await NewToken(new { name = "thu hồi", allPermissions = true });
        (await Rpc(WithToken(token), "tools/list")).EnsureSuccessStatusCode();
        (await WithToken(token).GetAsync("/api/admin/audit")).EnsureSuccessStatusCode();
        (await (await SuperAdmin()).DeleteAsync($"/api/admin/agent-tokens/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(WithToken(token), "tools/list")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(token).GetAsync("/api/admin/audit")).StatusCode);

        var (id2, token2) = await NewToken(new { name = "hết hạn", allPermissions = true });
        using var scope = Factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AgentTokenService>();
        await svc.Tokens.UpdateOneAsync(t => t.Id == id2, Builders<AgentToken>.Update.Set(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(WithToken(token2), "tools/list")).StatusCode);
    }

    [Fact]
    public async Task Token_needs_explicit_known_permissions_and_only_admin_manage_can_create()
    {
        var admin = await SuperAdmin();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/admin/agent-tokens", new { name = "không quyền" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/admin/agent-tokens", new { name = "quyền lạ", permissions = new[] { "khong.ton.tai" } })).StatusCode);
        var editor = await Admin("editor.agent", AdminRoles.Editor);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync("/api/admin/agent-tokens", new { name = "editor", allPermissions = true })).StatusCode);
    }

    [Fact]
    public async Task Plain_token_is_not_stored()
    {
        var (id, token) = await NewToken(new { name = "lưu trữ", permissions = new[] { Perm.ReportsView } });
        using var scope = Factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<AgentTokenService>().Tokens.Find(t => t.Id == id).FirstAsync();
        Assert.NotEqual(token, saved.Hash);
        Assert.DoesNotContain(token, saved.ToString() + saved.Hash + saved.Name);
    }

    [Fact]
    public async Task Limited_token_cannot_see_or_call_content_tools()
    {
        var (_, token) = await NewToken(new { name = "báo cáo", permissions = new[] { Perm.ReportsView } });
        var list = await (await Rpc(WithToken(token), "tools/list")).EnsureOk();
        var names = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("audit_recent", names);
        Assert.Contains("admin_api_call", names);
        Assert.DoesNotContain("explore_save", names);
        Assert.True((await Call(token, "explore_save", new { title = "Bài không được tạo", body = Body })).IsError);
    }

    [Fact]
    public async Task Save_creates_draft_publish_is_explicit_and_audit_names_agent()
    {
        var (_, token) = await NewToken(new { name = "claude", permissions = new[] { Perm.ContentManage, Perm.ReportsView } });
        var saved = await Call(token, "explore_save", new { title = "Bài nháp của trợ lý", body = Body });
        Assert.False(saved.IsError, saved.Text);
        var doc = JsonDocument.Parse(saved.Text).RootElement;
        Assert.Equal("Draft", doc.GetProperty("status").GetString());
        var id = doc.GetProperty("id").GetString();

        // Bài chưa có ảnh thì không đăng được.
        Assert.True((await Call(token, "explore_set_status", new { id, status = "Published" })).IsError);
        var archived = await Call(token, "explore_set_status", new { id, status = "Archived" });
        Assert.False(archived.IsError, archived.Text);

        var audit = await Call(token, "audit_recent", new { limit = 10 });
        Assert.Contains("agent:claude", audit.Text);
        Assert.Contains("explore.create", audit.Text);
    }

    [Fact]
    public async Task Community_moderate_refuses_removed()
    {
        var (_, token) = await NewToken(new { name = "kiểm duyệt", permissions = new[] { Perm.ContentManage } });
        var r = await Call(token, "community_moderate", new { id = "000000000000000000000000", status = "Removed", reason = "thử" });
        Assert.True(r.IsError);
        Assert.Contains("ẩn hoặc hiện", r.Text);
    }

    [Fact]
    public async Task Full_token_reaches_admin_routes_through_http_and_tool()
    {
        var (_, token) = await NewToken(new { name = "toàn quyền", allPermissions = true });
        (await WithToken(token).GetAsync("/api/admin/plans/payments")).EnsureSuccessStatusCode();

        var routes = await Call(token, "admin_api_list");
        Assert.Contains("/api/admin/plans/payments", routes.Text);
        Assert.DoesNotContain("agent-tokens", routes.Text);

        var call = await Call(token, "admin_api_call", new { method = "GET", path = "/api/admin/plans/payments", query = "status=Paid" });
        Assert.False(call.IsError, call.Text);
        Assert.Equal(200, JsonDocument.Parse(call.Text).RootElement.GetProperty("status").GetInt32());

        // Thao tác ghi qua pipeline thật: audit mang tên agent.
        var write = await Call(token, "admin_api_call", new
        {
            method = "POST", path = "/api/admin/explore",
            body = new { title = "Bài qua admin_api_call", body = Body, status = "Draft" },
        });
        Assert.Equal(200, JsonDocument.Parse(write.Text).RootElement.GetProperty("status").GetInt32());
        using var scope = Factory.Services.CreateScope();
        var logs = scope.ServiceProvider.GetRequiredService<AuditService>().Logs;
        var log = await logs.Find(l => l.Action == "explore.create").SortByDescending(l => l.At).FirstAsync();
        Assert.Equal("agent:toàn quyền", log.ActorName);
    }

    [Fact]
    public async Task Token_missing_permission_gets_403()
    {
        var (_, token) = await NewToken(new { name = "thiếu quyền", permissions = new[] { Perm.ContentManage } });
        Assert.Equal(HttpStatusCode.Forbidden, (await WithToken(token).GetAsync("/api/admin/plans/payments")).StatusCode);
        var call = await Call(token, "admin_api_call", new { method = "GET", path = "/api/admin/plans/payments" });
        Assert.Equal(403, JsonDocument.Parse(call.Text).RootElement.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("/api/admin/agent-tokens")]
    [InlineData("/API/ADMIN/AGENT-TOKENS")]
    [InlineData("/api/admin/auth/me")]
    public async Task Token_cannot_reach_token_or_auth_routes(string path)
    {
        var (_, token) = await NewToken(new { name = "bị chặn", allPermissions = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(token).GetAsync(path)).StatusCode);
        Assert.True((await Call(token, "admin_api_call", new { method = "GET", path })).IsError);
    }

    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/admin/x/../agent-tokens")]
    [InlineData("/api/admin/%61gent-tokens")]
    public async Task Api_call_rejects_paths_outside_admin(string path)
    {
        var (_, token) = await NewToken(new { name = "đường dẫn", allPermissions = true });
        Assert.True((await Call(token, "admin_api_call", new { method = "GET", path })).IsError);
    }

    [Fact]
    public async Task Agent_token_is_not_accepted_on_member_routes()
    {
        var (_, token) = await NewToken(new { name = "ngoài admin", allPermissions = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(token).GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Names_are_unique_among_active_tokens_and_reveal_stops_after_revoke()
    {
        var name = "trùng " + Guid.NewGuid().ToString("N")[..8];
        var (id, token) = await NewToken(new { name, permissions = new[] { Perm.ContentManage } });
        var admin = await SuperAdmin();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/agent-tokens", new { name = name.ToUpperInvariant(), permissions = new[] { Perm.ContentManage } })).StatusCode);

        var shown = await (await admin.PostAsync($"/api/admin/agent-tokens/{id}/reveal", null)).EnsureOk();
        Assert.Equal(token, shown.GetProperty("token").GetString());

        (await admin.DeleteAsync($"/api/admin/agent-tokens/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/admin/agent-tokens/{id}/reveal", null)).StatusCode);
        (await admin.PostAsJsonAsync("/api/admin/agent-tokens", new { name, permissions = new[] { Perm.ContentManage } })).EnsureSuccessStatusCode();
    }
}
