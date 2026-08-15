using System.Net;
using Microsoft.Extensions.Logging;

namespace PokemonInvestBatch.Infrastructure.Pokedex;

/// <summary>
/// One run of <see cref="SpeciesIconStore.FetchMissingAsync"/>: how many
/// icons this call wrote from each tier of the fallback chain, how many
/// files already existed and needed no request, and how many species have
/// no icon at either tier — with the dex numbers, so a gap is traceable to
/// a species rather than just a count. These are the spec §7 receipt
/// numbers the Pokédex lane logs every sweep.
/// </summary>
public sealed record IconFetchResult
{
    /// <summary>Written from the gen-viii menu icon — the fallback chain's
    /// first, preferred tier.</summary>
    public required int FromMenuIcons { get; init; }

    /// <summary>Written from the default front sprite, for a species the
    /// gen-viii menu set never drew — the fallback tier, not the
    /// preference.</summary>
    public required int FromDefaultSprites { get; init; }

    /// <summary>Already had a file on disk from an earlier run, so no
    /// request was made for it. This is what makes calling
    /// <see cref="SpeciesIconStore.FetchMissingAsync"/> with the full dex
    /// list every sweep cheap after the first one.</summary>
    public required int Skipped { get; init; }

    /// <summary>Neither tier had an icon. A recorded state, not an error:
    /// the product already renders a gradient-tile fallback for "no icon"
    /// as a normal state (spec §2), so this count and
    /// <see cref="MissingDexNumbers"/> are what the lane logs, not
    /// something it retries within the same sweep.</summary>
    public required int Missing { get; init; }

    /// <summary>The dex numbers counted in <see cref="Missing"/>, in the
    /// order <c>dexNumbers</c> was walked.</summary>
    public required IReadOnlyList<int> MissingDexNumbers { get; init; }
}

/// <summary>
/// One retro pixel icon per species, fetched once from the pinned PokéAPI
/// sprites mirror (ADR-0011) and written verbatim to
/// <c>{iconDirectory}/{dex}.png</c>. <see cref="FetchMissingAsync"/> is
/// meant to be called every sweep with the full dex list (~1,025 species):
/// a dex whose file already exists is skipped with no request at all, so
/// the cost after the first full run is proportional only to species still
/// missing an icon — new entries from a future re-pin, or gaps an earlier
/// sweep recorded that this one gets another chance at.
///
/// Per dex, in order: the gen-viii menu icon
/// (<c>sprites/pokemon/versions/generation-viii/icons/{dex}.png</c>) and,
/// on 404 only, the default front sprite (<c>sprites/pokemon/{dex}.png</c>).
/// 404 at both tiers is a recorded gap — logged, counted into
/// <see cref="IconFetchResult.Missing"/>, no file written, no exception —
/// because the product already renders a gradient-tile fallback for "no
/// icon" as a normal state (spec §2), not a failure this store needs to
/// escalate.
///
/// Any other response — a 5xx, an unexpected status, a transport error —
/// throws immediately, the same loud-failure posture <see cref="PokeapiMirror"/>
/// takes via <c>EnsureSuccessStatusCode</c>: only 404 means "PokéAPI truly
/// has nothing here," so nothing else may advance the chain or land in
/// <see cref="IconFetchResult.Missing"/> — a gap must never actually mean
/// "the CDN hiccuped." The lane's sweep loop catches, logs, and retries the
/// whole sweep next time.
///
/// Deliberately unlike <see cref="PokeapiMirror"/>: a failure here does not
/// delete icons this call already wrote. The mirror's unit of completeness
/// is its whole directory — a manifest promises every file behind it is
/// present, so a half-written mirror must look like no mirror at all. This
/// store keeps no such promise; its unit of completeness is one file, and
/// the skip-if-exists check below is exactly what treats each
/// <c>{dex}.png</c> as already complete the moment it lands. Discarding
/// icons that landed correctly earlier in a call that later hit a 500 would
/// throw away real, correct work for no reason — an idempotent re-run
/// simply resumes at the first dex still missing a file.
///
/// Because the skip gate trusts existence alone, existence must imply
/// completeness — so every write lands via <see cref="WriteAtomicallyAsync"/>
/// (a same-directory temp file, then a rename) rather than a direct write to
/// <c>{dex}.png</c>. A process kill mid-write — a systemd restart, an OOM
/// kill on the Pi, both real on that box — must never leave a partial or
/// zero-byte file behind for a future sweep to silently treat as done.
/// </summary>
public static class SpeciesIconStore
{
    /// <summary>The gen-viii menu-icon tier sits under this subpath of
    /// <c>sprites/pokemon</c>; the default-sprite tier is
    /// <c>sprites/pokemon</c> itself.</summary>
    private const string MenuIconSubpath = "versions/generation-viii/icons";

