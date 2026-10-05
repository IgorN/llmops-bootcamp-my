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

// прайс (W2): USD за 1k токенів (in, out). Ціни це живі дані, тому для реальних моделей пишемо дату і джерело.
// mock-ціни навчальні, але з реальними пропорціями. strong дорожча у ~16 разів, out у 4 рази дорожчий за in.
// azure-gpt-5 навмисно немає. Ціна Azure залежить від типу deployment і регіону,
// а deployment у конфігу поки заглушка. Для нього cost_usd буде null, а не нуль.
var prices = new Dictionary<string, (decimal In, decimal Out)>
{
    ["mock-mini"] = (0.00015m, 0.0006m),
    ["mock-strong"] = (0.0025m, 0.01m),
    // OpenAI станом на 2026-10-04, developers.openai.com/api/docs/pricing ($/1M: mini 0.25/2, gpt-5 1.25/10)
    ["gpt-5-mini"] = (0.00025m, 0.002m),
    ["gpt-5"] = (0.00125m, 0.01m),
    // Anthropic станом на 2026-10-04, platform.claude.com/docs/en/about-claude/pricing ($/1M: haiku 1/5, opus 4/20)
    ["claude-haiku-4-5"] = (0.001m, 0.005m),
    ["claude-opus-5-5"] = (0.004m, 0.02m),
};
var budgetUsd = 5.00m;  // денний бюджет
var budgetAlertDay = DateOnly.MinValue;  // щоб алерт був раз на день, а не на кожен запит

