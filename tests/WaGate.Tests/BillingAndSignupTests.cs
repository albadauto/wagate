using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure.Billing;
using WaGate.Infrastructure.Waha;
using WaGate.Shared;

namespace WaGate.Tests;

public class BrDocsTests
{
    [Theory]
    [InlineData("529.982.247-25", true)]
    [InlineData("52998224725", true)]
    [InlineData("529.982.247-26", false)]
    [InlineData("111.111.111-11", false)]
    [InlineData("123", false)]
    public void Cpf(string value, bool expected) => Assert.Equal(expected, BrDocs.IsCpf(value));

    [Theory]
    [InlineData("11.222.333/0001-81", true)]
    [InlineData("11222333000181", true)]
    [InlineData("11.222.333/0001-82", false)]
    [InlineData("00.000.000/0000-00", false)]
    public void Cnpj(string value, bool expected) => Assert.Equal(expected, BrDocs.IsCnpj(value));

    [Theory]
    [InlineData("(11) 99999-9999", true)]
    [InlineData("5511999999999", true)]
    [InlineData("(11) 3333-4444", true)]
    [InlineData("(11) 89999-9999", false)]   // celular sem o 9
    [InlineData("(01) 99999-9999", false)]   // DDD inválido
    [InlineData("9999-9999", false)]
    public void Phone(string value, bool expected) => Assert.Equal(expected, BrDocs.IsBrPhone(value));

    [Fact]
    public void NormalizePhone_adds_country_code() => Assert.Equal("5511999999999", BrDocs.NormalizePhone("(11) 99999-9999"));

    [Fact]
    public void Formats_documents()
    {
        Assert.Equal("529.982.247-25", BrDocs.FormatDocument("52998224725"));
        Assert.Equal("11.222.333/0001-81", BrDocs.FormatDocument("11222333000181"));
    }

    [Theory]
    [InlineData("abc", 3)]           // falta tamanho, maiúscula e número
    [InlineData("", 4)]
    [InlineData("abcdefgh", 2)]
    [InlineData("Abcdefg1", 0)]
    public void Password_rules(string password, int issues) => Assert.Equal(issues, BrDocs.PasswordIssues(password).Count);

    [Fact]
    public void Register_request_validation_catches_bad_input()
    {
        var req = new RegisterRequest
        {
            Name = "Jo", Email = "x", Phone = "123", Document = "111.111.111-11",
            Password = "fraca", ConfirmPassword = "outra", AcceptTerms = false
        };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(req, new(req), results, validateAllProperties: true);
        var members = results.SelectMany(r => r.MemberNames).ToHashSet();
        Assert.Superset(new HashSet<string> { "Name", "Email", "Phone", "Document", "Password", "ConfirmPassword", "AcceptTerms" }, members);
        Assert.Contains("AcceptTerms", members);
        Assert.Contains("Document", members);
    }
}

public class StripeTests
{
    private static string Sign(string secret, long ts, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.{payload}")));

    [Fact]
    public void VerifySignature_accepts_valid_header()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1792700000);
        const string payload = """{"id":"evt_1","type":"invoice.paid"}""";
        var header = $"t={now.ToUnixTimeSeconds()},v1={Sign("whsec_x", now.ToUnixTimeSeconds(), payload)},v0=ignored";

