using Scrapper.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace Scrapper.Services;

/// <summary>
/// Service for generating wsrv.nl CDN URLs for product images.
/// wsrv.nl is a free, high-performance image cache and resizing service backed by Cloudflare's global network.
/// It proxies and optimizes images on-the-fly, requiring no manual uploads.
/// </summary>
/// <remarks>
/// Official Documentation: https://wsrv.nl/docs/
/// </remarks>
public class WsrvImageService
{
    private const string WsrvBaseUrl = "https://wsrv.nl/";

    #region Configuration Properties

    /// <summary>
    /// Gets or sets the target width of the processed image in pixels.
    /// If null, the aspect ratio is maintained based on the height.
    /// Default is 1000.
    /// </summary>
    public int? TargetWidth { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the target height of the processed image in pixels.
    /// If null, the aspect ratio is maintained based on the width.
    /// Default is 1000.
    /// </summary>
    public int? TargetHeight { get; set; } = 1000;

    /// <summary>
    /// Gets or sets how the image is fitted to its target dimensions.
    /// Supported values: "cover", "contain", "fill", "inside", "outside".
    /// Default is "cover".
    /// </summary>
    public string Fit { get; set; } = "cover";

    /// <summary>
    /// Gets or sets how the image is aligned when <see cref="Fit"/> is set to "cover" or "contain".
    /// Supported values include standard positions ("center", "top", "bottom", "left", "right") 
    /// as well as smart crop parameters ("attention", "entropy").
    /// Default is "center".
    /// </summary>
    public string Alignment { get; set; } = "center";

    /// <summary>
    /// Gets or sets whether to prevent enlarging the image if its original dimensions are smaller than specified.
    /// Appends the 'we' parameter when true.
    /// Default is true.
    /// </summary>
    public bool WithoutEnlargement { get; set; } = true;

    /// <summary>
    /// Gets or sets the desired output image format.
    /// Supported formats: "webp", "jpg", "png", "gif", "tiff", "jxl".
    /// Default is "webp" for modern compression.
    /// </summary>
    public string OutputFormat { get; set; } = "webp";

    /// <summary>
    /// Gets or sets the output quality of the image. Applies to lossy formats (jpg, webp, jxl, tiff).
    /// Value must be between 1 and 100. Default is 80.
    /// </summary>
    public int? Quality { get; set; } = 80;

    /// <summary>
    /// Gets or sets an optional absolute fallback image URL to return if the source image fails to load.
    /// Maps to the 'default=' parameter.
    /// </summary>
    public string? DefaultFallbackUrl { get; set; }

    /// <summary>
    /// Gets or sets custom Cache-Control max-age directive (e.g. "31d" or "1y").
    /// Values must be between "1d" and "1y" inclusive. Default is null (wsrv.nl default of 1 year).
    /// </summary>
    public string? MaxAge { get; set; }

    /// <summary>
    /// Gets or sets whether to keep and properly URL-encode the original query parameters of the source image.
    /// Many CDNs (such as AWS S3, Shopify, or Azure Blob) rely on signatures or parameters in query strings to authenticate/retrieve images.
    /// Default is true.
    /// </summary>
    public bool KeepOriginalQueryString { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to process all frames of animated images (GIF/WebP).
    /// Appends 'n=-1' when true. If false, only the first frame is processed.
    /// Default is false.
    /// </summary>
    public bool ProcessAllAnimatedFrames { get; set; } = false;

    /// <summary>
    /// Gets or sets the background color to apply when rendering transparent images or canvas padding.
    /// Supports hex colors (e.g., "white", "ff0000", "000000").
    /// </summary>
    public string? BackgroundColor { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of product images to process in one batch.
    /// Default is 3 (1 main image + 2 additional images).
    /// </summary>
    public int MaxImagesToProcess { get; set; } = 3;

    #endregion

    /// <summary>
    /// Process product images by converting them to optimized wsrv.nl CDN URLs.
    /// No download or manual upload is required; wsrv.nl proxies and caches the source URLs globally.
    /// </summary>
    /// <param name="product">The product containing the image URLs.</param>
    /// <param name="onProgressMessage">An optional asynchronous callback for progress reporting.</param>
    /// <returns>A tuple containing the optimized main image URL and a list of additional image URLs.</returns>
    public async Task<(string? mainImage, List<string> additionalImages)> ProcessProductImagesAsync(
        ProductInfo product,
        Func<string, Task>? onProgressMessage = null)
    {
        var mainImageUrl = string.Empty;
        var additionalImageUrls = new List<string>();

        if (product == null)
        {
            if (onProgressMessage != null)
            {
                await onProgressMessage("❌ Product is null; aborting image processing.");
            }
            return (null, new List<string>());
        }

        try
        {
            var allImageUrls = product.GetAllImages();

            if (allImageUrls == null || allImageUrls.Count == 0)
            {
                if (onProgressMessage != null)
                {
                    await onProgressMessage("⚠️ No images found for product.");
                }
                return (null, new List<string>());
            }

            // Deduplicate to avoid redundant processing of identical source URLs
            var uniqueImageUrls = allImageUrls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxImagesToProcess)
                .ToList();

            if (uniqueImageUrls.Count == 0)
            {
                if (onProgressMessage != null)
                {
                    await onProgressMessage("⚠️ No valid non-empty images found for product.");
                }
                return (null, new List<string>());
            }

            if (onProgressMessage != null)
            {
                await onProgressMessage($"📸 Processing {uniqueImageUrls.Count} unique images via wsrv.nl...");
            }

            // Process main image
            mainImageUrl = ConvertToWsrvUrl(uniqueImageUrls[0]);

            // Process additional images
            for (int i = 1; i < uniqueImageUrls.Count; i++)
            {
                var wsrvUrl = ConvertToWsrvUrl(uniqueImageUrls[i]);
                if (!string.IsNullOrEmpty(wsrvUrl))
                {
                    additionalImageUrls.Add(wsrvUrl);
                }
            }

            if (onProgressMessage != null)
            {
                await onProgressMessage($"✅ Successfully optimized {uniqueImageUrls.Count} image URLs via wsrv.nl");
            }

            return (mainImageUrl, additionalImageUrls);
        }
        catch (Exception ex)
        {
            if (onProgressMessage != null)
            {
                await onProgressMessage($"❌ Error converting images: {ex.Message}");
            }
            return (null, new List<string>());
        }
    }

    /// <summary>
    /// Converts a source image URL to a highly optimized wsrv.nl CDN URL based on the class properties.
    /// </summary>
    /// <param name="originalUrl">The source image URL to proxy and optimize.</param>
    /// <returns>The generated wsrv.nl CDN URL, or the original URL if transformation is skipped.</returns>
    public string ConvertToWsrvUrl(string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
            return string.Empty;

        try
        {
            var targetUrl = originalUrl.Trim();

            // Handle protocol-relative URLs (e.g., //example.com/image.jpg)
            if (targetUrl.StartsWith("//"))
            {
                targetUrl = "https:" + targetUrl;
            }
            // If it is not an absolute HTTP/HTTPS URL, wsrv.nl cannot proxy it. Return as-is.
            else if (!targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return originalUrl;
            }

            // Clean the URL query parameters only if explicitly requested
            if (!KeepOriginalQueryString)
            {
                var queryStartIndex = targetUrl.IndexOf('?');
                if (queryStartIndex != -1)
                {
                    targetUrl = targetUrl.Substring(0, queryStartIndex);
                }
            }

            // Encode the target URL to be safe as a query parameter
            var encodedUrl = HttpUtility.UrlEncode(targetUrl);

            var queryParams = new List<string>
            {
                $"url={encodedUrl}"
            };

            // Apply size parameters
            if (TargetWidth.HasValue)
            {
                queryParams.Add($"w={TargetWidth.Value}");
            }

            if (TargetHeight.HasValue)
            {
                queryParams.Add($"h={TargetHeight.Value}");
            }

            // Apply fit strategy
            if (!string.IsNullOrWhiteSpace(Fit))
            {
                queryParams.Add($"fit={HttpUtility.UrlEncode(Fit)}");
            }

            // Apply alignment (only meaningful if fit is cover or contain)
            if (!string.IsNullOrWhiteSpace(Alignment) &&
                (string.Equals(Fit, "cover", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Fit, "contain", StringComparison.OrdinalIgnoreCase)))
            {
                queryParams.Add($"a={HttpUtility.UrlEncode(Alignment)}");
            }

            // Apply enlargement rule (we)
            if (WithoutEnlargement)
            {
                queryParams.Add("we");
            }

            // Apply output format
            if (!string.IsNullOrWhiteSpace(OutputFormat))
            {
                queryParams.Add($"output={HttpUtility.UrlEncode(OutputFormat)}");
            }

            // Apply quality (1 to 100)
            if (Quality.HasValue && Quality.Value >= 1 && Quality.Value <= 100)
            {
                queryParams.Add($"q={Quality.Value}");
            }

            // Apply fallback/default image
            if (!string.IsNullOrWhiteSpace(DefaultFallbackUrl))
            {
                queryParams.Add($"default={HttpUtility.UrlEncode(DefaultFallbackUrl)}");
            }

            // Apply custom cache lifetime
            if (!string.IsNullOrWhiteSpace(MaxAge))
            {
                queryParams.Add($"maxage={HttpUtility.UrlEncode(MaxAge)}");
            }

            // Apply background color if specified
            if (!string.IsNullOrWhiteSpace(BackgroundColor))
            {
                queryParams.Add($"bg={HttpUtility.UrlEncode(BackgroundColor)}");
            }

            // Support animated images
            if (ProcessAllAnimatedFrames)
            {
                queryParams.Add("n=-1");
            }

            return $"{WsrvBaseUrl}?{string.Join("&", queryParams)}";
        }
        catch
        {
            // Fail safely: return original URL if construction throws
            return originalUrl;
        }
    }

    /// <summary>
    /// Initialize method for compatibility with existing code interfaces.
    /// Since wsrv.nl is a stateless image proxy and CDN, initialization is a no-op.
    /// </summary>
    public Task InitializeCacheAsync()
    {
        return Task.CompletedTask;
    }
}