// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба. MODEL=gpt-5-mini + ключ у gateway/.env — реальна модель.

using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:4000";
var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";
var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory, ILogger<Program> logger) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): тут перевірити вхід на PII / інʼєкції. поки нічого.
    // TODO(student, W4)

    // routing (W2): поки одна модель, а треба обирати за задачею
    var model = defaultModel;  // TODO(student, W2)

    // промпт (W1): активна версія з реєстру.
    // Недоступна БД не дорівнює порожньому реєстру. Без неї не буде ні промпта,
    // ні лога, тому віддаємо 503 замість тихої відповіді на дефолті.
    string systemPrompt, promptVersion;
    try
    {
        (systemPrompt, promptVersion) = await GetActivePrompt(dbConn);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "prompt registry unavailable, request {RequestId} rejected", requestId);
        return Results.Json(
            new { request_id = requestId, error = "prompt registry unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // cache (W3): перед викликом глянути в Redis — раптом вже відповідали
    // TODO(student, W3)

    // fallback (W4): якщо тут 429/5xx — піти на іншого провайдера. поки один виклик.
    // TODO(student, W4)
    var payload = JsonSerializer.Serialize(new
    {
        model,
        messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = body.Message }
        }
    });

    var http = httpFactory.CreateClient();
    var answer = "";
    string? toolCall = null;
    int promptTokens = 0, completionTokens = 0, status = 0; // 0 = відповіді не було
    try
    {
        var response = await http.PostAsync(
            $"{gateway}/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        status = (int)response.StatusCode;
        var rawJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(rawJson);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        answer = message.GetProperty("content").GetString() ?? "";

        // tools + HITL (W3/W4): якщо модель попросила інструмент — виконати;
        // перед незворотною дією (створити тікет) спитати людину. поки лише читаємо назву.
        // TODO(student, W3/W4)
        if (message.TryGetProperty("tool_calls", out var tools)
            && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
        {
            toolCall = tools[0].GetProperty("function").GetProperty("name").GetString();
        }

        var usage = doc.RootElement.GetProperty("usage");
        promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
        completionTokens = usage.GetProperty("completion_tokens").GetInt32();
    }
    catch
    {
        // мережа/gateway недоступні або відповідь не розпарсилась (status лишиться 0/5xx).
        // TODO(student, W4): тут краще graceful degradation
        answer = "Сервіс тимчасово недоступний.";
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // cost (W2): порахувати tokens * ціна і покласти в cost_usd
    decimal? costUsd = null;  // TODO(student, W2)

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await LogRequest(dbConn, requestId, model, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status);

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі
app.MapGet("/observability", () => Results.Json(new { todo = "aggregate from requests table" }));  // W5: { p95_ms, requests, cache_hit_pct, error_rate_pct, fallback_events }
app.MapGet("/cost", () => Results.Json(new { todo = "sum cost_usd for today + budget" }));         // W2/W5: { today_usd, budget_usd }
// W1: усі версії промпта, не лише активна. Консоль показує, на що можна відкотитись.
app.MapGet("/prompts", async (ILogger<Program> logger) =>
{
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT name, version, active FROM prompts ORDER BY name, version", db);
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<object>();
        while (await reader.ReadAsync())
        {
            rows.Add(new
            {
                name = reader.GetString(0),
                version = reader.GetString(1),
                active = reader.GetBoolean(2)
            });
        }

        return Results.Json(rows);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "failed to read prompt registry");
        return Results.Json(new { error = "prompt registry unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// W1: promote / rollback. Перемикання робить один атомарний UPDATE.
// EXISTS у тому ж запиті не дає зняти active з усіх версій, коли просять неіснуючу.
// Audit пишеться в тій самій транзакції, щоб стан і історія не розійшлися.
app.MapPost("/prompts/{version}/activate", async (string version, HttpContext ctx, ILogger<Program> logger) =>
{
    try
    {
        await using var db = new NpgsqlConnection(dbConn);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        await using var update = new NpgsqlCommand(
            "UPDATE prompts SET active = (version = @v) "
            + "WHERE name = 'support-system' "
            + "AND EXISTS (SELECT 1 FROM prompts WHERE name = 'support-system' AND version = @v)", db, tx);
        update.Parameters.AddWithValue("v", version);
        var affected = await update.ExecuteNonQueryAsync();

        if (affected == 0)
        {
            // жодного рядка не зачепили, такої версії немає. Стан реєстру не змінився.
            await tx.RollbackAsync();
            return Results.NotFound(new { error = $"prompt version '{version}' not found" });
        }

        await using var audit = new NpgsqlCommand(
            "INSERT INTO prompt_activations (name, version, actor) VALUES ('support-system', @v, @actor)", db, tx);
        audit.Parameters.AddWithValue("v", version);
        audit.Parameters.AddWithValue("actor", (object?)Actor(ctx) ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync();

        await tx.CommitAsync();
        logger.LogInformation("prompt version {Version} activated", version);
        return Results.Ok(new { name = "support-system", version, active = true });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "failed to activate prompt version {Version}", version);
        return Results.Json(new { error = "prompt registry unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapGet("/providers", () => Results.Json(new { todo = "provider health" }));                    // W5: { providers: [ { name, status } ] }
app.MapGet("/approvals", () => Results.Json(new { todo = "pending HITL approvals" }));              // W4: { pending: [ { id, action } ] }

app.Run("http://0.0.0.0:8080");

// хто перемкнув версію. Беремо із заголовка X-Actor.
// IP це запасний варіант і не ідентифікує людину, бо з хоста всі запити йдуть
// від bridge, а з консолі від контейнера ui. Тому позначаємо його як ip:
// і замінимо на перевірений ідентифікатор, коли зʼявиться автентифікація (W4).
static string? Actor(HttpContext ctx)
{
    var header = ctx.Request.Headers["X-Actor"].ToString();
    if (!string.IsNullOrWhiteSpace(header)) return header.Trim();

    var ip = ctx.Connection.RemoteIpAddress?.ToString();
    return ip is null ? null : $"ip:{ip}";
}

// дістає активний промпт і його версію з реєстру.
// Порожній реєстр це не помилка, а стан. Віддаємо резервний промпт без маркера
// "support" і версію "none", щоб поломку було видно і в чаті, і в лозі.
// Помилку підключення не ковтаємо, вона летить вище і стає 503.
static async Task<(string Body, string Version)> GetActivePrompt(string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT version, body FROM prompts WHERE name = 'support-system' AND active LIMIT 1", db);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        return (reader.GetString(1), reader.GetString(0));
    }

    return ("You are an assistant.", "none");
}

// пише один рядок у requests. якщо лог впав — запит користувача все одно віддаємо.
static async Task LogRequest(string conn, Guid id, string model, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status) "
            + "VALUES (@id, @model, @pv, @lat, @pt, @ct, @cost, @status)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("pv", promptVersion);
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue("cost", (object?)cost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", status.ToString());
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* не валимо запит через лог */ }
}

record ChatIn(string Message);
