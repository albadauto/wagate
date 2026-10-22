using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure;
using WaGate.Infrastructure.Security;
using WaGate.Infrastructure.Waha;
using WaGate.Infrastructure.Whatsapp;
using WaGate.Shared;

namespace WaGate.Tests;

public class CryptoTests
{
    [Fact]
    public void SignWebhook_matches_reference_hmac()
    {
        const string secret = "whsec_test";
        const string body = """{"event":"message.received"}""";
        const long ts = 1792700000;

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.{body}")));

        Assert.Equal(expected, Crypto.SignWebhook(secret, ts, body));
    }

    [Fact]
    public void VerifyWahaHmac_accepts_valid_and_rejects_tampered()
    {
        var key = "chave-hmac";
        var body = Encoding.UTF8.GetBytes("""{"event":"session.status"}""");
        var header = Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.UTF8.GetBytes(key), body));

        Assert.True(Crypto.VerifyWahaHmac(key, body, header));
        Assert.True(Crypto.VerifyWahaHmac(key, body, header.ToUpperInvariant()));
        Assert.False(Crypto.VerifyWahaHmac(key, Encoding.UTF8.GetBytes("""{"event":"x"}"""), header));
        Assert.False(Crypto.VerifyWahaHmac(key, body, null));
        Assert.False(Crypto.VerifyWahaHmac(key, body, "não-é-hex"));
    }

    [Fact]
    public void NewToken_is_random_and_has_requested_length()
    {
        var a = Crypto.NewToken(32);
        var b = Crypto.NewToken(32);
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
        Assert.StartsWith("whsec_", Crypto.NewSecret());
    }
}

public class ChatIdTests
{
    [Theory]
    [InlineData("5511999999999", "5511999999999@c.us")]
    [InlineData("+55 (11) 99999-9999", "5511999999999@c.us")]
    [InlineData("120363000000000000@g.us", "120363000000000000@g.us")]
    [InlineData("120363000000000000-group", "120363000000000000@g.us")]
    [InlineData("123456789@lid", "123456789@lid")]
    public void ToChatId_normalizes(string input, string expected) => Assert.Equal(expected, ChatIds.ToChatId(input));

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    public void ToChatId_rejects_invalid(string input) => Assert.Throws<ArgumentException>(() => ChatIds.ToChatId(input));

    [Theory]
    [InlineData("5511999999999@c.us", "5511999999999")]
    [InlineData("5511999999999:12@s.whatsapp.net", "5511999999999")]
    [InlineData(null, null)]
    public void ToPhone_extracts_number(string? chatId, string? expected) => Assert.Equal(expected, ChatIds.ToPhone(chatId));
}

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 30)]
    [InlineData(3, 120)]
    [InlineData(8, 21600)]
    [InlineData(50, 21600)]
    public void Delay_follows_schedule(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.Delay(attempt, jitter: false));

    [Fact]
    public void Delay_jitter_stays_within_10_percent()
    {
        for (var i = 0; i < 200; i++)
        {
            var d = RetryPolicy.Delay(1).TotalSeconds;
            Assert.InRange(d, 9, 11);
        }
    }
}

public class WebhookFilterTests
{
    [Theory]
    [InlineData("*", "message.received", true)]
    [InlineData("message.received,message.status", "message.status", true)]
    [InlineData("message.received", "instance.status", false)]
    [InlineData("message.*", "message.reaction", true)]
    [InlineData("message.*", "instance.connected", false)]
    [InlineData(" Message.Received ", "message.received", true)]
    public void Accepts(string events, string evt, bool expected) =>
        Assert.Equal(expected, new Webhook { Events = events }.Accepts(evt));
}

public class WahaMappingTests
{
    [Theory]
    [InlineData("WORKING", InstanceStatus.Working)]
    [InlineData("SCAN_QR_CODE", InstanceStatus.ScanQrCode)]
    [InlineData("STARTING", InstanceStatus.Starting)]
    [InlineData("STOPPED", InstanceStatus.Stopped)]
    [InlineData("FAILED", InstanceStatus.Failed)]
    [InlineData("algo_novo", InstanceStatus.Unknown)]
    public void MapWahaStatus(string waha, InstanceStatus expected) => Assert.Equal(expected, DependencyInjection.MapWahaStatus(waha));

