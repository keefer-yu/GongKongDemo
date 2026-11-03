using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Scada.Alarms;
using Scada.Control;
using Scada.Core.Configuration;
using Scada.Core.Control;
using Scada.Core.Tags;
using Scada.Storage;

namespace Scada.Northbound;

public sealed record WriteBody(double Value);

/// <summary>
/// 内嵌 Kestrel 的 REST API。用 localhost + 高端口，避免 HttpListener 的 URL ACL 权限问题。
///
/// 为什么用 Kestrel 而不是 HttpListener？
/// - WinForms/WPF 进程可以直接托管 ASP.NET Core 的 Kestrel；
/// - 不需要管理员权限或 netsh urlacl；
/// - 后续要换控制器/Swagger/鉴权中间件也容易。
/// </summary>
public sealed class RestApiServer : IAsyncDisposable
{
    private readonly RestSettings _settings;
    private readonly TagStore _store;
    private readonly AlarmEngine _alarms;
    private readonly SqliteHistoryStore _history;
    private readonly CommandService _commands;
    private WebApplication? _app;

    public RestApiServer(
        RestSettings settings,
        TagStore store,
        AlarmEngine alarms,
        SqliteHistoryStore history,
        CommandService commands)
    {
        _settings = settings;
        _store = store;
        _alarms = alarms;
        _history = history;
        _commands = commands;
    }

    public bool IsRunning => _app is not null;
    public string Url => _settings.Url;
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || _app is not null) return;

        // 一个极简的 Web 主机：不注册控制器，直接在 app 上挂 Minimal API。
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();       // 避免桌面程序控制台被请求日志刷屏
        builder.WebHost.UseUrls(_settings.Url); // 默认 http://127.0.0.1:15080
        var app = builder.Build();

        // /health 不鉴权，方便上位/运维先探活。
        app.MapGet("/api/v1/health", () => Results.Json(new
        {
            status = "ok",
            timestamp = DateTimeOffset.UtcNow,
            device = _store.Definitions.FirstOrDefault()?.Name ?? "scada",
        }));

        // 查询类接口：读 TagStore 快照，不直接读设备，所以不会阻塞采集。
        app.MapGet("/api/v1/tags", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            return Results.Json(_store.Snapshot().Values.Select(ToDto));
        });

        app.MapGet("/api/v1/tags/{id}", (string id, HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            var value = _store.Get(id);
            return value.Quality == TagQuality.Bad && value.Error?.Contains("未知") == true
                ? Results.NotFound(new { error = $"未知点位 {id}" })
                : Results.Json(ToDto(value));
        });

        // 写点接口：不在 REST 层直接写设备，而是交给 CommandService，
        // 这样 REST 和 MQTT 共享同一套联锁/回读/审计。
        app.MapPost("/api/v1/tags/{id}/write", async (string id, WriteBody body, HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();

            var result = await _commands.ExecuteAsync(new CommandRequest
            {
                TagId = id,
                Value = body.Value,
                Source = CommandSource.Rest,
                Confirmed = true, // 已通过 Token 鉴权，视为“上位系统确认”
                Origin = ctx.Connection.RemoteIpAddress?.ToString(),
            });

            // 用 HTTP 状态码表达业务结果：
            // 200=成功，409=联锁拒绝（可重试但不是程序错误），500=写入/回读失败。
            var status = result.State switch
            {
                CommandState.Succeeded => StatusCodes.Status200OK,
                CommandState.Rejected => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError,
            };
            return Results.Json(result, statusCode: status);
        });

        app.MapGet("/api/v1/alarms", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            var activeOnly = bool.TryParse(ctx.Request.Query["active"], out var active) && active;
            var data = activeOnly
                ? _alarms.Active.Select(ToDto)
                : _alarms.Recent.OrderByDescending(a => a.RaisedAt).Select(ToDto);
            return Results.Json(data);
        });

        // 历史查询：限制最多 1440 分钟（1 天），避免一次拉爆 SQLite。
        app.MapGet("/api/v1/history", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            var tagId = ctx.Request.Query["tagId"].FirstOrDefault() ?? "temp";
            var minutes = int.TryParse(ctx.Request.Query["minutes"], out var m) ? Math.Clamp(m, 1, 1440) : 10;
            var to = DateTimeOffset.UtcNow;
            var from = to.AddMinutes(-minutes);
            var rows = await _history.QueryHistoryAsync(tagId, from, to, 5000);
            return Results.Json(rows.Select(r => new
            {
                timestamp = r.Timestamp,
                tagId = r.TagId,
                value = r.Value,
                quality = r.Quality.ToString(),
                raw = r.RawValue,
            }));
        });

        app.MapGet("/api/v1/commands", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            var rows = await _history.QueryAuditAsync(200);
            return Results.Json(rows);
        });

        try
        {
            // 只启动，不 Run/阻塞；桌面程序主线程还要继续跑 UI。
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            _app = app;
            LastError = null;
        }
        catch (Exception ex)
        {
            // 端口被占用等启动失败：记录错误并释放，让 V5 继续以 MQTT 或纯采集模式运行。
            LastError = ex.Message;
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        try
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// 演示级鉴权：固定 Header Token。生产必须换成 OAuth2/API Key 管理 + HTTPS + 限流。
    /// </summary>
    private bool Authorized(HttpContext context)
        => string.Equals(
            context.Request.Headers["X-Api-Key"].FirstOrDefault(),
            _settings.ApiKey,
            StringComparison.Ordinal);

    private static object ToDto(TagValue value) => new
    {
        id = value.TagId,
        value = double.IsNaN(value.Value) ? (double?)null : value.Value,
        raw = double.IsNaN(value.RawValue) ? (double?)null : value.RawValue,
        quality = value.Quality.ToString(),
        timestamp = value.Timestamp,
        error = value.Error,
    };

    private static object ToDto(AlarmRecord record) => new
    {
        tagId = record.TagId,
        tagName = record.TagName,
        level = record.Level.ToString(),
        state = record.State.ToString(),
        value = record.Value,
        message = record.Message,
        raisedAt = record.RaisedAt,
        clearedAt = record.ClearedAt,
        acknowledgedAt = record.AcknowledgedAt,
    };

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