        Assert.True(StripeClient.VerifySignature(payload, header, "whsec_x", TimeSpan.FromMinutes(5), now));
    }

    [Fact]
    public void VerifySignature_rejects_wrong_secret_tampering_and_old_events()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1792700000);
        const string payload = """{"id":"evt_1"}""";
        var ts = now.ToUnixTimeSeconds();
        var header = $"t={ts},v1={Sign("whsec_x", ts, payload)}";

        Assert.False(StripeClient.VerifySignature(payload, header, "whsec_outro", TimeSpan.FromMinutes(5), now));
        Assert.False(StripeClient.VerifySignature(payload + " ", header, "whsec_x", TimeSpan.FromMinutes(5), now));
        Assert.False(StripeClient.VerifySignature(payload, header, "whsec_x", TimeSpan.FromMinutes(5), now.AddMinutes(10)));
        Assert.False(StripeClient.VerifySignature(payload, null, "whsec_x", TimeSpan.FromMinutes(5), now));
    }

    private const string TrialingSub = """
        { "id": "sub_123", "customer": "cus_9", "status": "trialing", "cancel_at_period_end": false,
          "trial_end": 1793300000, "current_period_end": 1793300000,
          "metadata": { "plan": "Ultimate", "accountId": "6c2f3a3e-1111-4c4c-9d9d-000000000001" },
          "items": { "data": [ { "id": "si_1", "quantity": 3, "price": { "id": "price_1", "lookup_key": "wagate_ultimate_9900_brl_month" } } ] } }
        """;

    [Fact]
    public void Parse_reads_subscription_fields()
    {
        var s = StripeSubscription.Parse(JsonDocument.Parse(TrialingSub).RootElement);
        Assert.Equal("sub_123", s.Id);
        Assert.Equal("cus_9", s.CustomerId);
        Assert.Equal("si_1", s.ItemId);
        Assert.Equal(3, s.Quantity);
        Assert.Equal(BillingPlan.Ultimate, s.Plan);
        Assert.NotNull(s.TrialEnd);
    }

    [Fact]
    public void Parse_supports_period_end_on_item_and_plan_from_lookup_key()
    {
        const string json = """
            { "id": "sub_2", "customer": { "id": "cus_2" }, "status": "active",
              "items": { "data": [ { "id": "si_2", "quantity": 25, "current_period_end": 1793300000,
                                     "price": { "lookup_key": "wagate_partner_8990_brl_month" } } ] } }
            """;
        var s = StripeSubscription.Parse(JsonDocument.Parse(json).RootElement);
        Assert.Equal("cus_2", s.CustomerId);
        Assert.Equal(BillingPlan.Partner, s.Plan);
        Assert.NotNull(s.CurrentPeriodEnd);
    }

    [Fact]
    public void Apply_updates_account_and_marks_trial_used()
    {
        var a = new Account();
        BillingService.Apply(a, StripeSubscription.Parse(JsonDocument.Parse(TrialingSub).RootElement));

        Assert.Equal(BillingPlan.Ultimate, a.Plan);
        Assert.Equal("trialing", a.SubscriptionStatus);
        Assert.Equal(3, a.PlanQuantity);
        Assert.True(a.TrialUsed);
        Assert.True(a.HasUsableSubscription);
        Assert.Equal("cus_9", a.StripeCustomerId);
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("trialing", true)]
    [InlineData("past_due", true)]
    [InlineData("canceled", false)]
    [InlineData("unpaid", false)]
    [InlineData("none", false)]
    public void Usable_statuses(string status, bool usable) => Assert.Equal(usable, new Account { SubscriptionStatus = status }.HasUsableSubscription);

    [Fact]
    public void Default_plans_match_pricing()
    {
        var o = new BillingOptions();
        Assert.Equal(9900, o.Ultimate.UnitAmount);
        Assert.Equal(7, o.Ultimate.TrialDays);
        Assert.Equal(8990, o.Partner.UnitAmount);
        Assert.Equal(10, o.Partner.MinQuantity);
        Assert.Equal(0, o.Partner.TrialDays);
    }
}

public class WahaSessionConfigTests
{
    private static readonly WahaOptions Opt = new() { WebhookUrl = "http://api:8080/waha/webhook", WebhookHmacKey = "k" };

    private static JsonElement Config(string url, string[] events, string? key) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        webhooks = new[] { new { url, events, hmac = key is null ? null : new { key } } }
    })).RootElement;

    [Fact]
    public void Matches_when_url_events_and_key_equal() =>
        Assert.True(WahaClient.WebhookConfigMatches(Config(Opt.WebhookUrl, Opt.Events, "k"), Opt));

    [Fact]
    public void Detects_changed_url_events_or_key()
    {
        Assert.False(WahaClient.WebhookConfigMatches(Config("http://outro/waha/webhook", Opt.Events, "k"), Opt));
        Assert.False(WahaClient.WebhookConfigMatches(Config(Opt.WebhookUrl, ["message"], "k"), Opt));
        Assert.False(WahaClient.WebhookConfigMatches(Config(Opt.WebhookUrl, Opt.Events, "antiga"), Opt));
        Assert.False(WahaClient.WebhookConfigMatches(null, Opt));
    }

    [Fact]
    public async Task Upsert_does_not_restart_a_running_session_with_same_config()
    {
        var calls = new List<string>();
        var sessionJson = JsonSerializer.Serialize(new { name = "wg_1", status = "WORKING", config = Config(Opt.WebhookUrl, Opt.Events, "k") });
        var handler = new Stub(req =>
        {
            calls.Add($"{req.Method} {req.RequestUri!.AbsolutePath}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson, Encoding.UTF8, "application/json") };
        });
        var client = new WahaClient(new HttpClient(handler) { BaseAddress = new Uri("http://waha/") }, Options.Create(Opt), NullLogger<WahaClient>.Instance);

        var s = await client.UpsertSessionAsync("wg_1", Guid.NewGuid(), start: true);

        Assert.Equal("WORKING", s.Status);
        Assert.Equal(["GET /api/sessions/wg_1"], calls); // nenhum PUT (que reiniciaria) e nenhum start
    }

    [Fact]
    public async Task Upsert_restarts_failed_session()
    {
        var calls = new List<string>();
        var sessionJson = JsonSerializer.Serialize(new { name = "wg_1", status = "FAILED", config = Config(Opt.WebhookUrl, Opt.Events, "k") });
        var handler = new Stub(req =>
        {
            calls.Add($"{req.Method} {req.RequestUri!.AbsolutePath}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson, Encoding.UTF8, "application/json") };
        });
        var client = new WahaClient(new HttpClient(handler) { BaseAddress = new Uri("http://waha/") }, Options.Create(Opt), NullLogger<WahaClient>.Instance);

        await client.UpsertSessionAsync("wg_1", Guid.NewGuid(), start: true);

        Assert.Contains("POST /api/sessions/wg_1/restart", calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("PUT"));
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var res = f(r);
            res.RequestMessage = r;
            return Task.FromResult(res);
        }
    }
}
