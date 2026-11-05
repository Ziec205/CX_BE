using ChamXanh.Api.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace ChamXanh.Api.Infrastructure;

public class DomainExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        if (ex is BadHttpRequestException bad)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { code = "BAD_REQUEST", message = bad.Message }, ct);
            return true;
        }
        // Mã trong URL không đúng dạng ObjectId: không có bản ghi nào như vậy, không phải lỗi máy chủ.
        if (ex is FormatException { Source: "MongoDB.Bson" })
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { code = "NOT_FOUND", message = "Không tìm thấy dữ liệu" }, ct);
            return true;
        }
        if (ex is not DomainException de) return false;
        http.Response.StatusCode = de.Status;
        await http.Response.WriteAsJsonAsync(new { code = de.Code, message = de.Message, details = de.Details }, ct);
        return true;
    }
}
