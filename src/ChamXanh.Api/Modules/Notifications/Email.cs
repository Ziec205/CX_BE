using System.Net;
using System.Net.Mail;
using System.Text;

namespace ChamXanh.Api.Modules.Notifications;

/// <summary>SMTP gửi email nhắc lịch. Khóa đặt qua biến môi trường (Render): Email__SmtpHost, Email__Username, Email__Password, Email__From.
/// Gmail: SmtpHost smtp.gmail.com, cổng 587, Password là "mật khẩu ứng dụng" (App Password), không phải mật khẩu Gmail.</summary>
public class EmailOptions
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string FromName { get; set; } = "Chạm Xanh";
    /// <summary>Địa chỉ web để chèn link trong email (vd https://chamxanh.vn).</summary>
    public string WebBaseUrl { get; set; } = "http://localhost:3000";

    public bool IsConfigured => SmtpHost.Length > 0 && From.Length > 0;
}

public record EmailMessage(string To, string Subject, string Text, string Html);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public class SmtpEmailSender(EmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort) { EnableSsl = options.EnableSsl, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (options.Username.Length > 0) client.Credentials = new NetworkCredential(options.Username, options.Password);
        using var mail = new MailMessage
        {
            From = new MailAddress(options.From, options.FromName, Encoding.UTF8),
            Subject = m.Subject, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8,
            Body = m.Text,
        };
        mail.To.Add(m.To);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(m.Html, Encoding.UTF8, "text/html"));
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>Chưa cấu hình SMTP: chỉ ghi log (che bớt địa chỉ email).</summary>
public class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        var at = m.To.IndexOf('@');
        logger.LogInformation("[email] {To}: {Subject}", (at > 1 ? m.To[..2] : "") + "***" + (at >= 0 ? m.To[at..] : ""), m.Subject);
        return Task.CompletedTask;
    }
}