    /// <summary>Spacing between actual requests — matches
    /// <see cref="PokeapiMirror.FetchSpacing"/>. Skipped dex numbers incur
    /// no delay at all, only real fetches do, so a fully-warm run
    /// (everything already on disk) costs nothing beyond the
    /// file-existence checks.</summary>
    public static readonly TimeSpan FetchSpacing = TimeSpan.FromMilliseconds(50);

    /// <summary>Walks <paramref name="dexNumbers"/> in order, writing
    /// whichever icons are missing from <paramref name="iconDirectory"/>
    /// and reporting where each one came from. Never throws for a species
    /// with no icon at either tier — see the class doc for why — but
    /// throws immediately, and leaves already-written files in place, for
    /// anything else.</summary>
    public static async Task<IconFetchResult> FetchMissingAsync(
        HttpClient http,
        string baseUrl,
        string pin,
        string iconDirectory,
        IReadOnlyList<int> dexNumbers,
        ILogger log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(iconDirectory);
        var root = $"{baseUrl}{pin}/sprites/pokemon";

        var fromMenuIcons = 0;
        var fromDefaultSprites = 0;
        var skipped = 0;
        var missingDexNumbers = new List<int>();

        foreach (var dex in dexNumbers)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(iconDirectory, $"{dex}.png");
            if (File.Exists(file))
            {
                skipped++;
                continue;
            }

            await Task.Delay(FetchSpacing, ct);
            var menuIcon = await TryFetchAsync(http, $"{root}/{MenuIconSubpath}/{dex}.png", ct);
            if (menuIcon is not null)
            {
                await WriteAtomicallyAsync(file, menuIcon, ct);
                fromMenuIcons++;
                continue;
            }

            await Task.Delay(FetchSpacing, ct);
            var defaultSprite = await TryFetchAsync(http, $"{root}/{dex}.png", ct);
            if (defaultSprite is not null)
            {
                await WriteAtomicallyAsync(file, defaultSprite, ct);
                fromDefaultSprites++;
                continue;
            }

            missingDexNumbers.Add(dex);
            log.LogWarning(
                "No PokéAPI icon for species dex {Dex} — 404 at both the gen-viii menu icon and the " +
                "default sprite; recording a gap for the gradient-tile fallback to render instead",
                dex);
        }

        return new IconFetchResult
        {
            FromMenuIcons = fromMenuIcons,
            FromDefaultSprites = fromDefaultSprites,
            Skipped = skipped,
            Missing = missingDexNumbers.Count,
            MissingDexNumbers = missingDexNumbers,
        };
    }

    /// <summary>Writes <paramref name="bytes"/> to a fixed, same-directory
    /// temp path (<c>{file}.tmp</c>) and then <see cref="File.Move(string,
    /// string, bool)"/>s it onto <paramref name="file"/> with overwrite —
    /// a POSIX rename within one directory is atomic, so <paramref
    /// name="file"/> only ever shows its old complete content or its new
    /// complete content, never a partial write. The skip-if-exists gate at
    /// the top of <see cref="FetchMissingAsync"/> has no other way to tell
    /// "complete" from "in progress," so this is what makes that check
    /// trustworthy rather than merely convenient.
    ///
    /// The temp name is fixed per dex, not GUID-suffixed: a stale
    /// <c>.tmp</c> left behind by an earlier crash (a systemd restart, an
    /// OOM kill, mid-write) is simply overwritten by the next attempt at
    /// that same dex — <see cref="File.WriteAllBytesAsync(string, byte[],
    /// CancellationToken)"/> truncates an existing file rather than
    /// erroring — instead of accumulating as orphaned garbage under a fresh
    /// GUID every retry.</summary>
    private static async Task WriteAtomicallyAsync(string file, byte[] bytes, CancellationToken ct)
    {
        var tmp = $"{file}.tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>One tier of the fallback chain: the bytes at <paramref
    /// name="url"/> on 200, null on 404 (the caller's cue to try the next
    /// tier or give up), or an exception for anything else —
    /// <c>EnsureSuccessStatusCode</c> throws the same
    /// <see cref="HttpRequestException"/> <see cref="PokeapiMirror"/> does,
    /// so callers of both mirrors see one consistent failure shape.</summary>
    private static async Task<byte[]?> TryFetchAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
