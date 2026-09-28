using System.Text.Json.Serialization;
using ChamXanh.Api.Common;
using ChamXanh.Api.Common.Auth;
using ChamXanh.Api.Infrastructure;
using ChamXanh.Api.Modules.Admin;
using ChamXanh.Api.Modules.Catalog;
using ChamXanh.Api.Modules.Discovery;
using ChamXanh.Api.Modules.Gardens;
using ChamXanh.Api.Modules.Messaging;
using ChamXanh.Api.Modules.Reviews;
using ChamXanh.Api.Modules.Identity;
using ChamXanh.Api.Modules.Listings;
using ChamXanh.Api.Modules.Media;
using ChamXanh.Api.Modules.Moderation;
using ChamXanh.Api.Modules.Pricing;
using ChamXanh.Api.Modules.Wallet;
using ChamXanh.Api.Modules.Notifications;
using ChamXanh.Api.Modules.PlantCare;
using ChamXanh.Api.Modules.Explore;
using ChamXanh.Api.Modules.Escrow;
using ChamXanh.Api.Modules.Platform;
using ChamXanh.Api.Modules.Community;
using ChamXanh.Api.Modules.Ai;
using ChamXanh.Api.Modules.Deals;
using ChamXanh.Api.Modules.Operations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var services = builder.Services;

// Render cấp cổng qua biến PORT.
if (Environment.GetEnvironmentVariable("PORT") is { } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

ConventionRegistry.Register("chamxanh", new ConventionPack
{
    new CamelCaseElementNameConvention(),
    new IgnoreExtraElementsConvention(true),
}, _ => true);

// ---- Hạ tầng dùng chung ----
services.AddSingleton<IMongoClient>(_ => new MongoClient(
    config.GetConnectionString("Mongo") ?? throw new InvalidOperationException("Thiếu ConnectionStrings:Mongo")));
services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(config["Mongo:Database"] ?? "chamxanh"));
services.AddSingleton(TimeProvider.System);
services.AddHttpContextAccessor();
services.AddScoped<AuditService>();
services.AddStartupTask((sp, _) => sp.GetRequiredService<AuditService>().EnsureIndexesAsync());

// ---- Xác thực & phân quyền ----
var authOptions = config.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
if (authOptions.JwtKey.Length < 32) throw new InvalidOperationException("Auth:JwtKey phải dài ít nhất 32 ký tự");
services.AddSingleton(authOptions);
services.AddScoped<TokenService>();
services.AddStartupTask((sp, _) => sp.GetRequiredService<TokenService>().EnsureIndexesAsync());
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = authOptions.Issuer, ValidAudience = authOptions.Issuer,
        IssuerSigningKey = TokenService.SigningKey(authOptions), ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name",
    };
    // WebSocket không gửi được header Authorization: SignalR truyền token qua query string.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query["access_token"] is { Count: > 0 } t)
                ctx.Token = t;
            return Task.CompletedTask;
        },
        // Token phạm vi "hub" chỉ hợp lệ trên /hubs.
        OnTokenValidated = ctx =>
        {
            if (ctx.Principal?.HasClaim(Claims.Scope, Claims.HubScope) == true && !ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                ctx.Fail("Token chỉ dùng cho kết nối realtime");
            return Task.CompletedTask;
        },
    };
});
services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Member, p => p.RequireClaim(Claims.Kind, Claims.Member))
    .AddPolicy(Policies.Admin, p => p.RequireClaim(Claims.Kind, Claims.Admin));
foreach (var perm in Perm.All)
    services.AddAuthorizationBuilder().AddPolicy(Policies.ForPerm(perm), p => p
        .RequireClaim(Claims.Kind, Claims.Admin).RequireClaim(Claims.Mfa, "true").RequireClaim(Claims.Perm, perm));

// ---- Module ----
services.AddSingleton(config.GetSection("Otp").Get<OtpOptions>() ?? new OtpOptions());
services.AddSingleton<IOtpSender, LogOtpSender>();
services.AddScoped<OtpService>();
services.AddScoped<UserService>();
services.AddStartupTask(async (sp, _) =>
{
    await sp.GetRequiredService<OtpService>().EnsureIndexesAsync();
    await sp.GetRequiredService<UserService>().EnsureIndexesAsync();
});

var adminOptions = config.GetSection("Admin").Get<AdminOptions>() ?? new AdminOptions();
// Mật khẩu admin yếu (vd admin/123) chỉ được phép khi chạy Development.
if (!builder.Environment.IsDevelopment()) adminOptions.AllowWeakBootstrapPassword = false;
services.AddSingleton(adminOptions);
services.AddScoped<AdminAuthService>();
services.AddStartupTask((sp, _) => sp.GetRequiredService<AdminAuthService>().EnsureIndexesAndBootstrapAsync());

services.AddScoped<CatalogService>();
services.AddStartupTask((sp, ct) => sp.GetRequiredService<CatalogService>().EnsureIndexesAndSeedAsync(ct));

services.AddScoped<MediaService>();
services.AddStartupTask((sp, _) => sp.GetRequiredService<MediaService>().EnsureIndexesAsync());

services.AddSingleton(config.GetSection("Listings").Get<ListingOptions>() ?? new ListingOptions());
services.AddScoped<ModerationService>();
services.AddScoped<ListingService>();
services.AddScoped<ListingSearch>();
services.AddScoped<ISpeciesLegalFlagChanged, ListingSpeciesFlagHandler>();
services.AddHostedService<ListingMaintenanceWorker>();
services.AddStartupTask(async (sp, _) =>
{
    await sp.GetRequiredService<ModerationService>().EnsureIndexesAsync();
    await sp.GetRequiredService<ListingService>().EnsureIndexesAsync();
    var phoneViews = sp.GetRequiredService<IMongoDatabase>().GetCollection<PhoneView>("phoneViews");
    await phoneViews.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<PhoneView>(Builders<PhoneView>.IndexKeys.Ascending(v => v.At), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(90) }),
        new CreateIndexModel<PhoneView>(Builders<PhoneView>.IndexKeys.Ascending(v => v.ViewerId).Ascending(v => v.At)),
    ]);
});

