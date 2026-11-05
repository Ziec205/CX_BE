# Cổng MCP cho Claude

Cổng `POST /mcp` cho Claude làm các việc quản trị của Chạm Xanh bằng token riêng, tách khỏi mật khẩu admin.

> **Cảnh báo:** bài viết, bình luận và tin nhắn do người dùng viết có thể chứa chỉ thị độc hại nhắm vào AI. Token toàn quyền có thể chạm tới ví, cấm tài khoản và tạo admin. Chỉ dùng token toàn quyền khi thật sự cần, đặt hạn ngắn và thu hồi ngay khi xong. Việc thường ngày nên dùng token chỉ có `content.manage` và `reports.view`.

## Tạo token

Token chỉ tạo được bằng phiên đăng nhập admin có quyền `admin.manage`, phải chọn quyền tường minh:

```
POST /api/admin/agent-tokens
{ "name": "claude-noi-dung", "days": 7, "permissions": ["content.manage", "reports.view"] }
```

Để cấp toàn quyền, thay `permissions` bằng `"allPermissions": true`. Hạn dùng từ 1 đến 365 ngày, mặc định 90.

Máy chủ xác thực bằng SHA-256 của token và lưu thêm bản mã hóa để admin xem lại bằng nút con mắt. Mỗi lần xem cần quyền `admin.manage` và được ghi audit; thu hồi sẽ xóa bản mã hóa.

## Kết nối Claude Code

```bash
claude mcp add --transport http chamxanh https://cx-be.onrender.com/mcp --header "Authorization: Bearer <token>"
```

## Công cụ

| Công cụ | Quyền | Việc làm |
|---|---|---|
| `admin_api_list` | Mọi token | Liệt kê route `/api/admin/*` và quyền cần có |
| `admin_api_call` | Theo quyền của route | Gọi một route admin qua đúng phân quyền và audit |
| `explore_list`, `explore_get` | `content.manage` | Xem bài Khám phá |
| `explore_save` | `content.manage` | Tạo bài nháp hoặc sửa bài |
| `explore_set_status` | `content.manage` | Đăng, chuyển về nháp hoặc lưu trữ bài |
| `media_upload` | `content.manage` | Tải ảnh base64, tối đa 15MB |
| `species_search` | `content.manage` | Tìm loài để gắn vào bài |
| `community_list` | `content.manage` | Xem bài Cộng đồng |
| `community_moderate` | `content.manage` | Ẩn hoặc hiện lại bài, bắt buộc có lý do |
| `audit_recent` | `reports.view` | Xem nhật ký quản trị |

Token cũng dùng trực tiếp được trên `/api/admin/*` bằng header `Authorization: Bearer`.

## Giới hạn

- Token bị chặn ở `/api/admin/agent-tokens` và `/api/admin/auth`, nên không tự cấp, gia hạn token hay đổi 2FA được.
- Token không dùng được ngoài `/api/admin/*` và `/mcp`.
- Thiếu quyền của route thì nhận 403, giống admin thật.
- Bài tạo bằng `explore_save` luôn là bản nháp.
- Thao tác ghi vào nhật ký với tên `agent:<tên token>`, ở những route vốn có ghi audit.
- Token toàn quyền vẫn gọi được `POST /api/admin/users` để tạo admin mới, và tài khoản đó tồn tại sau khi thu hồi token. Khi thu hồi một token toàn quyền, hãy kiểm tra lại danh sách admin.

## Thu hồi

```
GET /api/admin/agent-tokens
DELETE /api/admin/agent-tokens/{id}
```

Thu hồi có hiệu lực ngay ở yêu cầu kế tiếp. Nếu token bị lộ, hãy thu hồi rồi tạo token mới.
