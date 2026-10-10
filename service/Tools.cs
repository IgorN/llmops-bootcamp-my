// інструменти (W3): виконання, перевірка аргументів, таймаут і ідемпотентність.
// Винесено з Program.cs в окремий клас, щоб це можна було покрити юніт-тестами.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

public static class ToolRuntime
{
    // виконання інструмента зі слідом у tool_calls: хто (request_id), що (tool), з чим (args), з яким результатом.
    // Незворотна дія спершу резервує ключ ідемпотентності в базі і лише потім виконується.
    // Другий виклик із тим самим ключем не виконує дію вдруге, а повертає збережений результат.
    public static async Task<ToolOutcome> ExecuteTool(ToolSpec? spec, string name, string argsJson, ToolContext ctx, string conn, ILogger logger)
    {
        if (spec is null)
        {
            await LogToolCall(conn, ctx, name, null, argsJson, null, "unknown_tool", null, null, logger);
            return new ToolOutcome("unknown_tool", null);
        }

        JsonElement args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        }
        catch (JsonException)
        {
            args = default;
        }
        if (args.ValueKind != JsonValueKind.Object || !ArgsMatchSchema(args, spec.ArgsSchema))
        {
            await LogToolCall(conn, ctx, name, spec.Category, argsJson, null, "invalid_args", null, null, logger);
            return new ToolOutcome("invalid_args", null);
        }

        var canonicalArgs = CanonicalArgs(args);
        if (spec.Category == ToolCategory.ReadOnly)
        {
            var (status, result, latency) = await RunWithTimeout(spec, args, ctx);
            await LogToolCall(conn, ctx, name, spec.Category, canonicalArgs, null, status, result, latency, logger);
            return new ToolOutcome(status, result);
        }

