using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChamXanh.Api.Tests;

/// <summary>Các lỗi logic tìm được khi rà Cộng đồng, Khám phá, Hồ sơ vườn.</summary>
[Collection(ApiCollection.Name)]
public class LogicRegressionTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    async Task<string> Question(HttpClient c) =>
        (await (await c.PostAsJsonAsync("/api/community/posts", new { type = "Question", title = "Cây bị vàng lá phải làm sao", body = "Lá vàng dần từ gốc lên ngọn" })).EnsureOk())
        .GetProperty("id").GetString()!;

    async Task<string> Comment(HttpClient c, string postId, string body = "Giảm tưới nước") =>
        (await (await c.PostAsJsonAsync($"/api/community/posts/{postId}/comments", new { body })).EnsureOk()).GetProperty("id").GetString()!;

    async Task<JsonElement> Post(string id) =>
        (await (await Anonymous().GetAsync($"/api/community/posts/{id}")).EnsureOk()).GetProperty("post");

    [Fact]
    public async Task Deleting_best_answer_clears_it_so_asker_can_choose_again()
    {
        var (asker, _) = await Member();
        var (other, _) = await Member();
        var postId = await Question(asker);
        var first = await Comment(other, postId);
        var second = await Comment(other, postId, "Thay đất mới");
        (await asker.PostAsync($"/api/community/posts/{postId}/best/{first}", null)).EnsureSuccessStatusCode();

        (await other.DeleteAsync($"/api/community/comments/{first}")).EnsureSuccessStatusCode();

        Assert.Equal(JsonValueKind.Null, (await Post(postId)).GetProperty("bestCommentId").ValueKind);
        (await asker.PostAsync($"/api/community/posts/{postId}/best/{second}", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Comment_count_follows_auto_hide_and_moderation()
    {
        var (asker, _) = await Member();
        var postId = await Question(asker);
        var commentId = await Comment(asker, postId);
        Assert.Equal(1, (await Post(postId)).GetProperty("comments").GetInt32());

        for (var i = 0; i < 3; i++)
        {
            var (reporter, _) = await Member();
            (await reporter.PostAsJsonAsync($"/api/community/comments/{commentId}/report", new { reason = "spam" })).EnsureSuccessStatusCode();
        }
        Assert.Equal(0, (await Post(postId)).GetProperty("comments").GetInt32());

        var admin = await SuperAdmin();
        (await admin.PostAsJsonAsync($"/api/admin/community/comments/{commentId}/moderate", new { status = "Visible" })).EnsureSuccessStatusCode();
        Assert.Equal(1, (await Post(postId)).GetProperty("comments").GetInt32());
        (await admin.PostAsJsonAsync($"/api/admin/community/comments/{commentId}/moderate", new { status = "Removed" })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Post(postId)).GetProperty("comments").GetInt32());
    }

    [Theory]
    [InlineData("GET", "/api/community/users/khong-hop-le")]
    [InlineData("GET", "/api/me/garden/plants/khong-hop-le")]
    [InlineData("PUT", "/api/community/comments/khong-hop-le/like")]
    [InlineData("DELETE", "/api/community/comments/khong-hop-le")]
    [InlineData("DELETE", "/api/community/posts/khong-hop-le")]
    [InlineData("POST", "/api/me/garden/plants/khong-hop-le/reminders/khong-hop-le/done")]
    public async Task Malformed_id_is_not_found_rather_than_server_error(string method, string url)
    {
        var (c, _) = await Member();
        var res = await c.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/explore?page=2147483647&pageSize=50")]
    [InlineData("/api/community/posts?page=2147483647")]
    public async Task Huge_page_number_returns_empty_page(string url)
    {
        var body = await (await Anonymous().GetAsync(url)).EnsureOk();
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Showcase_cannot_lose_all_photos_on_edit()
    {
        var (c, _) = await Member();
        var form = new MultipartFormDataContent { { new ByteArrayContent(TestImages.Jpeg()), "file", "a.jpg" }, { new StringContent("ListingPhoto"), "kind" } };
        var media = (await (await c.PostAsync("/api/media", form)).EnsureOk()).GetProperty("id").GetString()!;
        var id = (await (await c.PostAsJsonAsync("/api/community/posts", new { type = "Showcase", title = "Khoe chậu sen đá", body = "Chậu sen đá mới của mình", mediaIds = new[] { media } })).EnsureOk())
            .GetProperty("id").GetString()!;

        var res = await c.PutAsJsonAsync($"/api/community/posts/{id}", new { type = "Showcase", title = "Khoe chậu sen đá", body = "Chậu sen đá mới của mình", mediaIds = Array.Empty<string>() });

        Assert.Equal("PHOTO_REQUIRED", await res.ErrorCode());
    }
}
