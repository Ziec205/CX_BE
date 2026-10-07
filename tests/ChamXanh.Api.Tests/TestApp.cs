using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChamXanh.Api.Common.Auth;
using EphemeralMongo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ChamXanh.Api.Tests;

/// <summary>Một MongoDB tạm dùng chung cho toàn bộ integration test (tải/khởi động chỉ một lần).</summary>
public class MongoFixture : IAsyncLifetime
{
    public IMongoRunner Runner { get; private set; } = default!;
    public async Task InitializeAsync() => Runner = await MongoRunner.RunAsync(new MongoRunnerOptions { UseSingleNodeReplicaSet = true });
    public Task DisposeAsync() { Runner.Dispose(); return Task.CompletedTask; }
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "api";
}

/// <summary>Mỗi lớp test có một API riêng trên database riêng.</summary>
public abstract class ApiTestBase(MongoFixture mongo) : IAsyncLifetime
{
    protected WebApplicationFactory<Program> Factory { get; private set; } = default!;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    int _phoneSeq;

    public virtual async Task InitializeAsync()
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development"); // OTP trả mã trong response, 2FA không bắt buộc, có bootstrap superadmin
            b.UseSetting("ConnectionStrings:Mongo", mongo.Runner.ConnectionString);
            b.UseSetting("Mongo:Database", $"test_{Guid.NewGuid():N}");
            ConfigureSettings(b);
        });
        // App mở cổng trước rồi mới tạo index/seed: đợi seed xong để test không gặp bảng giá/danh mục trống.
        _ = Factory.Server;
        await Factory.Services.GetRequiredService<ChamXanh.Api.Common.StartupGate>().Ready.WaitAsync(TimeSpan.FromSeconds(60));
    }

    protected virtual void ConfigureSettings(IWebHostBuilder b) { }

    public async Task DisposeAsync() => await Factory.DisposeAsync();

    protected HttpClient Anonymous() => Factory.CreateClient();

    protected HttpClient WithToken(string token)
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>Đăng nhập thành viên bằng OTP. Mặc định khai luôn họ tên + tỉnh để đủ điều kiện đăng tin.</summary>
    protected async Task<(HttpClient Client, string UserId)> Member(string? phone = null, bool completeProfile = true)
    {
        phone ??= $"09{Interlocked.Increment(ref _phoneSeq):D8}";
        var anon = Anonymous();
        var req = await (await anon.PostAsJsonAsync("/api/auth/otp/request", new { phone })).EnsureOk();
        var code = req.GetProperty("devCode").GetString();
        var auth = await (await anon.PostAsJsonAsync("/api/auth/otp/verify", new { phone, code })).EnsureOk();
        var client = WithToken(auth.GetProperty("tokens").GetProperty("accessToken").GetString()!);
        if (completeProfile)
            (await client.PutAsJsonAsync("/api/me", new { fullName = "Nguyễn Văn Test", provinceId = "79" })).EnsureSuccessStatusCode();
        // Email nhận nhắc lịch (bắt buộc trước khi đặt lời nhắc chăm cây).
        (await client.PutAsJsonAsync("/api/me", new { email = $"u{phone}@test.local" })).EnsureSuccessStatusCode();
        return (client, auth.GetProperty("user").GetProperty("id").GetString()!);
    }

    protected Task<HttpClient> SuperAdmin() => AdminLogin("admin", "123");

    protected async Task<HttpClient> AdminLogin(string username, string password)
    {
        var res = await (await Anonymous().PostAsJsonAsync("/api/admin/auth/login", new { username, password })).EnsureOk();
        return WithToken(res.GetProperty("accessToken").GetString()!);
    }

    /// <summary>Tạo admin mới với vai trò cho trước và đăng nhập.</summary>
    protected async Task<HttpClient> Admin(string username, params string[] roles)
    {
        var super = await SuperAdmin();
        var password = "test-password-123456";
        (await super.PostAsJsonAsync("/api/admin/users", new { username, displayName = username, password, roles = roles.Length > 0 ? roles : [AdminRoles.SuperAdmin] }))
            .EnsureSuccessStatusCode();
        return await AdminLogin(username, password);
    }
}

public static class HttpTestExtensions
{
    public static async Task<JsonElement> EnsureOk(this HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{(int)res.StatusCode} {res.RequestMessage?.Method} {res.RequestMessage?.RequestUri}: {body}");
        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<T> EnsureOk<T>(this HttpResponseMessage res)
    {
        var el = await res.EnsureOk();
        return el.Deserialize<T>(ApiTestBase.Json)!;
    }

    public static async Task<string> ErrorCode(this HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!;
    }
}
