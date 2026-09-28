using System.Net;
using System.Net.Http.Json;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Modules.Identity;
using Microsoft.AspNetCore.Hosting;

namespace ChamXanh.Api.Tests;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("0912345678", "+84912345678")]
    [InlineData("+84 912 345 678", "+84912345678")]
    [InlineData("84-3-8765-4321", "+84387654321")]
    public void Normalizes_vn_mobile(string raw, string expected) => Assert.Equal(expected, PhoneNumber.Normalize(raw));

    [Theory]
    [InlineData("0212345678")]
    [InlineData("12345")]
    [InlineData("09123456789")]
    public void Rejects_invalid(string raw) => Assert.ThrowsAny<Exception>(() => PhoneNumber.Normalize(raw));
}

[Collection(ApiCollection.Name)]
public class IdentityApiTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    [Fact]
    public async Task Otp_login_creates_user_once_and_profile_can_be_updated()
    {
        var anon = Anonymous();
        var first = await (await anon.PostAsJsonAsync("/api/auth/otp/request", new { phone = "0901234567" })).EnsureOk();
        var verify = await (await anon.PostAsJsonAsync("/api/auth/otp/verify",
            new { phone = "+84901234567", code = first.GetProperty("devCode").GetString() })).EnsureOk();
        Assert.True(verify.GetProperty("isNew").GetBoolean());
        Assert.False(verify.GetProperty("user").GetProperty("canPost").GetBoolean());

        var again = await (await anon.PostAsJsonAsync("/api/auth/otp/request", new { phone = "0901234567" })).EnsureOk();
        var second = await (await anon.PostAsJsonAsync("/api/auth/otp/verify",
            new { phone = "0901234567", code = again.GetProperty("devCode").GetString() })).EnsureOk();
        Assert.False(second.GetProperty("isNew").GetBoolean());
        Assert.Equal(verify.GetProperty("user").GetProperty("id").GetString(), second.GetProperty("user").GetProperty("id").GetString());

        var client = WithToken(second.GetProperty("tokens").GetProperty("accessToken").GetString()!);
        var me = await (await client.PutAsJsonAsync("/api/me", new { fullName = "Trần Thị Lan", provinceId = "79", displayName = "Lan Cây Cảnh" })).EnsureOk();
        Assert.True(me.GetProperty("canPost").GetBoolean());
        Assert.Equal("Lan Cây Cảnh", me.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Wrong_code_rejected_and_code_is_single_use()
    {
        var anon = Anonymous();
        var req = await (await anon.PostAsJsonAsync("/api/auth/otp/request", new { phone = "0931111111" })).EnsureOk();
        var code = req.GetProperty("devCode").GetString();
        var wrong = await anon.PostAsJsonAsync("/api/auth/otp/verify", new { phone = "0931111111", code = code == "000000" ? "111111" : "000000" });
        Assert.Equal("OTP_INVALID", await wrong.ErrorCode());
        (await anon.PostAsJsonAsync("/api/auth/otp/verify", new { phone = "0931111111", code })).EnsureSuccessStatusCode();
        var reuse = await anon.PostAsJsonAsync("/api/auth/otp/verify", new { phone = "0931111111", code });
        Assert.Equal("OTP_EXPIRED", await reuse.ErrorCode());
    }

    [Fact]
    public async Task Otp_rate_limited_per_phone()
    {
        var anon = Anonymous();
        for (var i = 0; i < 5; i++) (await anon.PostAsJsonAsync("/api/auth/otp/request", new { phone = "0942222222" })).EnsureSuccessStatusCode();
        var sixth = await anon.PostAsJsonAsync("/api/auth/otp/request", new { phone = "0942222222" });
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }

    [Fact]
    public async Task Invalid_phone_is_bad_request()
    {
        var res = await Anonymous().PostAsJsonAsync("/api/auth/otp/request", new { phone = "12345" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_token_rotates_and_cannot_be_reused()
    {
        var anon = Anonymous();
        var req = await (await anon.PostAsJsonAsync("/api/auth/otp/request", new { phone = "0953333333" })).EnsureOk();
        var auth = await (await anon.PostAsJsonAsync("/api/auth/otp/verify", new { phone = "0953333333", code = req.GetProperty("devCode").GetString() })).EnsureOk();
        var refresh = auth.GetProperty("tokens").GetProperty("refreshToken").GetString();

        var rotated = await (await anon.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh })).EnsureOk();
        Assert.NotEqual(refresh, rotated.GetProperty("refreshToken").GetString());
        var reused = await anon.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task Admin_wrong_password_rejected_and_member_token_cannot_reach_admin()
    {
        var res = await Anonymous().PostAsJsonAsync("/api/admin/auth/login", new { username = "superadmin", password = "sai-mat-khau" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var (member, _) = await Member();
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/admin/users")).StatusCode);
        var me = await (await (await SuperAdmin()).GetAsync("/api/admin/auth/me")).EnsureOk();
        Assert.Contains(me.GetProperty("permissions").EnumerateArray(), p => p.GetString() == Perm.AdminManage);
    }
}

// Môi trường bắt buộc 2FA (như production): admin chưa bật 2FA không có quyền nào cho tới khi bật.
[Collection(ApiCollection.Name)]
public class AdminTwoFactorTests(MongoFixture mongo) : ApiTestBase(mongo)
{
    protected override void ConfigureSettings(IWebHostBuilder b) => b.UseSetting("Admin:Require2fa", "true");

    [Fact]
    public async Task Admin_without_2fa_has_no_permissions_until_enabled()
    {
        var super = await SuperAdmin();
        Assert.Equal(HttpStatusCode.Forbidden, (await super.GetAsync("/api/admin/price-books")).StatusCode);

        var setup = await (await super.PostAsync("/api/admin/auth/2fa/setup", null)).EnsureOk();
        var secret = setup.GetProperty("secret").GetString()!;
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();
        (await super.PostAsJsonAsync("/api/admin/auth/2fa/enable", new { code })).EnsureSuccessStatusCode();

        var noCode = await Anonymous().PostAsJsonAsync("/api/admin/auth/login", new { username = "superadmin", password = "dev-superadmin-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);

        var login = await (await Anonymous().PostAsJsonAsync("/api/admin/auth/login",
            new { username = "superadmin", password = "dev-superadmin-password", totpCode = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp() })).EnsureOk();
        var withMfa = WithToken(login.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await withMfa.GetAsync("/api/admin/price-books")).StatusCode);
    }
}
