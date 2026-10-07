using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChamXanh.Api.Common;
using ChamXanh.Api.Modules.Catalog;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Plans;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ChamXanh.Api.Modules.Ai;

// Trợ lý AI: hỏi đáp cây cối (mọi gói), so sánh tin trong chợ, viết tin và gợi ý giá (gói Pro). Mọi tính năng dùng chung lượt/ngày.

public class AiConversation
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = default!;
    public string Title { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Lịch sử lưu 60 ngày kể từ lần trò chuyện cuối (TTL).</summary>
    public DateTime ExpireAt { get; set; }
}

public class AiMessage
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string ConversationId { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public bool FromUser { get; set; }
    public string Text { get; set; } = "";
    public List<string> MediaIds { get; set; } = [];
    /// <summary>Câu hỏi ngoài chủ đề cây cối: bị từ chối và không tính lượt.</summary>
    public bool OffTopic { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpireAt { get; set; }
}

public record ChatRequest(string? ConversationId, string? Message, List<string>? MediaIds);
public record CompareRequest(List<string> ListingIds);
public record ListingDraftRequest(List<string> MediaIds, string? CategoryId, string? Notes);
public record PriceSuggestRequest(string CategoryId, string? Title, string? Description, Dictionary<string, JsonElement>? Attributes, List<string>? MediaIds);

public class AssistantService(IMongoDatabase db, IAiModel model, AiQuotaService quota, PlanService plans, MediaService media,
    ListingService listings, ListingSearch search, CatalogService catalog, TimeProvider clock, ILogger<AssistantService> logger)
{
    public const int HistoryDays = 60;
    const int MaxMessageLength = 2000, MaxImages = 3, HistoryTurns = 12;

    public IMongoCollection<AiConversation> Conversations { get; } = db.GetCollection<AiConversation>("aiConversations");
    public IMongoCollection<AiMessage> Messages { get; } = db.GetCollection<AiMessage>("aiMessages");

    public async Task EnsureIndexesAsync()
    {
        var ttl = new CreateIndexOptions { ExpireAfter = TimeSpan.Zero };
        await Conversations.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<AiConversation>(Builders<AiConversation>.IndexKeys.Ascending(c => c.UserId).Descending(c => c.UpdatedAt)),
            new CreateIndexModel<AiConversation>(Builders<AiConversation>.IndexKeys.Ascending(c => c.ExpireAt), ttl),
        ]);
        await Messages.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<AiMessage>(Builders<AiMessage>.IndexKeys.Ascending(m => m.ConversationId).Ascending(m => m.CreatedAt)),
            new CreateIndexModel<AiMessage>(Builders<AiMessage>.IndexKeys.Ascending(m => m.ExpireAt), ttl),
        ]);
    }

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---------------- Hỏi đáp ----------------

    const string ChatSystem = """
        Bạn là "Trợ lý Chạm Xanh", trợ lý AI của Chạm Xanh — sàn mua bán, trao đổi cây trồng ở Việt Nam.
        Phạm vi: cây cảnh, bonsai, lan, hoa, cây ăn quả, rau, cây giống; chăm sóc, tưới, bón phân, đất và giá thể, chậu, sâu bệnh;
        nhận biết cây; mua bán, định giá, đóng gói, vận chuyển cây; kinh nghiệm cho nhà vườn bán cây; cách dùng các tính năng của Chạm Xanh
        (Chợ cây, Khám phá, Hồ sơ vườn, Nhắc lịch tưới, Cộng đồng, Giao dịch đảm bảo).
        Câu hỏi nằm ngoài phạm vi trên (lập trình, chính trị, bài tập, giải trí...): đặt onTopic=false, answer là 1–2 câu từ chối nhẹ nhàng và mời hỏi về cây.
        Chào hỏi xã giao hoặc cảm ơn vẫn là onTopic=true.
        Cách trả lời: tiếng Việt, thân thiện, ngắn gọn, thực tế với khí hậu từng miền Việt Nam. Dùng gạch đầu dòng "- " khi liệt kê, **in đậm** ý chính, không dùng bảng, không dùng tiêu đề #.
        Khi xem ảnh cây bệnh: nêu 1–3 khả năng kèm mức chắc chắn, dấu hiệu để tự kiểm tra, cách xử lý an toàn (ưu tiên biện pháp sinh học); bệnh nặng thì khuyên hỏi kỹ sư nông nghiệp.
        Không bịa giá thị trường cụ thể; chỉ nói khoảng giá chung và khuyên xem tin tương tự trên Chợ cây.
        Không khuyên chuyển tiền cọc ngoài sàn, không xin hay tiết lộ thông tin cá nhân, không hỗ trợ mua bán loài bị cấm hoặc cây khai thác từ rừng.
        title: tên ngắn (tối đa 6 từ) cho cuộc trò chuyện, dựa trên câu hỏi.
        """;

    static readonly JsonObject ChatSchema = Obj(new()
    {
        ["onTopic"] = T("BOOLEAN"), ["answer"] = T("STRING"), ["title"] = T("STRING"),
    }, "onTopic", "answer", "title");

    public async Task<object> ChatAsync(string userId, ChatRequest req, CancellationToken ct)
    {
        var text = req.Message?.Trim() ?? "";
        var mediaIds = (req.MediaIds ?? []).Distinct().ToList();
        if (text.Length == 0 && mediaIds.Count == 0) throw new DomainException("EMPTY_MESSAGE", "Hãy nhập câu hỏi hoặc gửi ảnh cây");
        if (text.Length > MaxMessageLength) throw new DomainException("MESSAGE_TOO_LONG", $"Câu hỏi tối đa {MaxMessageLength} ký tự");
        if (mediaIds.Count > MaxImages) throw new DomainException("TOO_MANY_PHOTOS", $"Mỗi câu hỏi gửi tối đa {MaxImages} ảnh");

        AiConversation? conv = null;
        if (req.ConversationId is { Length: > 0 } cid)
            conv = (ObjectId.TryParse(cid, out _) ? await Conversations.Find(c => c.Id == cid && c.UserId == userId).FirstOrDefaultAsync(ct) : null)
                ?? throw DomainException.NotFound("cuộc trò chuyện");
        var images = await ImagesAsync(mediaIds, userId, ct);
        var history = conv is null ? [] : (await Messages.Find(m => m.ConversationId == conv.Id).SortByDescending(m => m.CreatedAt).Limit(HistoryTurns).ToListAsync(ct))
            .OrderBy(m => m.CreatedAt).Select(m => new AiTurn(m.FromUser, m.FromUser && m.MediaIds.Count > 0 ? $"[đã gửi {m.MediaIds.Count} ảnh] {m.Text}" : m.Text)).ToList();
        history.Add(new AiTurn(true, text.Length > 0 ? text : "Cây của tôi trong ảnh bị sao vậy? Cách chăm thế nào?", images));

        var simulated = Json(new
        {
            onTopic = true,
            title = text.Length > 0 ? Shorten(text, 40) : "Hỏi về cây qua ảnh",
            answer = "**Chế độ thử:** Trợ lý AI chưa được nối với Gemini nên đây là câu trả lời mẫu.\n- Khi quản trị đặt khóa `Gemini__ApiKey`, câu trả lời sẽ do AI tạo.\n- Lượt hỏi vẫn được tính để bạn thử hạn mức của gói.",
        });
        var (doc, used) = await RunAsync(userId, new AiPrompt(ChatSystem, history, ChatSchema, 0.5, simulated), ct);
        var onTopic = Bool(doc, "onTopic", true);
        var answer = Str(doc, "answer");
        if (answer.Length == 0) { await quota.RefundAsync(userId, ct); throw Unavailable(); }
        // Câu ngoài chủ đề bị từ chối: trả lại lượt.
        if (!onTopic) { await quota.RefundAsync(userId, ct); used = used with { Used = Math.Max(0, used.Used - 1) }; }

        var now = Now;
        var expire = now.AddDays(HistoryDays);
        if (conv is null)
        {
            var title = Shorten(Str(doc, "title") is { Length: > 0 } t ? t : text.Length > 0 ? text : "Hỏi về cây qua ảnh", 60);
            conv = new AiConversation { UserId = userId, Title = title, CreatedAt = now, UpdatedAt = now, ExpireAt = expire };
            await Conversations.InsertOneAsync(conv, cancellationToken: ct);
        }
        else await Conversations.UpdateOneAsync(c => c.Id == conv.Id, Builders<AiConversation>.Update.Set(c => c.UpdatedAt, now).Set(c => c.ExpireAt, expire), cancellationToken: ct);

        var question = new AiMessage { ConversationId = conv.Id, UserId = userId, FromUser = true, Text = text, MediaIds = mediaIds, OffTopic = !onTopic, CreatedAt = now, ExpireAt = expire };
        var reply = new AiMessage { ConversationId = conv.Id, UserId = userId, Text = answer, OffTopic = !onTopic, CreatedAt = now.AddMilliseconds(1), ExpireAt = expire };
        await Messages.InsertManyAsync([question, reply], cancellationToken: ct);
        // Ảnh hỏi AI giữ cùng thời hạn lịch sử (không bị job dọn ảnh lẻ xóa sau 24 giờ).
        if (mediaIds.Count > 0) await media.AttachAsync(mediaIds, $"ai:{conv.Id}", ct);

        return new { conversation = ConversationDto(conv), question = MessageDto(question), reply = MessageDto(reply), counted = onTopic, quota = QuotaDto(used) };
    }

    public static object ConversationDto(AiConversation c) => new { c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.ExpireAt };
    public static object MessageDto(AiMessage m) => new
    {
        m.Id, role = m.FromUser ? "user" : "assistant", m.Text, m.OffTopic, m.CreatedAt,
        photos = m.MediaIds.Select(id => $"/media/{id}/card.webp"),
    };
    public static object QuotaDto(AiQuota q) => new { used = q.Used, limit = q.Limit, remaining = q.Remaining };

    public async Task DeleteConversationAsync(string userId, string id, CancellationToken ct)
    {
        var res = await Conversations.DeleteOneAsync(c => c.Id == id && c.UserId == userId, ct);
        if (res.DeletedCount == 0) throw DomainException.NotFound("cuộc trò chuyện");
        await Messages.DeleteManyAsync(m => m.ConversationId == id, ct);
        await media.AttachAsync((await media.Items.Find(m => m.AttachedTo == $"ai:{id}").Project(m => m.Id).ToListAsync(ct)), null!, ct);
    }

    // ---------------- So sánh tin trong chợ (Pro) ----------------

    const string CompareSystem = """
        Bạn là chuyên gia cây cảnh giúp người mua trên Chạm Xanh so sánh các tin đang bán. Dữ liệu mỗi tin ở dạng JSON, ảnh đính kèm theo đúng thứ tự tin.
        Đánh giá theo: giá so với kích thước/tình trạng, độ khỏe và dáng cây nhìn từ ảnh, độ đầy đủ của thông tin, độ tin cậy người bán
        (nhà vườn đã xác minh, có Giao dịch đảm bảo, ảnh chụp thật trong app), khoảng cách nếu có.
        Chỉ dựa vào dữ liệu được cung cấp, không bịa. Thiếu thông tin thì nói rõ là thiếu. Tiếng Việt, ngắn gọn.
        recommendedId: id tin nên chọn nhất (hoặc chuỗi rỗng nếu không thể kết luận). valueScore: 1–10 (đáng tiền). verdict: 2–4 câu kết luận.
        tips: 2–4 lưu ý khi hỏi người bán hoặc khi nhận cây.
        """;

    static readonly JsonObject CompareSchema = Obj(new()
    {
        ["verdict"] = T("STRING"), ["recommendedId"] = T("STRING"),
        ["items"] = Arr(Obj(new()
        {
            ["listingId"] = T("STRING"), ["highlights"] = Arr(T("STRING")), ["concerns"] = Arr(T("STRING")), ["valueScore"] = T("INTEGER"),
        }, "listingId", "highlights", "concerns", "valueScore")),
        ["tips"] = Arr(T("STRING")),
    }, "verdict", "recommendedId", "items", "tips");

    public async Task<object> CompareAsync(string userId, CompareRequest req, CancellationToken ct)
    {
        await RequireAsync(userId, p => p.MarketCompare, "So sánh cây bằng AI", ct);
        var ids = (req.ListingIds ?? []).Distinct().ToList();
        if (ids.Count is < 2 or > 4) throw new DomainException("INVALID_COMPARE", "Chọn từ 2 đến 4 tin để so sánh");
        if (ids.Any(id => !ObjectId.TryParse(id, out _))) throw DomainException.NotFound("tin đăng");
        var found = await listings.Listings.Find(l => ids.Contains(l.Id)).ToListAsync(ct);
        var items = ids.Select(id => found.FirstOrDefault(l => l.Id == id && ListingRules.IsPubliclyVisible(l.Status))
            ?? throw new DomainException("LISTING_UNAVAILABLE", "Có tin đã ẩn hoặc không còn bán, hãy bỏ tin đó ra")).ToList();
        var sellers = await search.SellerSummariesAsync(items.Select(l => l.SellerId), ct);
        var facts = new List<object>();
        foreach (var l in items) facts.Add(await FactsAsync(l, sellers[l.SellerId], ct));
        var images = await ListingImagesAsync(items, ct);

        var simulated = Json(new
        {
            verdict = "Chế độ thử: đây là kết quả mẫu vì chưa nối Gemini. Khi có khóa API, AI sẽ đối chiếu giá, kích thước, tình trạng và ảnh của từng tin.",
            recommendedId = items[0].Id,
            items = items.Select(l => new { listingId = l.Id, highlights = new[] { "Có ảnh và giá rõ ràng" }, concerns = new[] { "Kết quả mẫu, chưa phân tích thật" }, valueScore = 7 }),
            tips = new[] { "Hỏi người bán ảnh/video cây hiện tại", "Ưu tiên Giao dịch đảm bảo khi trả trước" },
        });
        var prompt = new AiPrompt(CompareSystem, [new AiTurn(true, "So sánh các tin sau:\n" + Json(facts), images)], CompareSchema, 0.3, simulated);
        var (doc, used) = await RunAsync(userId, prompt, ct);

        var result = doc.RootElement;
        var recommended = Str(doc, "recommendedId");
        var cards = items.Select(l => search.ToCard(l, sellers[l.SellerId], Now)).ToList();
        return new
        {
            verdict = Str(doc, "verdict"),
            recommendedId = ids.Contains(recommended) ? recommended : null,
            items = ids.Select(id => ItemFor(result, id)).ToList(),
            tips = StrArray(result, "tips"),
            listings = cards,
            quota = QuotaDto(used),
        };
    }

    static object ItemFor(JsonElement root, string id)
    {
        if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
                if (it.TryGetProperty("listingId", out var lid) && lid.GetString() == id)
                    return new
                    {
                        listingId = id, highlights = StrArray(it, "highlights"), concerns = StrArray(it, "concerns"),
                        valueScore = it.TryGetProperty("valueScore", out var v) && v.TryGetInt32(out var n) ? Math.Clamp(n, 1, 10) : (int?)null,
                    };
        return new { listingId = id, highlights = new List<string>(), concerns = new List<string>(), valueScore = (int?)null };
    }

    // ---------------- Công cụ nhà vườn (Pro) ----------------

    const string MarketSystem = """
        Bạn là chuyên gia định giá cây trên Chạm Xanh. Người bán muốn biết tin của mình so với các tin tương tự đang bán trên chợ.
        Dữ liệu: tin của người bán, danh sách tin tương tự (JSON) và thống kê giá. Chỉ dựa vào dữ liệu; nếu ít hơn 3 tin tương tự thì nói rõ dữ liệu còn mỏng.
        position: một trong "Thấp hơn chợ", "Ngang chợ", "Cao hơn chợ", "Chưa đủ dữ liệu". summary: 2–4 câu.
        suggestedMin/suggestedMax: khoảng giá bán hợp lý (VND, số nguyên, làm tròn nghìn); 0 nếu không đủ dữ liệu.
        suggestions: 3–5 gợi ý cụ thể để tin hấp dẫn và bán nhanh hơn (ảnh, mô tả, thông tin còn thiếu, giao dịch đảm bảo...).
        """;

    static readonly JsonObject MarketSchema = Obj(new()
    {
        ["position"] = T("STRING"), ["summary"] = T("STRING"), ["suggestedMin"] = T("INTEGER"), ["suggestedMax"] = T("INTEGER"),
        ["suggestions"] = Arr(T("STRING")),
    }, "position", "summary", "suggestedMin", "suggestedMax", "suggestions");

    public async Task<object> MarketCheckAsync(string userId, string listingId, CancellationToken ct)
    {
        await RequireAsync(userId, p => p.SellerAi, "So tin với chợ bằng AI", ct);
        var own = await listings.GetOwnedAsync(listingId, userId, ct);
        var comps = await ComparablesAsync(own.CategoryId, own.SpeciesId, userId, ct);
        var sellers = await search.SellerSummariesAsync(comps.Select(l => l.SellerId).Append(own.SellerId), ct);
        var stats = PriceStats(comps);
        var compFacts = new List<object>();
        foreach (var l in comps) compFacts.Add(await FactsAsync(l, sellers[l.SellerId], ct));
        var input = Json(new { myListing = await FactsAsync(own, sellers[own.SellerId], ct), stats, similar = compFacts });

        var simulated = Json(new
        {
            position = stats is null ? "Chưa đủ dữ liệu" : "Ngang chợ",
            summary = "Chế độ thử: kết quả mẫu vì chưa nối Gemini. Thống kê giá bên dưới là số liệu thật từ các tin tương tự trên chợ.",
            suggestedMin = stats?.Min ?? 0, suggestedMax = stats?.Max ?? 0,
            suggestions = new[] { "Thêm ảnh chụp toàn cây và cận lá", "Ghi rõ chiều cao, tình trạng chậu/bầu", "Bật Giao dịch đảm bảo để người mua yên tâm" },
        });
        var (doc, used) = await RunAsync(userId, new AiPrompt(MarketSystem, [new AiTurn(true, input, await ListingImagesAsync([own], ct))], MarketSchema, 0.3, simulated), ct);
        var now = Now;
        return new
        {
            position = Str(doc, "position"), summary = Str(doc, "summary"),
            suggestedMin = Long(doc, "suggestedMin"), suggestedMax = Long(doc, "suggestedMax"),
            suggestions = StrArray(doc.RootElement, "suggestions"),
            myPrice = own.Price, stats,
            similar = comps.Select(l => search.ToCard(l, sellers[l.SellerId], now)).ToList(),
            quota = QuotaDto(used),
        };
    }

    const string PriceSystem = """
        Bạn là chuyên gia định giá cây trên Chạm Xanh. Gợi ý giá bán cho một tin sắp đăng dựa vào thông tin người bán nhập, ảnh (nếu có)
        và các tin tương tự đang bán trên chợ kèm thống kê giá. Ưu tiên dữ liệu chợ; nếu ít hơn 3 tin tương tự thì ước lượng theo kinh nghiệm
        thị trường cây Việt Nam và đặt confidence = "thấp". Giá là VND, số nguyên, làm tròn nghìn. low ≤ recommended ≤ high.
        reasoning: 2–4 câu giải thích. confidence: "cao", "trung bình" hoặc "thấp".
        """;

    static readonly JsonObject PriceSchema = Obj(new()
    {
        ["low"] = T("INTEGER"), ["recommended"] = T("INTEGER"), ["high"] = T("INTEGER"), ["reasoning"] = T("STRING"), ["confidence"] = T("STRING"),
    }, "low", "recommended", "high", "reasoning", "confidence");

    public async Task<object> SuggestPriceAsync(string userId, PriceSuggestRequest req, CancellationToken ct)
    {
        await RequireAsync(userId, p => p.SellerAi, "Gợi ý giá bằng AI", ct);
        var category = await catalog.GetLeafForPostingAsync(req.CategoryId, ct);
        var images = await ImagesAsync((req.MediaIds ?? []).Distinct().Take(MaxImages).ToList(), userId, ct);
        var comps = await ComparablesAsync(category.Id, null, userId, ct, req.Title);
        var sellers = await search.SellerSummariesAsync(comps.Select(l => l.SellerId), ct);
        var stats = PriceStats(comps);
        var compFacts = new List<object>();
        foreach (var l in comps) compFacts.Add(await FactsAsync(l, sellers[l.SellerId], ct));
        var input = Json(new
        {
            draft = new { category = category.Name, title = req.Title?.Trim(), description = Shorten(req.Description?.Trim() ?? "", 1500), attributes = req.Attributes },
            stats, similar = compFacts,
        });

        var mid = stats?.Median ?? 150_000;
        var simulated = Json(new
        {
            low = RoundK(mid * 0.85), recommended = RoundK(mid), high = RoundK(mid * 1.15),
            reasoning = "Chế độ thử: giá mẫu tính từ trung vị các tin tương tự (nếu có) vì chưa nối Gemini.",
            confidence = stats is null ? "thấp" : "trung bình",
        });
        var (doc, used) = await RunAsync(userId, new AiPrompt(PriceSystem, [new AiTurn(true, input, images)], PriceSchema, 0.3, simulated), ct);
        long low = Long(doc, "low"), rec = Long(doc, "recommended"), high = Long(doc, "high");
        if (rec <= 0) { await quota.RefundAsync(userId, ct); throw Unavailable(); }
        low = low <= 0 ? rec : Math.Min(low, rec);
        high = Math.Max(high, rec);
        var now = Now;
        return new
        {
            low = RoundK(low), recommended = RoundK(rec), high = RoundK(high), reasoning = Str(doc, "reasoning"), confidence = Str(doc, "confidence"),
            stats, similar = comps.Take(6).Select(l => search.ToCard(l, sellers[l.SellerId], now)).ToList(), quota = QuotaDto(used),
        };
    }

    const string DraftSystem = """
        Bạn giúp nhà vườn viết tin bán cây trên Chạm Xanh từ ảnh và ghi chú. Nhìn ảnh để nhận ra loại cây, dáng, kích thước tương đối, tình trạng.
        Không bịa thông tin không thấy được (tuổi cây, nguồn gốc, giá); những thứ chưa rõ thì để người bán tự điền.
        title: 10–70 ký tự, có tên cây và điểm nổi bật, không viết hoa toàn bộ, không emoji, không số điện thoại.
        description: 300–900 ký tự, tiếng Việt tự nhiên, chia đoạn ngắn: mô tả cây, tình trạng, cách chăm cơ bản, hình thức giao nhận.
        Không chèn số điện thoại, link hay lời mời giao dịch ngoài sàn.
        missing: 2–4 thông tin người bán nên bổ sung (vd: chiều cao, kích thước chậu).
        """;

    static readonly JsonObject DraftSchema = Obj(new()
    {
        ["title"] = T("STRING"), ["description"] = T("STRING"), ["missing"] = Arr(T("STRING")),
    }, "title", "description", "missing");

    public async Task<object> DraftListingAsync(string userId, ListingDraftRequest req, CancellationToken ct)
    {
        await RequireAsync(userId, p => p.SellerAi, "AI viết tin", ct);
        var ids = (req.MediaIds ?? []).Distinct().ToList();
        if (ids.Count is < 1 or > MaxImages) throw new DomainException("INVALID_PHOTOS", $"Chọn từ 1 đến {MaxImages} ảnh để AI xem");
        if (req.Notes?.Length > 1000) throw new DomainException("NOTES_TOO_LONG", "Ghi chú tối đa 1000 ký tự");
        var images = await ImagesAsync(ids, userId, ct);
        string? categoryName = null;
        if (req.CategoryId is { Length: > 0 } cat) categoryName = (await catalog.GetCategoryAsync(cat, ct)).Name;
        var input = $"Danh mục: {categoryName ?? "chưa chọn"}\nGhi chú của người bán: {(string.IsNullOrWhiteSpace(req.Notes) ? "(không có)" : req.Notes.Trim())}";

        var simulated = Json(new
        {
            title = "Cây cảnh khỏe đẹp, sẵn chậu (bản nháp mẫu)",
            description = "Chế độ thử: đây là bản nháp mẫu vì chưa nối Gemini.\n\nCây khỏe, lá xanh đều, đã thuần trong chậu. Phù hợp để bàn làm việc hoặc ban công có nắng nhẹ.\n\nGiao tận tay trong nội thành hoặc gửi xe, đóng gói kỹ.",
            missing = new[] { "Chiều cao cây", "Kích thước chậu" },
        });
        var (doc, used) = await RunAsync(userId, new AiPrompt(DraftSystem, [new AiTurn(true, input, images)], DraftSchema, 0.6, simulated), ct);
        var title = Shorten(Str(doc, "title"), 69); // Shorten có thể thêm "…": tối đa 70 ký tự như luật tiêu đề
        var description = Shorten(Str(doc, "description"), 3000);
        if (title.Length < 10 || description.Length < 20) { await quota.RefundAsync(userId, ct); throw Unavailable(); }
        return new { title, description, missing = StrArray(doc.RootElement, "missing"), quota = QuotaDto(used) };
    }

    // ---------------- Dùng chung ----------------

    async Task RequireAsync(string userId, Func<PlanDefinition, bool> allowed, string feature, CancellationToken ct)
    {
        var (plan, _) = await plans.GetEffectiveAsync(userId, ct);
        if (!allowed(plan))
            throw new DomainException("PLAN_REQUIRED", $"{feature} dành cho gói {PlanCatalog.Pro.Name} (69.000 đ/tháng).",
                StatusCodes.Status403Forbidden, new { required = PlanCode.Pro.ToString(), current = plan.Code.ToString() });
    }

    /// <summary>Giữ 1 lượt, gọi mô hình, đọc JSON. Lỗi thì trả lại lượt.</summary>
    async Task<(JsonDocument Doc, AiQuota Quota)> RunAsync(string userId, AiPrompt prompt, CancellationToken ct)
    {
        var used = await quota.ConsumeAsync(userId, ct);
        try
        {
            var raw = await model.GenerateAsync(prompt, ct);
            return (JsonDocument.Parse(StripFence(raw)), used);
        }
        catch (Exception ex) when (ex is AiUnavailableException or JsonException)
        {
            logger.LogWarning(ex, "Gọi AI thất bại cho {UserId}", userId);
            await quota.RefundAsync(userId, CancellationToken.None);
            throw Unavailable();
        }
        catch
        {
            await quota.RefundAsync(userId, CancellationToken.None);
            throw;
        }
    }

    static DomainException Unavailable() =>
        new("AI_UNAVAILABLE", "Trợ lý AI đang bận, bạn thử lại sau ít phút nhé. Lượt này không bị tính.", StatusCodes.Status503ServiceUnavailable);

    async Task<List<AiImage>> ImagesAsync(List<string> ids, string userId, CancellationToken ct)
    {
        var list = new List<AiImage>();
        if (ids.Count == 0) return list;
        foreach (var item in await media.GetOwnedAsync(ids, userId, ct))
            if (await ReadAsync(item, "full", ct) is { } img) list.Add(img);
        return list;
    }

    /// <summary>Ảnh đầu tiên (cỡ thẻ) của mỗi tin, để AI nhìn được cây.</summary>
    async Task<List<AiImage>> ListingImagesAsync(IEnumerable<Listing> items, CancellationToken ct)
    {
        var list = new List<AiImage>();
        foreach (var l in items)
            if (l.Media.Count > 0 && await media.FindAsync(l.Media[0].MediaId, ct) is { } item && await ReadAsync(item, "card", ct) is { } img)
                list.Add(img);
        return list;
    }

    async Task<AiImage?> ReadAsync(MediaItem item, string variant, CancellationToken ct)
    {
        if (await media.OpenAsync(item, variant, ct) is not { } file) return null;
        await using var s = file.Stream;
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return new AiImage(ms.ToArray(), file.ContentType);
    }

    async Task<object> FactsAsync(Listing l, SellerSummary seller, CancellationToken ct)
    {
        string category;
        try { category = (await catalog.GetCategoryAsync(l.CategoryId, ct)).Name; }
        catch (DomainException) { category = l.CategoryId; }
        return new
        {
            id = l.Id, title = l.Title, category, price = l.Price, negotiable = l.PriceNegotiable || l.PriceMode == PriceMode.Negotiable,
            attributes = l.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value is string s ? s : kv.Value is System.Collections.IEnumerable list ? string.Join(", ", list.Cast<object>()) : kv.Value?.ToString()),
            description = Shorten(l.Description, 600), photos = l.Media.Count,
            realPhotos = l.Media.Count(m => m.CapturedInApp), escrow = l.EscrowEnabled, available = l.Available, unit = l.Unit,
            seller = new { verifiedGarden = seller.IsVerifiedGarden, garden = seller.IsGarden, proSeller = seller.IsProSeller },
            postedDaysAgo = l.FirstPublishedAt is { } at ? (int)(Now - at).TotalDays : (int?)null,
        };
    }

    /// <summary>Tin bán đang hiển thị cùng danh mục (ưu tiên cùng loài/từ khóa tiêu đề), không gồm tin của chính người hỏi.</summary>
    async Task<List<Listing>> ComparablesAsync(string categoryId, string? speciesId, string userId, CancellationToken ct, string? title = null)
    {
        var f = Builders<Listing>.Filter;
        var baseFilter = f.Eq(l => l.Status, ListingStatus.Active) & f.Eq(l => l.Type, ListingType.Sell) & f.Ne(l => l.Price, null)
            & f.Gt(l => l.Price, 0) & f.Ne(l => l.SellerId, userId) & f.Eq(l => l.CategoryId, categoryId);
        var result = new List<Listing>();
        if (speciesId is { Length: > 0 } && speciesId != "khac")
            result.AddRange(await listings.Listings.Find(baseFilter & f.Eq(l => l.SpeciesId, speciesId)).SortByDescending(l => l.BumpedAt).Limit(10).ToListAsync(ct));
        var words = VietnameseText.Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).Take(3).ToList();
        if (result.Count < 10 && words.Count > 0)
        {
            var kw = f.And(words.Select(w => f.Regex(l => l.SearchText, new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(w)))));
            result.AddRange(await listings.Listings.Find(baseFilter & kw & f.Nin(l => l.Id, result.Select(r => r.Id))).SortByDescending(l => l.BumpedAt).Limit(10 - result.Count).ToListAsync(ct));
        }
        if (result.Count < 10)
            result.AddRange(await listings.Listings.Find(baseFilter & f.Nin(l => l.Id, result.Select(r => r.Id))).SortByDescending(l => l.BumpedAt).Limit(10 - result.Count).ToListAsync(ct));
        return result;
    }

    public record PriceStatsDto(int Count, long Min, long Median, long Max);

    static PriceStatsDto? PriceStats(List<Listing> comps)
    {
        var prices = comps.Where(l => l.Price > 0).Select(l => l.Price!.Value).Order().ToList();
        if (prices.Count == 0) return null;
        var median = prices.Count % 2 == 1 ? prices[prices.Count / 2] : (prices[prices.Count / 2 - 1] + prices[prices.Count / 2]) / 2;
        return new(prices.Count, prices[0], median, prices[^1]);
    }

    static long RoundK(double v) => (long)Math.Round(v / 1000, MidpointRounding.AwayFromZero) * 1000;

    static string StripFence(string raw)
    {
        var s = raw.Trim();
        if (!s.StartsWith("```")) return s;
        var start = s.IndexOf('\n');
        var end = s.LastIndexOf("```", StringComparison.Ordinal);
        return start > 0 && end > start ? s[(start + 1)..end].Trim() : s;
    }

    static string Shorten(string s, int max)
    {
        s = s.Trim();
        if (s.Length <= max) return s;
        var cut = s[..max];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', ',', '.', '-') + "…";
    }

    static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    static string Str(JsonDocument d, string key) =>
        d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
    static bool Bool(JsonDocument d, string key, bool fallback) =>
        d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
    static long Long(JsonDocument d, string key) =>
        d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt64(out var n) ? n : (long)v.GetDouble()) : 0;
    static List<string> StrArray(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Take(8).ToList()
            : [];

    static JsonObject T(string type) => new() { ["type"] = type };
    static JsonObject Arr(JsonObject items) => new() { ["type"] = "ARRAY", ["items"] = items };
    static JsonObject Obj(Dictionary<string, JsonObject> props, params string[] required)
    {
        var p = new JsonObject();
        foreach (var (k, v) in props) p[k] = v;
        return new JsonObject { ["type"] = "OBJECT", ["properties"] = p, ["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray()) };
    }
}