        // незворотна дія. Без бази ідемпотентність не гарантувати, тому не виконуємо зовсім.
        var key = IdempotencyKey(ctx.ConversationId ?? ctx.RequestId.ToString(), name, canonicalArgs);
        try
        {
            await using var db = new NpgsqlConnection(conn);
            await db.OpenAsync();
            await using var reserve = new NpgsqlCommand(
                "INSERT INTO tool_calls (request_id, conversation_id, tool, category, args, idempotency_key, status) "
                + "VALUES (@rid, @cid, @tool, @cat, @args, @key, 'pending') "
                + "ON CONFLICT (idempotency_key) DO NOTHING RETURNING id", db);
            reserve.Parameters.AddWithValue("rid", ctx.RequestId);
            reserve.Parameters.AddWithValue("cid", (object?)ctx.ConversationId ?? DBNull.Value);
            reserve.Parameters.AddWithValue("tool", name);
            reserve.Parameters.AddWithValue("cat", spec.Category.ToString());
            reserve.Parameters.AddWithValue("args", canonicalArgs);
            reserve.Parameters.AddWithValue("key", key);
            var reservedId = await reserve.ExecuteScalarAsync();

            if (reservedId is null)
            {
                // ключ уже був. Дію не повторюємо, віддаємо результат першого виклику.
                await using var previous = new NpgsqlCommand("SELECT status, result FROM tool_calls WHERE idempotency_key = @key", db);
                previous.Parameters.AddWithValue("key", key);
                await using var reader = await previous.ExecuteReaderAsync();
                await reader.ReadAsync();
                var prevStatus = reader.GetString(0);
                var prevResult = reader.IsDBNull(1) ? null : reader.GetString(1);
                await reader.CloseAsync();
                await LogToolCall(conn, ctx, name, spec.Category, canonicalArgs, null, "duplicate", prevResult, null, logger);
                return new ToolOutcome(prevStatus switch { "ok" => "duplicate", "pending" => "duplicate_pending", _ => "duplicate_unknown" }, prevResult);
            }

            // таймаут незворотної дії лишає статус timeout. Повтор із тим самим ключем не виконає її знову,
            // бо ми не знаємо, чи вона встигла відбутись. Це вирішує оператор.
            var (status, result, latency) = await RunWithTimeout(spec, args, ctx);
            await using var update = new NpgsqlCommand(
                "UPDATE tool_calls SET status = @status, result = @result, latency_ms = @lat WHERE id = @id", db);
            update.Parameters.AddWithValue("status", status);
            update.Parameters.AddWithValue("result", (object?)result ?? DBNull.Value);
            update.Parameters.AddWithValue("lat", latency);
            update.Parameters.AddWithValue("id", reservedId);
            await update.ExecuteNonQueryAsync();
            return new ToolOutcome(status, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "irreversible tool {Tool} not executed for request {RequestId}", name, ctx.RequestId);
            return new ToolOutcome("error", null);
        }
    }

    public static async Task<(string Status, string? Result, int LatencyMs)> RunWithTimeout(ToolSpec spec, JsonElement args, ToolContext ctx)
    {
        var started = DateTimeOffset.UtcNow;
        using var cts = new CancellationTokenSource(spec.Timeout);
        int Elapsed() => (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        try
        {
            // WaitAsync ріже за таймаутом навіть інструмент, який ігнорує токен
            var result = await spec.Run(args, ctx, cts.Token).WaitAsync(spec.Timeout);
            return ("ok", AsData(result), Elapsed());
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return ("timeout", null, Elapsed());
        }
        catch
        {
            return ("error", null, Elapsed());
        }
    }

    // примітка до відповіді за результатом інструмента
    public static string ToolNote(ToolSpec? spec, ToolOutcome outcome) => outcome switch
    {
        { Status: "ok", Result: { } r } => $" ({r})",
        // повтор тієї самої дії. Кажемо, що вона вже була, інакше «створюю тікет» звучить як новий тікет.
        { Status: "duplicate", Result: { } r } => $" (ваш {r} уже створено)",
        { Status: "duplicate_pending" } => " (такий запит уже обробляється)",
        { Status: "timeout" or "error" or "duplicate_unknown" } when spec?.Category == ToolCategory.Irreversible
            => " (не вдалося підтвердити дію, оператор перевірить)",
        { Status: "timeout" or "error" } => " (зараз не вдалося перевірити, спробуйте пізніше)",
        // невідомий інструмент чи аргументи не за схемою дають явну відмову, а не мовчання
        { Status: "unknown_tool" or "invalid_args" } => " (цю дію я виконати не можу)",
        _ => "",
    };

    // мінімальна перевірка за схемою. Лише відомі поля і рядкові значення, обовʼязкові поля на місці.
    public static bool ArgsMatchSchema(JsonElement args, string schemaJson)
    {
        using var schema = JsonDocument.Parse(schemaJson);
        var props = schema.RootElement.GetProperty("properties");
        foreach (var p in args.EnumerateObject())
        {
            if (!props.TryGetProperty(p.Name, out var prop)) return false;
            if (prop.GetProperty("type").GetString() == "string" && p.Value.ValueKind != JsonValueKind.String) return false;
        }
        if (schema.RootElement.TryGetProperty("required", out var required))
        {
            foreach (var r in required.EnumerateArray())
            {
                if (!args.TryGetProperty(r.GetString()!, out _)) return false;
            }
        }
        return true;
    }

    // аргументи з відсортованими полями, щоб той самий виклик з іншим порядком полів дав той самий ключ
    public static string CanonicalArgs(JsonElement args) =>
        JsonSerializer.Serialize(args.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => p.Value));

    // ключ ідемпотентності = hash(розмова, інструмент, аргументи).
    // Розмова, а не HTTP-запит, бо повтор того самого прохання приходить новим запитом.
    // Без conversation_id беремо request_id, і тоді ключ захищає лише від повтору всередині одного запиту.
    public static string IdempotencyKey(string scope, string tool, string canonicalArgs) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\n{tool}\n{canonicalArgs}")));

    // слід tool-виклику. Падіння логу не валить відповідь.
    public static async Task LogToolCall(string conn, ToolContext ctx, string tool, ToolCategory? category, string args,
        string? key, string status, string? result, int? latency, ILogger logger)
    {
        try
        {
            await using var db = new NpgsqlConnection(conn);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO tool_calls (request_id, conversation_id, tool, category, args, idempotency_key, status, result, latency_ms) "
                + "VALUES (@rid, @cid, @tool, @cat, @args, @key, @status, @result, @lat)", db);
            cmd.Parameters.AddWithValue("rid", ctx.RequestId);
            cmd.Parameters.AddWithValue("cid", (object?)ctx.ConversationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("tool", tool);
            cmd.Parameters.AddWithValue("cat", (object?)category?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("args", args);
            cmd.Parameters.AddWithValue("key", (object?)key ?? DBNull.Value);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("result", (object?)result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("lat", (object?)latency ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "tool call log failed for request {RequestId}", ctx.RequestId);
        }
    }

    // результат інструмента це дані, а не істина і не інструкція. Він іде лише в текст для користувача,
    // ніколи в промпт. Обрізаємо довжину і переноси, щоб чужий текст із бекенду не розростався у відповіді.
    public static string AsData(string result)
    {
        var oneLine = string.Join(' ', result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "…";
    }
}

public enum ToolCategory { ReadOnly, Irreversible }
public record ToolSpec(ToolCategory Category, TimeSpan Timeout, string ArgsSchema, Func<JsonElement, ToolContext, CancellationToken, Task<string>> Run);
public record ToolContext(Guid RequestId, string? ConversationId, string Message);
public record ToolOutcome(string Status, string? Result);

// фейковий бекенд замовлень. Модель mock шле порожні аргументи, тому номер беремо з повідомлення.
// Справжня модель заповнила б order_id за схемою з реєстру.
public static class FakeOrders
{
    private static readonly Dictionary<string, string> Orders = new()
    {
        ["123"] = "статус: оплачено, доставку призначено",
        ["10482"] = "статус: оплачено, доставку призначено",
        ["555"] = "статус: доставлено",
    };

    public static async Task<string> Lookup(JsonElement args, string message, CancellationToken ct)
    {
        var id = args.TryGetProperty("order_id", out var v) ? v.GetString() : Regex.Match(message, @"#(\d+)").Groups[1].Value;
        // 99999 імітує повільний бекенд, щоб перевірити гілку таймауту
        if (id == "99999") await Task.Delay(TimeSpan.FromSeconds(3), ct);
        return Orders.TryGetValue(id ?? "", out var status) ? status : "замовлення не знайдено";
    }
}
