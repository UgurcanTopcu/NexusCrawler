using Scrapper.Models;
using System.Text;

namespace Scrapper.Services;

/// <summary>
/// Thin wrapper over the Scrape.do API for plain (non-rendered) GET requests.
///
/// Akakce answers a direct HttpClient with 403, so every request has to go through
/// the proxy. Rendering is deliberately NOT used: everything this project needs from
/// an Akakce page - the aggregate price summary, the seller sample and the
/// first-party retailer block - is present in the server-rendered HTML, and a plain
/// request costs a fraction of a rendered one.
/// </summary>
public class ScrapeDoFetcher
{
    private readonly HttpClient _httpClient;
    private readonly ScrapeDoConfig _config = new();

    public ScrapeDoFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Fetch a URL through Scrape.do and return the body decoded as UTF-8.
    /// </summary>
    /// <remarks>
    /// The body is decoded as UTF-8 explicitly rather than via ReadAsStringAsync:
    /// Scrape.do does not always send a charset, and the Latin-1 fallback mangles
    /// Turkish text (Koctas comes back as "KoÃ§taÅ").
    /// </remarks>
    public async Task<(bool Ok, int Status, string Html)> GetHtmlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var apiUrl = $"{_config.BaseUrl}?url={Uri.EscapeDataString(url)}&token={_config.ApiToken}";

        using var response = await _httpClient.GetAsync(apiUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (false, (int)response.StatusCode, string.Empty);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (true, (int)response.StatusCode, Encoding.UTF8.GetString(bytes));
    }
}
