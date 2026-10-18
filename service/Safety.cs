// безпека (W4): вхідні guardrails і черга підтверджень для незворотних дій.
// Винесено з Program.cs, щоб покрити юніт-тестами.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

public static class Guards
{
    // email замінюємо до будь-якого зовнішнього виклику, до ключа кешу і до логів.
    // Модель для відповіді «як скинути пароль» справжньої адреси не потребує, а замаскований текст
    // дає однаковий ключ кешу для двох клієнтів з однаковим питанням.
    // Навчальний regex ловить не всі формати адрес і не замінює повноцінного PII-детектора.
    private static readonly Regex Email = new(@"[\w.\-+]+@[\w\-]+(\.[\w\-]+)+", RegexOptions.Compiled);

    public static string MaskPii(string s) => Email.Replace(s, "[email]");

    // три родини прямого injection з уроку 8. Ловимо намір, а не точну фразу, англійською і українською.
    // Ціль атаки завжди та сама, це системний промпт або інструкції бота. Тому шаблон спрацьовує лише тоді,
    // коли поруч стоїть саме ця ціль, а не будь-які «правила» чи «інструкція до товару».
    // Хибна відмова коштує так само дорого, як пропущена атака, бо за нею клієнт із нормальним питанням.
    // Шаблони це перша лінія: перефразування, яке вони не покривають, ловить лише наступний regression-кейс в evals.
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // ціль атаки, тобто інструкції чи промпт самого бота
    private const string Target =
        @"(system\s+prompt|(your|the|previous|all|prior)\s+(instructions?|prompt)|instructions?|" +
        @"системн\w*\s+промпт\w*|(сво\w*|тво\w*|попередн\w*|ус\w*|вс\w*)\s+(інструкц\w*|промпт\w*)|промпт\w*)";

    // до трьох слів у межах одного речення. Без цього «Забудьте моє питання. Які правила…» зшивалося в атаку.
    private const string SameSentence = @"[^\S\n]+([^\s.!?]+[^\S\n]+){0,3}";

    private static readonly (string Family, Regex Pattern)[] Injection =
    [
        // перезапис ролі: «забудь попередні інструкції», «тепер ти інший бот»
        ("role_override", new(
            @"\b(ignore|disregard|forget|override|ігнор\w*|забудь\w*|не\s+зважай\w*)" + SameSentence + Target +
            @"|\b(you\s+are\s+now|from\s+now\s+on\s+you\s+are|тепер\s+ти|відтепер\s+ти)\s+(\w+\s+){0,2}(bot|assistant|model|ai|бот\w*|асистент\w*|модел\w*)\b",
            Opts)),
        // витяг інструкцій: «покажи свій системний промпт», а не «розкажіть про свої правила доставки»
        ("prompt_extraction", new(
            @"\b(show|print|reveal|repeat|output|tell\s+me|покаж\w*|виведи|виведіть|надрукуй\w*|повтори\w*|розкаж\w*)" + SameSentence + Target,
            Opts)),
        // соціальний тиск: «я розробник, для дебагу мені потрібно…», а не «я адміністратор магазину»
        ("social_pressure", new(
            @"\b(i\s+am|i'm|я)\s+(the\s+|a\s+)?(developer|розробник\w*)\W+(\w+\W+){0,6}(debug\w*|дебаг\w*|bypass|обійти|system\s+prompt|системн\w*\s+промпт\w*|тво\w*\s+інструкц\w*|your\s+instructions)",
            Opts)),
    ];

    // родина атаки або null. Перевірка до виклику моделі, тож атака не коштує токенів і не доходить до промпта.
    public static string? InjectionFamily(string message) =>
        Injection.FirstOrDefault(i => i.Pattern.IsMatch(message)).Family;
}

// заявка на незворотну дію. Стан: pending -> running -> approved | failed, або pending -> expired.
// Message уже замаскований, тож оператор бачить контекст без персональних даних.
public record Approval(
    string Id, string Tool, string ArgsJson, ToolContext Context, DateTimeOffset CreatedAt,
    string Status = "pending", string? Result = null, DateTimeOffset? DecidedAt = null, string? Actor = null);

// черга підтверджень у памʼяті процесу. Після рестарту заявки зникають, а кілька інстансів мають окремі черги.
// Для навчального стенда це свідомо, у робочій системі черга живе в базі.
public class ApprovalQueue(TimeSpan ttl)
{
    private readonly ConcurrentDictionary<string, Approval> items = new();

    // незворотна дія не виконується, а стає заявкою з коротким id
    public Approval Enqueue(string tool, string argsJson, ToolContext context, DateTimeOffset now)
    {
        while (true)
        {
            var approval = new Approval(Guid.NewGuid().ToString("N")[..6], tool, argsJson, context, now);
            if (items.TryAdd(approval.Id, approval)) return approval;
        }
    }

    // атомарне захоплення заявки. TryUpdate міняє pending на running лише якщо стан не змінився з моменту читання,
    // тож з двох одночасних approve дію виконає рівно один. Повтор, невідомий id і прострочена заявка дають null:
    // відповідь однакова, щоб її не можна було використати для перебору id.
    public Approval? TryClaim(string id, string? actor, DateTimeOffset now)
    {
        if (!items.TryGetValue(id, out var current) || current.Status != "pending") return null;
        if (Expired(current, now))
        {
            items.TryUpdate(id, current with { Status = "expired", DecidedAt = now }, current);
            return null;
        }
        var running = current with { Status = "running", Actor = actor };
        return items.TryUpdate(id, running, current) ? running : null;
    }

    // термінальний стан після виконання. Результат лишається в заявці, щоб «рівно один тікет» можна було перевірити.
    public Approval Complete(Approval running, bool ok, string result, DateTimeOffset now)
    {
        var done = running with { Status = ok ? "approved" : "failed", Result = result, DecidedAt = now };
        items[running.Id] = done;
        return done;
    }

    // стан черги. Строк дії перевіряємо при читанні, тож заявка змінює стан на наступному GET, а не за таймером.
    public IReadOnlyList<Approval> Snapshot(DateTimeOffset now)
    {
        foreach (var (id, a) in items)
        {
            if (a.Status == "pending" && Expired(a, now))
                items.TryUpdate(id, a with { Status = "expired", DecidedAt = now }, a);
        }
        return items.Values.OrderBy(a => a.CreatedAt).ToList();
    }

    private bool Expired(Approval a, DateTimeOffset now) => now - a.CreatedAt >= ttl;
}
