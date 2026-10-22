using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaGate.Infrastructure.Waha;

namespace WaGate.Tests;

/// <summary>Garante que o cliente monta as chamadas exatamente como o WAHA espera.</summary>
public class WahaClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? ApiKey)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("X-Api-Key", out var keys);
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body, keys?.FirstOrDefault()));
            var res = respond(request);
            res.RequestMessage = request;
            return res;
        }
    }

    private static (WahaClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://waha:3000/") };
        http.DefaultRequestHeaders.Add("X-Api-Key", "k");
        var opt = Options.Create(new WahaOptions { WebhookUrl = "http://api:8080/waha/webhook", WebhookHmacKey = "segredo" });
        return (new WahaClient(http, opt, NullLogger<WahaClient>.Instance), handler);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SendText_posts_expected_body()
    {
        var (client, h) = Create(_ => Json("""{"id":{"_serialized":"true_5511@c.us_ABC"}}"""));

        var res = await client.SendTextAsync("wg_1", "5511999999999@c.us", "Olá", "reply-id", true, null);

        var call = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/sendText", call.Path);
        Assert.Equal("k", call.ApiKey);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("wg_1", body.RootElement.GetProperty("session").GetString());
        Assert.Equal("5511999999999@c.us", body.RootElement.GetProperty("chatId").GetString());
        Assert.Equal("Olá", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("reply-id", body.RootElement.GetProperty("reply_to").GetString());
        Assert.Equal("true_5511@c.us_ABC", WahaClient.ExtractMessageId(res));
    }

    [Fact]
    public async Task UpsertSession_creates_with_webhook_and_hmac_when_missing()
    {
        var (client, h) = Create(req => req.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("""{"name":"wg_1","status":"STARTING"}""", HttpStatusCode.Created));

        var id = Guid.NewGuid();
        var s = await client.UpsertSessionAsync("wg_1", id, start: true);

        Assert.Equal("STARTING", s.Status);
        var create = h.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("/api/sessions", create.Path);
        using var body = JsonDocument.Parse(create.Body!);
        var root = body.RootElement;
        Assert.True(root.GetProperty("start").GetBoolean());
        var hook = root.GetProperty("config").GetProperty("webhooks")[0];
        Assert.Equal("http://api:8080/waha/webhook", hook.GetProperty("url").GetString());
        Assert.Equal("segredo", hook.GetProperty("hmac").GetProperty("key").GetString());
        Assert.Contains(hook.GetProperty("events").EnumerateArray(), e => e.GetString() == "message.any");
        Assert.Equal(id.ToString(), root.GetProperty("config").GetProperty("metadata").GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task UpsertSession_updates_existing_and_starts_if_stopped()
    {
        var (client, h) = Create(req => req.Method == HttpMethod.Get
            ? Json("""{"name":"wg_1","status":"STOPPED"}""")
            : Json("{}"));

        await client.UpsertSessionAsync("wg_1", Guid.NewGuid(), start: true);

        Assert.Contains(h.Calls, c => c.Method == HttpMethod.Put && c.Path == "/api/sessions/wg_1");
        Assert.Contains(h.Calls, c => c.Method == HttpMethod.Post && c.Path == "/api/sessions/wg_1/start");
    }

    [Fact]
    public async Task GetQr_converts_binary_image_to_base64()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var (client, _) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            r.Content.Headers.ContentType = new("image/png");
            return r;
        });

        var qr = await client.GetQrAsync("wg_1");

        Assert.Equal("image/png", qr!.Mimetype);
        Assert.Equal(Convert.ToBase64String(png), qr.Data);
    }

    [Fact]
    public async Task Errors_surface_as_WahaApiException_with_message()
    {
        var (client, _) = Create(_ => Json("""{"statusCode":422,"message":"Session status is not as expected"}""", HttpStatusCode.UnprocessableEntity));

        var ex = await Assert.ThrowsAsync<WahaApiException>(() =>
            client.SendTextAsync("wg_1", "1@c.us", "x", null, true, null));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("Session status is not as expected", ex.Message);
    }

    [Fact]
    public async Task CheckExists_strips_formatting()
    {
        var (client, h) = Create(_ => Json("""{"numberExists":true,"chatId":"5511999999999@c.us"}"""));

        var r = await client.CheckExistsAsync("wg_1", "+55 (11) 99999-9999");

        Assert.True(r.NumberExists);
        Assert.Equal("/api/contacts/check-exists?phone=5511999999999&session=wg_1", h.Calls[0].Path);
    }
}
