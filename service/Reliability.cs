// надійність (W4): один виклик моделі як значення і запобіжник на кожну модель.
// Винесено з Program.cs, щоб покрити юніт-тестами.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

// результат одного виклику. Збій це значення, а не виняток, тому ланцюг може спробувати наступну модель.
// ErrorKind розрізняє, що сталось: status (провайдер відповів 4xx/5xx), timeout, network (відповіді не було),
// bad_response (відповідь не розпарсилась), garbage (200 і валідний JSON, але змісту немає).
// Без нього таймаут і розрив зʼєднання в лозі виглядали б однаково.
public record GatewayResult(
    bool Ok, int Status, string? ErrorKind, string Answer, string? Tool, string ToolArgs,
    int PromptTokens, int CompletionTokens, bool UsageKnown, int LatencyMs);

public static class Reliability
{
    public static async Task<GatewayResult> CallGateway(
        HttpClient http, string gateway, string model, string systemPrompt, string userMessage, TimeSpan timeout)
    {
        var started = DateTimeOffset.UtcNow;
        int Elapsed() => (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        GatewayResult Fail(int status, string kind) => new(false, status, kind, "", null, "{}", 0, 0, false, Elapsed());

        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            }
        });

        var status = 0;  // 0 означає, що HTTP-відповіді не було взагалі
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var response = await http.PostAsync(
                $"{gateway}/v1/chat/completions",
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cts.Token);
            status = (int)response.StatusCode;
            if (status >= 400) return Fail(status, "status");

            var rawJson = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(rawJson);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var answer = message.GetProperty("content").GetString() ?? "";

            // tools (W3): модель лише просить інструмент, тут читаємо назву й аргументи
            string? tool = null;
            var toolArgs = "{}";
            if (message.TryGetProperty("tool_calls", out var tools)
                && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            {
                var function = tools[0].GetProperty("function");
                tool = function.GetProperty("name").GetString();
                toolArgs = function.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
            }

            var usage = doc.RootElement.GetProperty("usage");
            var promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            var completionTokens = usage.GetProperty("completion_tokens").GetInt32();

            // W5: успішна, але непридатна відповідь це збій формату, а не успіх. Текст без жодної літери
            // («...», порожньо) користувачу нічого не каже. Токени провайдер однаково порахував, тож usage лишаємо.
            // Відповідь з інструментом може бути без тексту, її не чіпаємо.
            if (tool is null && !answer.Any(char.IsLetter))
                return new GatewayResult(false, status, "garbage", "", null, "{}", promptTokens, completionTokens, true, Elapsed());

            return new GatewayResult(true, status, null, answer, tool, toolArgs, promptTokens, completionTokens, true, Elapsed());
        }
        catch (OperationCanceledException)
        {
            return Fail(status, "timeout");
        }
        catch (HttpRequestException)
        {
            return Fail(status, "network");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return Fail(status, "bad_response");
        }
    }
}

// запобіжник однієї моделі. closed пропускає все. Після 3 збоїв поспіль open не пускає 30 с,
// і запити одразу йдуть до наступної моделі ланцюга, а не чекають на мертвого провайдера.
// half-open раз на 5 с пропускає один пробний запит, і успіх закриває запобіжник.
// Без half-open відновлений провайдер лишається заблокованим до кінця всього періоду open.
public class Breaker
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private int fails;
    private DateTimeOffset openUntil;
    private DateTimeOffset nextProbe;

    public bool IsOpen(DateTimeOffset now)
    {
        lock (gate) return openUntil > now;
    }

    // статус для консолі (W5). down, поки запобіжник відкритий. degraded, коли останні виклики падали,
    // зокрема після кінця вікна open, бо успіху ще не було. ok повертає перший успішний виклик.
    public string Status(DateTimeOffset now)
    {
        lock (gate) return openUntil > now ? "down" : fails > 0 ? "degraded" : "ok";
    }

    // чи можна зараз звернутись до моделі
    public bool TryPass(DateTimeOffset now, bool halfOpen)
    {
        lock (gate)
        {
            if (openUntil <= now) return true;
            if (!halfOpen || now < nextProbe) return false;
            nextProbe = now + ProbeEvery;
            return true;
        }
    }

    public void Record(bool ok, DateTimeOffset now)
    {
        lock (gate)
        {
            if (ok)
            {
                fails = 0;
                openUntil = default;
                return;
            }
            if (++fails >= FailureThreshold)
            {
                openUntil = now + OpenFor;
                nextProbe = now + ProbeEvery;  // перший пробний запит через 5 с, а не одразу
            }
        }
    }
}

// таксономія збоїв (W5): окремий лічильник на кожну причину, бо кожна веде до своєї дії.
// provider_429 означає вичерпані ліміти, тож розводимо трафік і домовляємось про квоту.
// tool_timeout означає, що не відповіла зовнішня система, і лагодити треба інтеграцію, а не модель.
// guardrail_refusal це успіх захисту, а не збій, тому в error rate його немає.
// db_failure означає, що лог неповний і плитки з бази показують менше, ніж було насправді.
// Лічильники в памʼяті процесу, бо збій бази в самій базі не порахувати. Тож вікно тут від старту процесу, а не за день.
// Статичний клас, бо рахують і /chat, і ToolRuntime, а тягнути лічильник у кожен їхній виклик заради цього зайве.
public static class ErrorTaxonomy
{
    public const string Provider429 = "provider_429";
    public const string ToolTimeout = "tool_timeout";
    public const string GuardrailRefusal = "guardrail_refusal";
    public const string DbFailure = "db_failure";

    // усі причини з нулем від старту, щоб «не було жодного» відрізнялось від «не рахується»
    private static readonly ConcurrentDictionary<string, long> Counts = new(
        new[] { Provider429, ToolTimeout, GuardrailRefusal, DbFailure }.Select(k => KeyValuePair.Create(k, 0L)));

    public static void Count(string kind) => Counts.AddOrUpdate(kind, 1, (_, n) => n + 1);

    public static long Get(string kind) => Counts.GetValueOrDefault(kind);

    public static IReadOnlyDictionary<string, long> Snapshot() => new SortedDictionary<string, long>(Counts);
}
