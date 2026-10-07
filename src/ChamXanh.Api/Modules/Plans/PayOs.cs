using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChamXanh.Api.Common;

namespace ChamXanh.Api.Modules.Plans;

/// <summary>Khóa PayOS chỉ đặt qua biến môi trường (Render): PayOS__ClientId, PayOS__ApiKey, PayOS__ChecksumKey.</summary>
public class PayOsOptions
{
    public string ClientId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ChecksumKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api-merchant.payos.vn";
    /// <summary>Địa chỉ web để PayOS đưa người dùng quay về (vd https://chamxanh.vn).</summary>
    public string ReturnBaseUrl { get; set; } = "http://localhost:3000";
    /// <summary>Khi chưa có khóa: cho phép thanh toán giả lập để thử luồng. Chỉ bật khi phát triển/demo, không bao giờ ở production thật.</summary>
    public bool AllowSimulator { get; set; }
    public int LinkExpiryMinutes { get; set; } = 15;

    public bool IsConfigured => ClientId.Length > 0 && ApiKey.Length > 0 && ChecksumKey.Length > 0;
}

public record PaymentLinkRequest(long OrderCode, long Amount, string Description, string ItemName, string ReturnUrl, string CancelUrl, DateTime ExpiresAt);
public record PaymentLink(string? PaymentLinkId, string CheckoutUrl);
public enum GatewayState { Pending, Paid, Cancelled, Expired }
public record GatewayStatus(GatewayState State, long AmountPaid, string? Reference);

/// <summary>Cổng tạo link thanh toán cho gói. Đổi cổng không sửa nghiệp vụ.</summary>
public interface IPlanPaymentGateway
{
    string Name { get; }
    bool IsSimulator { get; }
    Task<PaymentLink> CreateAsync(PaymentLinkRequest req, CancellationToken ct);
    Task<GatewayStatus?> GetAsync(long orderCode, CancellationToken ct);
    Task CancelAsync(long orderCode, CancellationToken ct);
}

public static class PayOsSignature
{
    public static string Hmac(string key, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    /// <summary>Chữ ký tạo link: các trường xếp theo bảng chữ cái.</summary>
    public static string ForCreate(string key, PaymentLinkRequest r) =>
        Hmac(key, $"amount={r.Amount}&cancelUrl={r.CancelUrl}&description={r.Description}&orderCode={r.OrderCode}&returnUrl={r.ReturnUrl}");

    /// <summary>Chữ ký webhook: mọi khóa của "data" xếp theo bảng chữ cái, nối key=value bằng &amp;; null thành chuỗi rỗng.</summary>
    public static string ForData(string key, JsonElement data)
    {
        var parts = data.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={Value(p.Value)}");
        return Hmac(key, string.Join('&', parts));
    }

    static string Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(),
    };

    public static bool Verify(string key, JsonElement data, string? signature) =>
        signature is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ForData(key, data)), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
}

public class PayOsGateway(HttpClient http, PayOsOptions options) : IPlanPaymentGateway
{
    public string Name => "PAYOS";
    public bool IsSimulator => false;

    HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, options.BaseUrl.TrimEnd('/') + path);
        req.Headers.Add("x-client-id", options.ClientId);
        req.Headers.Add("x-api-key", options.ApiKey);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    static async Task<JsonElement> DataAsync(HttpResponseMessage res, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (!res.IsSuccessStatusCode || root.GetProperty("code").GetString() != "00" || root.GetProperty("data").ValueKind != JsonValueKind.Object)
            throw new DomainException("PAYMENT_GATEWAY_ERROR",
                $"Cổng PayOS báo lỗi: {(root.TryGetProperty("desc", out var d) ? d.GetString() : res.StatusCode.ToString())}", StatusCodes.Status502BadGateway);
        return root.GetProperty("data").Clone();
    }

    public async Task<PaymentLink> CreateAsync(PaymentLinkRequest r, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, "/v2/payment-requests", new
        {
            orderCode = r.OrderCode, amount = r.Amount, description = r.Description,
            items = new[] { new { name = r.ItemName, quantity = 1, price = r.Amount } },
            cancelUrl = r.CancelUrl, returnUrl = r.ReturnUrl,
            expiredAt = new DateTimeOffset(r.ExpiresAt, TimeSpan.Zero).ToUnixTimeSeconds(),
            signature = PayOsSignature.ForCreate(options.ChecksumKey, r),
        });
        using var res = await http.SendAsync(req, ct);
        var data = await DataAsync(res, ct);
        return new(data.GetProperty("paymentLinkId").GetString(), data.GetProperty("checkoutUrl").GetString()!);
    }

    public async Task<GatewayStatus?> GetAsync(long orderCode, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, $"/v2/payment-requests/{orderCode}");
        using var res = await http.SendAsync(req, ct);
        var data = await DataAsync(res, ct);
        var state = data.GetProperty("status").GetString() switch
        {
            "PAID" or "SUCCEEDED" => GatewayState.Paid,
            "CANCELLED" => GatewayState.Cancelled,
            "EXPIRED" => GatewayState.Expired,
            _ => GatewayState.Pending,
        };
        var paid = data.TryGetProperty("amountPaid", out var a) && a.TryGetInt64(out var n) ? n : 0;
        string? reference = null;
        if (data.TryGetProperty("transactions", out var tx) && tx.ValueKind == JsonValueKind.Array && tx.GetArrayLength() > 0
            && tx[0].TryGetProperty("reference", out var rf))
            reference = rf.GetString();
        return new(state, paid, reference);
    }

    public async Task CancelAsync(long orderCode, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, $"/v2/payment-requests/{orderCode}/cancel", new { cancellationReason = "Người dùng hủy" });
        using var res = await http.SendAsync(req, ct);
        await DataAsync(res, ct);
    }
}

/// <summary>Chưa có khóa PayOS: trang web hiện màn hình thanh toán giả lập, người dùng bấm xác nhận để thử luồng.</summary>
public class SimulatedPlanGateway(PayOsOptions options) : IPlanPaymentGateway
{
    public string Name => "SIMULATOR";
    public bool IsSimulator => true;

    public Task<PaymentLink> CreateAsync(PaymentLinkRequest req, CancellationToken ct)
    {
        if (!options.AllowSimulator)
            throw new DomainException("PAYMENT_NOT_CONFIGURED", "Thanh toán chưa được cấu hình, vui lòng thử lại sau", StatusCodes.Status503ServiceUnavailable);
        return Task.FromResult(new PaymentLink(null, $"{options.ReturnBaseUrl.TrimEnd('/')}/goi/gia-lap?ma={req.OrderCode}"));
    }

    public Task<GatewayStatus?> GetAsync(long orderCode, CancellationToken ct) => Task.FromResult<GatewayStatus?>(null);
    public Task CancelAsync(long orderCode, CancellationToken ct) => Task.CompletedTask;
}
