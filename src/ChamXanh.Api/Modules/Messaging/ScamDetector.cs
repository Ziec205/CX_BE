using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Listings;

namespace ChamXanh.Api.Modules.Messaging;

/// <summary>BR-CHAT-01: phát hiện mẫu lừa đảo phổ biến và trả về cảnh báo cho người nhận.</summary>
public static class ScamDetector
{
    static readonly string[] DepositTerms = ["chuyen coc", "dat coc", "chuyen khoan truoc", "ck truoc", "coc truoc", "chuyen tien truoc", "thanh toan truoc"];
    static readonly string[] OtpTerms = ["ma otp", "otp", "ma xac nhan", "ma xac thuc"];
    static readonly string[] OffPlatformTerms = ["zalo", "messenger", "telegram", "ib qua", "inbox qua", "nhan tin qua"];

    public static string? Check(string? text, bool listingHasEscrow, bool senderIsNewAccount, bool isFirstMessageFromSender)
    {
        var norm = " " + VietnameseText.Normalize(text) + " ";
        if (norm.Trim().Length == 0) return null;
        if (OtpTerms.Any(t => norm.Contains($" {t} ")))
            return "Cảnh báo: Chạm Xanh không bao giờ yêu cầu bạn cung cấp mã OTP. Tuyệt đối không gửi mã OTP cho bất kỳ ai.";
        if (ListingRules.ContainsContactInfo(text ?? "") && (text ?? "").Contains("http", StringComparison.OrdinalIgnoreCase))
            return "Cảnh báo: tin nhắn có đường link. Không đăng nhập hay nhập thông tin cá nhân vào trang lạ.";
        if (!listingHasEscrow && DepositTerms.Any(t => norm.Contains($" {t} ")))
            return "Cảnh báo: đừng chuyển cọc khi chưa xem cây. Hãy xem cây trực tiếp hoặc mua tin có nhãn \"Giao dịch đảm bảo\".";
        if (senderIsNewAccount && isFirstMessageFromSender && OffPlatformTerms.Any(t => norm.Contains($" {t} ")))
            return "Lưu ý: tài khoản mới đề nghị chuyển sang ứng dụng khác ngay từ đầu. Hãy trao đổi trong Chạm Xanh để được bảo vệ.";
        return null;
    }
}
