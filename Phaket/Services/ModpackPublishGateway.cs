using System.Text.RegularExpressions;

namespace Phaket.Services;

// Signed publication of a local modpack. ModpackApiClient already carries the call but the
// catalog view model only depends on the search contract, so publication goes through this
// narrow interface and tests can fake it without an HTTP client.
public interface IModpackPublisher
{
    Task<ModpackPublishResult> PublishAsync(ModpackDefinition package, string slug, bool createNew = true,
        CancellationToken cancellationToken = default);
}

public static class ModpackPublishGateway
{
    public static IModpackPublisher? For(IModpackApiClient api) => api switch
    {
        IModpackPublisher publisher => publisher,
        ModpackApiClient client => new ModpackApiClientPublisher(client),
        _ => null
    };

    // Catalog slugs are ASCII, lower case and stable across languages: the slug remembered from
    // an earlier publication wins, otherwise the pack name is transliterated into one.
    public static string SlugOf(ModpackDefinition package) => IsValidSlug(package.Slug) ? package.Slug! : Sanitize(package.Name);

    public static bool IsValidSlug(string? value) => value is { Length: > 0 and <= 80 }
        && Regex.IsMatch(value, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private static string Sanitize(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var ascii = new string(normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
            System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = Regex.Replace(ascii.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 70) slug = slug[..70].TrimEnd('-');
        return IsValidSlug(slug) ? slug : "modpack";
    }

    private sealed class ModpackApiClientPublisher(ModpackApiClient client) : IModpackPublisher
    {
        public Task<ModpackPublishResult> PublishAsync(ModpackDefinition package, string slug, bool createNew = true,
            CancellationToken cancellationToken = default) => client.PublishAsync(package, slug, createNew, cancellationToken);
    }
}
