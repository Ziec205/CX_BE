namespace ChamXanh.Api.Common;

/// <summary>Lỗi nghiệp vụ có mã ổn định để client xử lý. HTTP status mặc định 422.</summary>
public class DomainException(string code, string message, int status = StatusCodes.Status422UnprocessableEntity, object? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public object? Details { get; } = details;

    public static DomainException NotFound(string what) => new("NOT_FOUND", $"Không tìm thấy {what}", StatusCodes.Status404NotFound);
    public static DomainException Forbidden(string message) => new("FORBIDDEN", message, StatusCodes.Status403Forbidden);
    public static DomainException Conflict(string code, string message, object? details = null) => new(code, message, StatusCodes.Status409Conflict, details);
    public static DomainException TooMany(string message) => new("RATE_LIMITED", message, StatusCodes.Status429TooManyRequests);
}