services.AddSingleton(config.GetSection("Payments").Get<PaymentOptions>() ?? new PaymentOptions());
services.AddScoped<WalletService>();
services.AddScoped<TopUpService>();
services.AddScoped<PromotionService>();
services.AddHostedService<WalletWorker>();
services.AddStartupTask(async (sp, _) =>
{
    await sp.GetRequiredService<WalletService>().EnsureIndexesAsync();
    await sp.GetRequiredService<TopUpService>().EnsureIndexesAsync();
    await sp.GetRequiredService<PromotionService>().EnsureIndexesAsync();
});

// Enum dạng chữ giống REST, để client xử lý tin nhắn realtime và tin nhắn tải qua API như nhau.
services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddScoped<ChatService>();
services.AddStartupTask((sp, _) => sp.GetRequiredService<ChatService>().EnsureIndexesAsync());

services.AddSingleton(new DataProtector(config.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions()));
services.AddScoped<GardenService>();
services.AddHostedService<GardenPlanWorker>();
services.AddStartupTask((sp, _) => sp.GetRequiredService<GardenService>().EnsureIndexesAsync());

services.AddScoped<ReviewService>();
services.AddStartupTask(async (sp, _) =>
{
    await sp.GetRequiredService<ReviewService>().EnsureIndexesAsync();
    await DiscoveryEndpoints.EnsureIndexesAsync(sp.GetRequiredService<IMongoDatabase>());
});

services.AddScoped<PricingService>();
services.AddHostedService<PriceBookActivationWorker>();
services.AddStartupTask(async (sp, ct) =>
{
    var pricing = sp.GetRequiredService<PricingService>();
    await pricing.EnsureIndexesAsync(ct);
    await pricing.SeedIfEmptyAsync(ct);
});

services.AddScoped<FeatureFlagService>();
services.AddSingleton(config.GetSection("Escrow").Get<EscrowOptions>() ?? new EscrowOptions());
services.AddSingleton<IEscrowGateway, SandboxEscrowGateway>();
services.AddScoped<EscrowService>();
services.AddHostedService<EscrowWorker>();
services.AddStartupTask((sp, _) => sp.GetRequiredService<EscrowService>().EnsureIndexesAsync());
services.AddSingleton<LogPushSender>();
services.AddSingleton<IPushSender>(sp => sp.GetRequiredService<LogPushSender>());
services.AddSingleton<ISmsSender>(sp => sp.GetRequiredService<LogPushSender>());
services.AddScoped<DealsService>();
services.AddSingleton(config.GetSection("Kyc").Get<KycOptions>() ?? new KycOptions());
services.AddSingleton<IKycVerifier, ManualKycVerifier>();
services.AddScoped<KycCheckService>();
services.AddStartupTask(async (sp, _) =>
{
    await sp.GetRequiredService<DealsService>().EnsureIndexesAsync();
    await OperationsEndpoints.EnsureIndexesAsync(sp.GetRequiredService<IMongoDatabase>());
});
services.AddScoped<CommunityService>();
services.AddScoped<AccountLifecycleService>();
var aiOptions = config.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
services.AddSingleton(aiOptions);
if (aiOptions.Provider == "plantid" && aiOptions.ApiKey.Length > 0)
    services.AddHttpClient<IPlantRecognizer, PlantIdRecognizer>(c => c.Timeout = TimeSpan.FromSeconds(20));
else
    services.AddSingleton<IPlantRecognizer, NullPlantRecognizer>();
services.AddScoped<RecognitionService>();
services.AddStartupTask(async (sp, _) =>
{
    await sp.GetRequiredService<CommunityService>().EnsureIndexesAsync();
    await sp.GetRequiredService<RecognitionService>().EnsureIndexesAsync();
});
services.AddScoped<NotificationService>();
services.AddScoped<PlantCareService>();
services.AddScoped<ExploreService>();
services.AddHostedService<CareReminderWorker>();
services.AddStartupTask(async (sp, _) =>
{
    await sp.GetRequiredService<NotificationService>().EnsureIndexesAsync();
    await sp.GetRequiredService<PlantCareService>().EnsureIndexesAsync();
    await sp.GetRequiredService<ExploreService>().EnsureIndexesAsync();
});

// ---- Web ----
services.AddExceptionHandler<DomainExceptionHandler>();
services.AddProblemDetails();
services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddOpenApi();
services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapGet("/health", async (IMongoDatabase db, CancellationToken ct) =>
{
    await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
    return Results.Ok(new { status = "ok" });
});
app.MapIdentity();
app.MapAdminAuth();
app.MapCatalog();
app.MapMedia();
app.MapListings();
app.MapModeration();
app.MapWallet();
app.MapChat();
app.MapGardens();
app.MapReviews();
app.MapDiscovery();
app.MapPricing();
app.MapNotifications();
app.MapPlantCare();
app.MapExplore();
app.MapFeatureFlags();
app.MapEscrow();
app.MapCommunity();
app.MapAi();
app.MapAccountLifecycle();
app.MapDeals();
app.MapOperations();

// Mở cổng trước rồi mới chạy startup task: tạo index/seed lần đầu có thể lâu hơn hạn quét cổng của Render.
await app.StartAsync();
await app.RunStartupTasksAsync();
await app.WaitForShutdownAsync();

public partial class Program;