app.MapPost("/chat", async (ChatIn body, HttpContext ctx, IHttpClientFactory httpFactory, ILogger<Program> logger) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;
    var source = Source(ctx);

    // guardrails (W4): тут перевірити вхід на PII / інʼєкції. поки нічого.
    // TODO(student, W4)

    // routing (W2): модель за рівнем (tier) запиту. Рівень пишемо в лог, щоб бачити частку трафіку.
    var (model, tier) = Route(body.Message, defaultModel);

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
    var usageKnown = false;  // без usage вартість невідома, і це null, а не нуль
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
        usageKnown = true;
    }
    catch
    {
        // мережа/gateway недоступні або відповідь не розпарсилась (status лишиться 0/5xx).
        // TODO(student, W4): тут краще graceful degradation
        answer = "Сервіс тимчасово недоступний.";
    }

    var latencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // cost (W2): usage з відповіді, ціна за моделлю з прайсу. Нема тарифу або usage, буде null.
    // Нуль лишаємо для випадку, коли виклику справді не було (cache-hit з W3).
    // Округлення до 6 знаків як у колонці NUMERIC(10, 6).
    decimal? costUsd = usageKnown && prices.TryGetValue(model, out var price)
        ? Math.Round(promptTokens / 1000m * price.In + completionTokens / 1000m * price.Out, 6)
        : null;

    // лог кожного запиту — з цього живе observability (W1) і cost (W2)
    await LogRequest(dbConn, requestId, model, tier, source, promptVersion, latencyMs, promptTokens, completionTokens, costUsd, status);

    // budget policy (W2): поріг 80% за клієнтськими витратами дає алерт у лог, див. рядок біля Route().
    // Перевіряємо лише після платного клієнтського виклику. Тільки він збільшує суму, тож
    // evals, невдалі виклики і cache-hit з W3 не ходять у базу за сумою.
    // Перевірка після логу, щоб у суму потрапив і цей запит. Падіння перевірки
    // не валить відповідь користувачу, як і падіння логу.
    if (source == "user" && costUsd > 0)
    {
        try
        {
            var spent = (await TodayCostUsd(dbConn)).User;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (spent >= budgetUsd * 0.8m && budgetAlertDay != today)
            {
                budgetAlertDay = today;
                logger.LogWarning("budget alert: users spent {Spent} of {Budget} USD today, 80% threshold reached", spent, budgetUsd);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "budget check failed for request {RequestId}", requestId);
        }
    }

    return Results.Json(new { request_id = requestId, content = answer, tool = toolCall, latency_ms = latencyMs });
});

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));                                    // ліфнес, не для консолі
app.MapGet("/observability", () => Results.Json(new { todo = "aggregate from requests table" }));  // W5: { p95_ms, requests, cache_hit_pct, error_rate_pct, fallback_events }
// W2: витрати за сьогодні з БД плюс бюджет. Без БД 503, а не нуль, бо нуль збреше плитці.
app.MapGet("/cost", async (ILogger<Program> logger) =>
{
    try
    {
        // today_usd лишається загальним, бо гроші за evals теж справжні. Розбивка показує, хто витратив.
        var cost = await TodayCostUsd(dbConn);
        return Results.Json(new
        {
            today_usd = cost.Total,
            budget_usd = budgetUsd,
            by_source = new { user = cost.User, eval = cost.Eval },
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "failed to read today cost");
        return Results.Json(new { error = "cost unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
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

// fallback chain: escalation mock-strong -> claude-opus-5-5 -> тікет на оператора; faq, standard mock-mini -> claude-haiku-4-5 -> контрольована помилка
// Другий крок завжди інший провайдер. mock-mini і mock-strong живуть на одному
// mock-provider і падають разом, тож перехід між ними від падіння не рятує.
// Ескалацію не спускаємо на слабшу модель. Повернення і скарги краще віддати людині.
// budget policy: на 80% денного бюджету за клієнтськими витратами (source = user) пишемо алерт у лог і маршрут не міняємо, бо faq і standard уже на найдешевшій моделі, а ескалацію свідомо не здешевлюємо.
// Evals у поріг не входять. Політика керує маршрутом клієнтів, і прогін у CI не повинен їх різати. Свій ліміт для evals буде на W6.
static (string Model, string Tier) Route(string message, string defaultModel)
{
    var tier = Tier(message);

    // MODEL не mock означає реальну модель. Її лишаємо як є, а tier тільки пишемо в лог.
    if (defaultModel != "mock") return (defaultModel, tier);

    // faq поки йде на mock-mini. На W3 його підхопить кеш.
    return (tier == "escalation" ? "mock-strong" : "mock-mini", tier);
}

// tier-політика. Ескалацію перевіряємо першою, бо вона сильніша за FAQ.
// «поверніть гроші, не працює вхід» має піти на сильну модель.
// Хибна ескалація коштує дорожчий виклик, пропущена коштує клієнта зі скаргою.
// Тому маркери ескалації широкі, а FAQ вузький. Решта це standard.
static string Tier(string message)
{
    var m = message.ToLowerInvariant();
    string[] escalation = ["поверн", "терміново", "скарг", "refund", "money back", "chargeback", "urgent", "complain"];
    string[] faq = ["пароль", "password", "вхід", "login"];

    if (escalation.Any(m.Contains)) return "escalation";
    if (faq.Any(m.Contains)) return "faq";
    return "standard";
}

// звідки запит. Runner evals шле X-Source: eval, решта це клієнти.
// Заголовок задає сам клієнт, тож це атрибуція витрат, а не захист.
// Невідоме значення не валить запит і вважається user.
static string Source(HttpContext ctx) =>
    string.Equals(ctx.Request.Headers["X-Source"].ToString(), "eval", StringComparison.OrdinalIgnoreCase)
        ? "eval"
        : "user";

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

// витрати за сьогодні, усього і окремо за source. Одна функція і для GET /cost,
// і для порогу бюджету, щоб не розійшлись. Один запит, FILTER ділить суму без другого походу.
// Діапазон дає той самий день, що й created_at::date = CURRENT_DATE, але може взяти індекс.
// SUM без рядків дає NULL, тому COALESCE до нуля. Тут нуль чесний, бо витрат не було.
static async Task<(decimal Total, decimal User, decimal Eval)> TodayCostUsd(string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT COALESCE(SUM(cost_usd), 0), "
        + "COALESCE(SUM(cost_usd) FILTER (WHERE source = 'user'), 0), "
        + "COALESCE(SUM(cost_usd) FILTER (WHERE source = 'eval'), 0) "
        + "FROM requests WHERE created_at >= CURRENT_DATE AND created_at < CURRENT_DATE + 1", db);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
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
static async Task LogRequest(string conn, Guid id, string model, string tier, string source, string promptVersion, int latency,
    int promptTokens, int completionTokens, decimal? cost, int status)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO requests (request_id, model, tier, source, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status) "
            + "VALUES (@id, @model, @tier, @source, @pv, @lat, @pt, @ct, @cost, @status)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("tier", tier);
        cmd.Parameters.AddWithValue("source", source);
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
