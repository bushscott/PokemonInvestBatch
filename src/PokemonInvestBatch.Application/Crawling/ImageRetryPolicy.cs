namespace PokemonInvestBatch.Application.Crawling;

/// <summary>
/// Fetch-once applies to images that provably do not exist (client errors);
/// transient CDN trouble must leave the card pending so the next sweep
/// retries, instead of silently giving up on the image forever.
/// </summary>
public static class ImageRetryPolicy
{
    public static bool GiveUp(int statusCode) =>
        statusCode is >= 400 and < 500 and not 429;
}
