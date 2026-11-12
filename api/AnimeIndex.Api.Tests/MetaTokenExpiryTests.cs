using System.Net;
using AnimeIndex.Scraper.Infrastructure.Instagram;

namespace AnimeIndex.Api.Tests;

public class MetaTokenExpiryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void ExpiredToken_OAuthError190_IsExpired()
    {
        // Lo que contesta Meta cuando el token vencido se usa como access_token
        const string body = """
            {"error":{"message":"Error validating access token: Session has expired","type":"OAuthException","code":190,"error_subcode":463}}
            """;

        Assert.True(MetaGraphApiClient.ParseTokenExpiryDays(HttpStatusCode.BadRequest, body, Now) <= 0);
    }

    [Fact]
    public void InvalidToken_IsValidFalse_IsExpired()
    {
        const string body = """{"data":{"is_valid":false,"expires_at":1700000000}}""";

        Assert.True(MetaGraphApiClient.ParseTokenExpiryDays(HttpStatusCode.OK, body, Now) <= 0);
    }

    [Fact]
    public void SystemUserToken_ExpiresAtZero_NeverExpires()
    {
        const string body = """{"data":{"is_valid":true,"expires_at":0,"type":"SYSTEM_USER"}}""";

        Assert.Equal(double.MaxValue, MetaGraphApiClient.ParseTokenExpiryDays(HttpStatusCode.OK, body, Now));
    }

    [Fact]
    public void LongLivedToken_ReturnsDaysLeft()
    {
        var expiresAt = Now.ToUnixTimeSeconds() + 5 * 86400;
        var body = """{"data":{"is_valid":true,"expires_at":""" + expiresAt + "}}";

        Assert.Equal(5, MetaGraphApiClient.ParseTokenExpiryDays(HttpStatusCode.OK, body, Now), precision: 3);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"code":2}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":4,"message":"rate limit"}}""")]
    [InlineData(HttpStatusCode.BadRequest, "<html>gateway</html>")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public void TransientOrUnknown_DoesNotBlockTheRun(HttpStatusCode status, string body)
    {
        Assert.Equal(double.MaxValue, MetaGraphApiClient.ParseTokenExpiryDays(status, body, Now));
    }
}
