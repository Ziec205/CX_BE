using System.Net;
using System.Text;
using ChamXanh.Api.Modules.Plans;

namespace ChamXanh.Api.Tests;

public class PayOsGatewayTests
{
    class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    static PaymentLinkRequest Req() => new(123, 49000, "CXPLUS1", "Plus 1 tháng", "https://x/ok", "https://x/huy", DateTime.UtcNow.AddMinutes(15), "https://x");

    [Fact]
    public async Task Create_returns_qr_code_from_payos()
    {
        var json = """{"code":"00","desc":"success","data":{"paymentLinkId":"abc","checkoutUrl":"https://pay.payos.vn/web/abc","qrCode":"00020101021238570010A000000727"}}""";
        var gw = new PayOsGateway(new HttpClient(new StubHandler(json)), new PayOsOptions { ClientId = "c", ApiKey = "k", ChecksumKey = "s" });
        var link = await gw.CreateAsync(Req(), CancellationToken.None);
        Assert.Equal(("abc", "https://pay.payos.vn/web/abc", "00020101021238570010A000000727"), (link.PaymentLinkId, link.CheckoutUrl, link.QrCode));
    }

    [Fact]
    public async Task Create_without_qr_code_still_works()
    {
        var json = """{"code":"00","desc":"success","data":{"paymentLinkId":"abc","checkoutUrl":"https://pay.payos.vn/web/abc"}}""";
        var gw = new PayOsGateway(new HttpClient(new StubHandler(json)), new PayOsOptions { ClientId = "c", ApiKey = "k", ChecksumKey = "s" });
        Assert.Null((await gw.CreateAsync(Req(), CancellationToken.None)).QrCode);
    }

    [Fact]
    public void Simulator_qr_is_valid_emv_string()
    {
        var qr = SimulatedPlanGateway.FakeVietQr(Req());
        Assert.StartsWith("000201", qr);
        Assert.Contains("5405" + "49000", qr);
        Assert.Matches("6304[0-9A-F]{4}$", qr);
    }
}
