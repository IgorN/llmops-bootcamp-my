// юніт-тести надійності (W4). Mock не дає ні таймауту виклику, ні битої відповіді,
// а запобіжник із 30-секундним вікном зручніше перевіряти з контрольованим часом.

using System.Net;
using System.Text;
using Xunit;

public class ReliabilityTests
{
    // HTTP-заглушка, що віддає заданий статус і тіло або висить до скасування
    private sealed class StubHandler(HttpStatusCode code, string body, TimeSpan? delay = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (delay is { } d) await Task.Delay(d, ct);
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }

    private const string OkBody =
        """{"choices":[{"message":{"content":"привіт","tool_calls":[{"function":{"name":"lookup_order","arguments":"{}"}}]}}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""";

    private static Task<GatewayResult> Call(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        Reliability.CallGateway(new HttpClient(handler), "http://gw", "mock-mini", "system", "user", timeout ?? TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Success_is_parsed_into_value()
    {
        var r = await Call(new StubHandler(HttpStatusCode.OK, OkBody));

        Assert.True(r.Ok);
        Assert.Equal(200, r.Status);
        Assert.Null(r.ErrorKind);
        Assert.Equal("привіт", r.Answer);
        Assert.Equal("lookup_order", r.Tool);
        Assert.Equal((5, 3), (r.PromptTokens, r.CompletionTokens));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    public async Task Provider_error_is_a_value_not_an_exception(HttpStatusCode code, int expected)
    {
        var r = await Call(new StubHandler(code, "{}"));

        Assert.False(r.Ok);
        Assert.Equal(expected, r.Status);
        Assert.Equal("status", r.ErrorKind);
        Assert.False(r.UsageKnown);
    }

    [Fact]
    public async Task Hanging_model_hits_timeout()
    {
        var r = await Call(new StubHandler(HttpStatusCode.OK, OkBody, TimeSpan.FromSeconds(10)), TimeSpan.FromMilliseconds(200));

        Assert.False(r.Ok);
        Assert.Equal(0, r.Status);
        Assert.Equal("timeout", r.ErrorKind);
        Assert.InRange(r.LatencyMs, 150, 2000);
    }

    [Fact]
    public async Task No_connection_is_network_error_with_status_zero()
    {
        var r = await Call(new ThrowingHandler());

        Assert.Equal((false, 0, "network"), (r.Ok, r.Status, r.ErrorKind));
    }

    [Fact]
    public async Task Garbage_body_is_bad_response()
    {
        var r = await Call(new StubHandler(HttpStatusCode.OK, "<html>502 Bad Gateway</html>"));

        Assert.Equal((false, 200, "bad_response"), (r.Ok, r.Status, r.ErrorKind));
    }

    [Theory]
    [InlineData("...")]
    [InlineData("")]
    [InlineData("  ?! ")]
    public async Task Answer_without_letters_is_garbage_but_keeps_usage(string content)
    {
        var body = $$$"""{"choices":[{"message":{"content":"{{{content}}}"}}],"usage":{"prompt_tokens":5,"completion_tokens":1}}""";

        var r = await Call(new StubHandler(HttpStatusCode.OK, body));

        Assert.Equal((false, 200, "garbage"), (r.Ok, r.Status, r.ErrorKind));
        Assert.True(r.UsageKnown);  // провайдер токени порахував, тож і ми
    }

    [Fact]
    public async Task Tool_call_without_text_is_not_garbage()
    {
        var body = """{"choices":[{"message":{"content":"","tool_calls":[{"function":{"name":"lookup_order","arguments":"{}"}}]}}],"usage":{"prompt_tokens":5,"completion_tokens":1}}""";

        var r = await Call(new StubHandler(HttpStatusCode.OK, body));

        Assert.True(r.Ok);
        Assert.Equal("lookup_order", r.Tool);
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static Breaker OpenedAt(DateTimeOffset at)
    {
        var b = new Breaker();
        for (var i = 0; i < Breaker.FailureThreshold; i++) b.Record(false, at);
        return b;
    }

    [Fact]
    public void Two_failures_keep_breaker_closed_third_opens_it()
    {
        var b = new Breaker();
        b.Record(false, T0);
        b.Record(false, T0);
        Assert.True(b.TryPass(T0, halfOpen: true));

        b.Record(false, T0);
        Assert.True(b.IsOpen(T0));
        Assert.False(b.TryPass(T0, halfOpen: true));
    }

    [Fact]
    public void Success_resets_the_failure_streak()
    {
        var b = new Breaker();
        b.Record(false, T0);
        b.Record(false, T0);
        b.Record(true, T0);
        b.Record(false, T0);
        Assert.False(b.IsOpen(T0));
    }

    [Fact]
    public void Half_open_lets_one_probe_through_every_5_seconds()
    {
        var b = OpenedAt(T0);

        Assert.False(b.TryPass(T0.AddSeconds(4), halfOpen: true));
        Assert.True(b.TryPass(T0.AddSeconds(5), halfOpen: true));   // пробний запит
        Assert.False(b.TryPass(T0.AddSeconds(6), halfOpen: true));  // другий пробний лише ще через 5 с
    }

    [Fact]
    public void Successful_probe_closes_the_breaker()
    {
        var b = OpenedAt(T0);
        Assert.True(b.TryPass(T0.AddSeconds(5), halfOpen: true));

        b.Record(true, T0.AddSeconds(5));

        Assert.False(b.IsOpen(T0.AddSeconds(5)));
        Assert.True(b.TryPass(T0.AddSeconds(5), halfOpen: true));
    }

    [Fact]
    public void Failed_probe_keeps_it_open_for_another_period()
    {
        var b = OpenedAt(T0);
        Assert.True(b.TryPass(T0.AddSeconds(5), halfOpen: true));

        b.Record(false, T0.AddSeconds(5));

        Assert.True(b.IsOpen(T0.AddSeconds(34)));
    }

    [Fact]
    public void Status_is_degraded_after_failure_and_down_while_open()
    {
        var b = new Breaker();
        Assert.Equal("ok", b.Status(T0));

        b.Record(false, T0);
        Assert.Equal("degraded", b.Status(T0));

        b.Record(false, T0);
        b.Record(false, T0);
        Assert.Equal("down", b.Status(T0));
    }

    [Fact]
    public void Status_stays_degraded_after_open_period_until_first_success()
    {
        var b = OpenedAt(T0);

        Assert.Equal("degraded", b.Status(T0.AddSeconds(31)));  // вікно минуло, але успіху ще не було

        b.Record(true, T0.AddSeconds(31));
        Assert.Equal("ok", b.Status(T0.AddSeconds(31)));
    }

    [Fact]
    public void Without_half_open_provider_stays_blocked_for_whole_period()
    {
        var b = OpenedAt(T0);

        Assert.False(b.TryPass(T0.AddSeconds(5), halfOpen: false));
        Assert.False(b.TryPass(T0.AddSeconds(29), halfOpen: false));
        Assert.True(b.TryPass(T0.AddSeconds(30), halfOpen: false));
    }
}
