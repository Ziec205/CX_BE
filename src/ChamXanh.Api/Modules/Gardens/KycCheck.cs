using ChamXanh.Api.Modules.Media;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Gardens;

public record KycInput(string IdNumber, string FullName, DateOnly DateOfBirth, IReadOnlyList<byte[]> DocumentImages);
public record KycResult(bool Passed, double? Score, string? Reason);

/// <summary>Adapter eKYC (VNPT eKYC, FPT.AI...). Mặc định "manual": không gọi ra ngoài, hồ sơ chờ duyệt tay (UC-ADM-05).</summary>
public interface IKycVerifier
{
    string Name { get; }
    Task<KycResult?> VerifyAsync(KycInput input, CancellationToken ct);
}

public class ManualKycVerifier : IKycVerifier
{
    public string Name => "manual";
    public Task<KycResult?> VerifyAsync(KycInput input, CancellationToken ct) => Task.FromResult<KycResult?>(null);
}

public class KycOptions
{
    /// <summary>Tự duyệt khi eKYC đạt. Mặc định tắt: người duyệt vẫn xem ảnh vườn/định vị trước khi cấp tick.</summary>
    public bool AutoApprove { get; set; }
}

public class KycCheckService(IKycVerifier verifier, KycOptions options, GardenService gardens, MediaService media, TimeProvider clock,
    ILogger<KycCheckService> logger)
{
    public async Task RunAsync(GardenProfile g, GardenApplication a, CancellationToken ct)
    {
        if (verifier is ManualKycVerifier) return;
        try
        {
            var images = new List<byte[]>();
            foreach (var id in g.Kyc.DocumentMediaIds)
                if (await media.FindAsync(id, ct) is { } item && await media.OpenAsync(item, "full", ct) is { } file)
                {
                    await using var s = file.Stream;
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms, ct);
                    images.Add(ms.ToArray());
                }
            var result = await verifier.VerifyAsync(new KycInput(a.IdNumber!, a.FullName, a.DateOfBirth, images), ct);
            if (result is null) return;
            await gardens.Gardens.UpdateOneAsync(x => x.Id == g.Id, Builders<GardenProfile>.Update
                .Set(x => x.Kyc.AutoCheck, new KycAutoCheck { Provider = verifier.Name, Passed = result.Passed, Score = result.Score, Reason = result.Reason, CheckedAt = clock.GetUtcNow().UtcDateTime })
                .Set(x => x.Kyc.Provider, verifier.Name), cancellationToken: ct);
            if (result.Passed && options.AutoApprove)
                await gardens.ReviewAsync(g.Id, new ReviewRequest(ReviewDecision.Approve, $"eKYC {verifier.Name} tự duyệt"), "system", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // eKYC lỗi không chặn nộp hồ sơ: vẫn vào hàng chờ duyệt tay.
            logger.LogWarning(ex, "eKYC lỗi cho hồ sơ {Id}", g.Id);
        }
    }
}
