using MongoDB.Bson;

namespace ChamXanh.Api.Modules.Escrow;

public class EscrowOptions
{
    public string Provider { get; set; } = "sandbox";
    public string CheckoutBaseUrl { get; set; } = "https://sandbox.pay.local/checkout";
    public int BuyerPaymentMinutes { get; set; } = 60;       // BR-ESC-11
    public int SellerPaymentHours { get; set; } = 24;        // BR-ESC-11
    public int SellerConfirmHours { get; set; } = 24;        // BR-ESC-10
    public int InspectionHours { get; set; } = 48;           // BR-ESC-13
    public int SellerDisputeResponseHours { get; set; } = 48; // BR-DSP-02
    public int PayoutDelayDays { get; set; } = 1;            // BR-ESC-15
    public long ReturnRequiredMinVnd { get; set; } = 5_000_000; // BR-DSP-04
}

public record PaymentSession(string GatewayRef, string CheckoutUrl, string? QrContent, DateTime ExpiresAt);

/// <summary>Cổng đối tác trung gian thanh toán. Thay bản sandbox bằng adapter đối tác thật khi ký hợp đồng,
/// luồng nghiệp vụ không đổi.</summary>
public interface IEscrowGateway
{
    Task<PaymentSession> CreatePaymentAsync(EscrowOrder order, long amount, CancellationToken ct);
    Task<string> RefundAsync(EscrowOrder order, long amount, string reason, CancellationToken ct);
    Task<string> PayoutAsync(EscrowOrder order, long amount, string bankCode, string accountNo, string accountName, CancellationToken ct);
}

/// <summary>Sandbox: không chuyển tiền thật. Thanh toán được giả lập bằng webhook ký HMAC (giống nạp Xu).</summary>
public class SandboxEscrowGateway(EscrowOptions options, ILogger<SandboxEscrowGateway> logger) : IEscrowGateway
{
    public Task<PaymentSession> CreatePaymentAsync(EscrowOrder order, long amount, CancellationToken ct)
    {
        var gatewayRef = $"SBX-{ObjectId.GenerateNewId()}";
        return Task.FromResult(new PaymentSession(gatewayRef, $"{options.CheckoutBaseUrl}/{order.Id}?amount={amount}",
            $"CXDB{order.Code}", order.PaymentDueAt ?? DateTime.UtcNow.AddHours(1)));
    }

    public Task<string> RefundAsync(EscrowOrder order, long amount, string reason, CancellationToken ct)
    {
        logger.LogInformation("[sandbox] Hoàn {Amount}đ đơn {Code}: {Reason}", amount, order.Code, reason);
        return Task.FromResult($"SBX-RF-{ObjectId.GenerateNewId()}");
    }

    public Task<string> PayoutAsync(EscrowOrder order, long amount, string bankCode, string accountNo, string accountName, CancellationToken ct)
    {
        logger.LogInformation("[sandbox] Chi {Amount}đ đơn {Code} tới {Bank}", amount, order.Code, bankCode);
        return Task.FromResult($"SBX-PO-{ObjectId.GenerateNewId()}");
    }
}
