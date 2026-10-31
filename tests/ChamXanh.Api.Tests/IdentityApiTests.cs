using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    async Task<JsonElement> Register(string username, string ip, string password = "mat-khau-dai-1")
    {
        var anon = Anonymous();
        anon.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return await (await anon.PostAsJsonAsync("/api/auth/register", new { username, password, confirmPassword = password })).EnsureOk();
    }

    [Fact]
    public async Task Register_then_login_with_password_and_profile_can_be_updated()
    {
        var reg = await Register("lan.caycanh", "10.9.0.1");
        Assert.True(reg.GetProperty("isNew").GetBoolean());
        Assert.False(reg.GetProperty("user").GetProperty("canPost").GetBoolean());

        var login = await (await Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "LAN.CayCanh", password = "mat-khau-dai-1" })).EnsureOk();
        Assert.False(login.GetProperty("isNew").GetBoolean());
        Assert.Equal(reg.GetProperty("user").GetProperty("id").GetString(), login.GetProperty("user").GetProperty("id").GetString());

        var client = WithToken(login.GetProperty("tokens").GetProperty("accessToken").GetString()!);
        var me = await (await client.PutAsJsonAsync("/api/me", new { fullName = "Trần Thị Lan", provinceId = "79", displayName = "Lan Cây Cảnh" })).EnsureOk();
        Assert.True(me.GetProperty("canPost").GetBoolean());
        Assert.Equal("Lan Cây Cảnh", me.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Phone_login_endpoints_are_gone_only_user_and_admin_sign_in()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().PostAsJsonAsync("/api/auth/otp/request", new { phone = "0901234567" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous().PostAsJsonAsync("/api/auth/otp/verify", new { phone = "0901234567", code = "000000" })).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_login_for_fifteen_minutes()
    {
        await Register("khoa.sai.mk", "10.9.0.2");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "khoa.sai.mk", password = "sai-roi" })).StatusCode);
        var right = await Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "khoa.sai.mk", password = "mat-khau-dai-1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    [Fact]
    public async Task Phone_verification_wrong_code_single_use_and_rate_limit()
    {
        var reg = await Register("xac.minh.sdt", "10.9.0.3");
        var c = WithToken(reg.GetProperty("tokens").GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/me/phone/request", new { phone = "12345" })).StatusCode);

        var code = (await (await c.PostAsJsonAsync("/api/me/phone/request", new { phone = "0931111111" })).EnsureOk()).GetProperty("devCode").GetString();
        Assert.Equal("OTP_INVALID", await (await c.PostAsJsonAsync("/api/me/phone/verify", new { phone = "0931111111", code = code == "000000" ? "111111" : "000000" })).ErrorCode());
        var me = await (await c.PostAsJsonAsync("/api/me/phone/verify", new { phone = "0931111111", code })).EnsureOk();
        Assert.Equal("+84931111111", me.GetProperty("phone").GetString());
        Assert.Equal("OTP_EXPIRED", await (await c.PostAsJsonAsync("/api/me/phone/verify", new { phone = "0931111111", code })).ErrorCode());

        // Tối đa 5 mã mỗi giờ cho một số điện thoại.
        for (var i = 0; i < 4; i++) (await c.PostAsJsonAsync("/api/me/phone/request", new { phone = "0942222222" })).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/me/phone/request", new { phone = "0942222222" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/me/phone/request", new { phone = "0942222222" })).StatusCode);
    }

    [Fact]
    public async Task Refresh_token_rotates_and_cannot_be_reused()
    {
        var auth = await Register("xoay.token", "10.9.0.4");
        var refresh = auth.GetProperty("tokens").GetProperty("refreshToken").GetString();
        var anon = Anonymous();
        var rotated = await (await anon.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh })).EnsureOk();
        Assert.NotEqual(refresh, rotated.GetProperty("refreshToken").GetString());
        var reused = await anon.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task Admin_wrong_password_rejected_and_member_token_cannot_reach_admin()
    {
        var res = await Anonymous().PostAsJsonAsync("/api/admin/auth/login", new { username = "admin", password = "sai-mat-khau" });
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

        var noCode = await Anonymous().PostAsJsonAsync("/api/admin/auth/login", new { username = "admin", password = "123" });
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);

        var login = await (await Anonymous().PostAsJsonAsync("/api/admin/auth/login",
            new { username = "admin", password = "123", totpCode = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp() })).EnsureOk();
        var withMfa = WithToken(login.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await withMfa.GetAsync("/api/admin/price-books")).StatusCode);
    }
}