    [Theory]
    [InlineData("""{"id":"true_5511@c.us_ABC"}""", "true_5511@c.us_ABC")]
    [InlineData("""{"id":{"fromMe":true,"_serialized":"true_5511@c.us_XYZ"}}""", "true_5511@c.us_XYZ")]
    [InlineData("""{"key":{"id":"3EB0NOWEB"}}""", "3EB0NOWEB")]
    [InlineData("""{"_data":{"id":{"_serialized":"nested"}}}""", "nested")]
    [InlineData("""{"foo":1}""", null)]
    public void ExtractMessageId_handles_engines(string json, string? expected) =>
        Assert.Equal(expected, WahaClient.ExtractMessageId(JsonDocument.Parse(json).RootElement));

    [Fact]
    public void NormalizeMessage_inbound_text()
    {
        var p = JsonDocument.Parse("""
            { "id": "false_5511988887777@c.us_3EB0", "timestamp": 1792700000, "from": "5511988887777@c.us",
              "to": "5511900000000@c.us", "fromMe": false, "body": "Oi!", "hasMedia": false,
              "_data": { "notifyName": "Carlos" } }
            """).RootElement;

        var m = WahaEventProcessor.NormalizeMessage(p);

        Assert.Equal("false_5511988887777@c.us_3EB0", m.MessageId);
        Assert.Equal("5511988887777@c.us", m.ChatId);
        Assert.Equal("5511988887777", m.Phone);
        Assert.False(m.FromMe);
        Assert.Equal(MessageType.Text, m.Type);
        Assert.Equal("Oi!", m.Text);
        Assert.Equal("Carlos", m.SenderName);
    }

    [Fact]
    public void NormalizeMessage_outbound_image_in_group()
    {
        var p = JsonDocument.Parse("""
            { "id": "true_120363@g.us_AAA", "from": "5511900000000@c.us", "to": "120363@g.us", "fromMe": true,
              "participant": "5511900000000@c.us", "hasMedia": true, "body": "legenda",
              "media": { "url": "http://waha/api/files/a.jpg", "mimetype": "image/jpeg", "filename": null } }
            """).RootElement;

        var m = WahaEventProcessor.NormalizeMessage(p);

        Assert.True(m.FromMe);
        Assert.True(m.IsGroup);
        Assert.Equal("120363@g.us", m.ChatId);
        Assert.Equal(MessageType.Image, m.Type);
        Assert.Equal("http://waha/api/files/a.jpg", m.Media!.Url);
    }

    [Theory]
    [InlineData("audio/ogg; codecs=opus", MessageType.Audio)]
    [InlineData("video/mp4", MessageType.Video)]
    [InlineData("application/pdf", MessageType.Document)]
    [InlineData("image/webp", MessageType.Sticker)]
    public void NormalizeMessage_detects_media_type(string mime, MessageType expected)
    {
        var json = $$"""{ "id": "x", "from": "1@c.us", "fromMe": false, "hasMedia": true, "media": { "mimetype": "{{mime}}" } }""";
        Assert.Equal(expected, WahaEventProcessor.NormalizeMessage(JsonDocument.Parse(json).RootElement).Type);
    }

    [Fact]
    public void NormalizeMessage_location_from_webjs_data()
    {
        var p = JsonDocument.Parse("""{ "id": "x", "from": "1@c.us", "fromMe": false, "_data": { "type": "location", "lat": -23.55, "lng": "-46.63" } }""").RootElement;
        var m = WahaEventProcessor.NormalizeMessage(p);
        Assert.Equal(MessageType.Location, m.Type);
        Assert.Equal(-23.55, m.Latitude);
        Assert.Equal(-46.63, m.Longitude);
    }
}

public class MediaTests
{
    [Fact]
    public void Base64_data_uri_extracts_mime_and_data()
    {
        var f = Media.ToFile(new SendImageRequest { Phone = "1", Base64 = "data:image/png;base64,AAAA" }, "image/jpeg", "image.jpg");
        Assert.Equal("image/png", f.Mimetype);
        Assert.Equal("AAAA", f.Data);
        Assert.Null(f.Url);
    }

    [Fact]
    public void Url_infers_filename_and_mime_from_extension()
    {
        var f = Media.ToFile(new SendDocumentRequest { Phone = "1", Url = "https://x.com/files/contrato.pdf?v=2" }, "application/octet-stream", "arquivo");
        Assert.Equal("contrato.pdf", f.Filename);
        Assert.Equal("application/pdf", f.Mimetype);
        Assert.Equal("https://x.com/files/contrato.pdf?v=2", f.Url);
    }

    [Fact]
    public void Missing_url_and_base64_throws() =>
        Assert.Throws<ArgumentException>(() => Media.ToFile(new SendImageRequest { Phone = "1" }, "image/jpeg", "a.jpg"));
}
