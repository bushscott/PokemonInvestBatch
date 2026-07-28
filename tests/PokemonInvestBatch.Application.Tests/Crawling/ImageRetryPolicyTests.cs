using PokemonInvestBatch.Application.Crawling;

namespace PokemonInvestBatch.Application.Tests.Crawling;

public class ImageRetryPolicyTests
{
    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    public void A_client_error_means_the_image_does_not_exist(int status)
    {
        // Fetch-once: never hammer the CDN for an image that is not there.
        Assert.True(ImageRetryPolicy.GiveUp(status));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(429)]
    public void Transient_trouble_leaves_the_image_pending(int status)
    {
        Assert.False(ImageRetryPolicy.GiveUp(status));
    }
}
