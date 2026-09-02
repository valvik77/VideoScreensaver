using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoScreensaver;

public sealed class PixabaySearchResult
{
    public int Total { get; set; }
    public int TotalHits { get; set; }
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public List<VideoGalleryItem> Items { get; set; } = [];
}

public static class PixabayCategories
{
    public static readonly IReadOnlyList<PixabayCategory> List =
    [
        new("Todas las categorías", ""),
        new("Fondos / Backgrounds", "backgrounds"),
        new("Naturaleza", "nature"),
        new("Ciencia y Tecnología", "science"),
        new("Lugares y Paisajes", "places"),
        new("Animales", "animals"),
        new("Viajes", "travel"),
        new("Edificios y Arquitectura", "buildings"),
        new("Música", "music"),
        new("Educación", "education"),
        new("Gente", "people"),
        new("Deportes", "sports"),
        new("Comida", "food"),
        new("Transporte", "transportation"),
        new("Negocios", "business"),
        new("Informática", "computer")
    ];
}

public sealed class PixabayRateLimitException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public PixabayRateLimitException(TimeSpan? retryAfter)
        : base(retryAfter.HasValue
            ? $"Pixabay ha limitado las peticiones. Inténtalo de nuevo en {(int)Math.Ceiling(retryAfter.Value.TotalSeconds)} segundos."
            : "Pixabay ha limitado las peticiones temporalmente. Espera unos segundos antes de volver a buscar.")
    {
        RetryAfter = retryAfter;
    }
}

public sealed class PixabayApiKeyMissingException : Exception
{
    public PixabayApiKeyMissingException()
        : base("Configura tu propia API Key de Pixabay en Ajustes para poder buscar vídeos.")
    {
    }
}

public sealed class PixabayService
{
    private static readonly string SearchCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoScreensaver",
        "pixabay-search-cache");
    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromHours(24);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
    };

    // Client-side throttling to avoid hammering the Pixabay API (their free tier allows ~100 req/min,
    // but bursts of rapid category/search changes can still trigger 429 Too Many Requests).
    private static readonly SemaphoreSlim ThrottleGate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static DateTime _rateLimitedUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(1200);

    public async Task<PixabaySearchResult> SearchVideosAsync(
        string query,
        string category,
        int page = 1,
        int perPage = 20,
        string? customApiKey = null,
        CancellationToken cancellationToken = default)
    {
        // No bundled fallback key: shipping one in a public repo would let anyone consume the
        // developer's quota or get it rate-limited/banned by Pixabay. Each user must supply theirs.
        if (string.IsNullOrWhiteSpace(customApiKey))
        {
            throw new PixabayApiKeyMissingException();
        }

        var apiKey = customApiKey.Trim();
        var cacheKey = CreateSearchCacheKey(query, category, page, perPage);
        if (TryReadCachedSearch(cacheKey, out var cachedResult))
        {
            return cachedResult;
        }

        var queryParams = new List<string>
        {
            $"key={Uri.EscapeDataString(apiKey)}",
            $"page={page}",
            $"per_page={perPage}",
            "safesearch=true"
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            queryParams.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            queryParams.Add($"category={Uri.EscapeDataString(category.Trim())}");
        }

        var url = $"https://pixabay.com/api/videos/?{string.Join("&", queryParams)}";

        HttpResponseMessage response;
        await ThrottleGate.WaitAsync(cancellationToken);
        try
        {
            // Another queued request may have populated this entry while we were waiting.
            if (TryReadCachedSearch(cacheKey, out cachedResult))
            {
                return cachedResult;
            }

            var rateLimitDelay = _rateLimitedUntilUtc - DateTime.UtcNow;
            if (rateLimitDelay > TimeSpan.Zero)
            {
                await Task.Delay(rateLimitDelay, cancellationToken);
            }

            var elapsedSinceLast = DateTime.UtcNow - _lastRequestUtc;
            if (elapsedSinceLast < MinRequestInterval)
            {
                await Task.Delay(MinRequestInterval - elapsedSinceLast, cancellationToken);
            }

            response = await HttpClient.GetAsync(url, cancellationToken);
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            ThrottleGate.Release();
        }

        string json;
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var retryAfter = GetRetryAfter(response);
                _rateLimitedUntilUtc = DateTime.UtcNow + (retryAfter ?? TimeSpan.FromSeconds(60));
                throw new PixabayRateLimitException(retryAfter);
            }

            response.EnsureSuccessStatusCode();

            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        var apiData = JsonSerializer.Deserialize<PixabayApiResponse>(json, JsonOptions);

        if (apiData is null || apiData.Hits is null)
        {
            var emptyResult = new PixabaySearchResult
            {
                Total = 0,
                TotalHits = 0,
                CurrentPage = page,
                TotalPages = 1,
                Items = []
            };
            WriteCachedSearch(cacheKey, emptyResult);
            return emptyResult;
        }

        // Pixabay's video endpoint has no orientation query parameter (unlike its image
        // endpoint), so enforce a desktop-friendly landscape orientation from the dimensions
        // returned for the available video variants.
        var items = apiData.Hits
        .Where(hit => IsLandscapeVideo(hit.Videos))
        .Select(hit =>
        {
            // Prefer the desktop-quality variant. The player waits for actual incoming-frame
            // progress before fading, avoiding the need to reduce all clips to the softer tiny.
            var bestVideo = hit.Videos?.Small?.Url ??
                            hit.Videos?.Medium?.Url ??
                            hit.Videos?.Large?.Url ??
                            hit.Videos?.Tiny?.Url ?? string.Empty;

            var previewVideo = hit.Videos?.Tiny?.Url ??
                               hit.Videos?.Small?.Url ??
                               hit.Videos?.Medium?.Url ??
                               bestVideo;

            var thumbnail = !string.IsNullOrWhiteSpace(hit.Videos?.Large?.Thumbnail) ? hit.Videos.Large.Thumbnail :
                            !string.IsNullOrWhiteSpace(hit.Videos?.Medium?.Thumbnail) ? hit.Videos.Medium.Thumbnail :
                            !string.IsNullOrWhiteSpace(hit.Videos?.Small?.Thumbnail) ? hit.Videos.Small.Thumbnail :
                            !string.IsNullOrWhiteSpace(hit.Videos?.Tiny?.Thumbnail) ? hit.Videos.Tiny.Thumbnail :
                            !string.IsNullOrWhiteSpace(hit.PictureId) ? $"https://i.vimeocdn.com/video/{hit.PictureId}_640x360.jpg" : string.Empty;

            var tags = string.IsNullOrWhiteSpace(hit.Tags) ? "Vídeo Pixabay" : hit.Tags.Trim();
            var title = tags.Length > 0 ? (char.ToUpperInvariant(tags[0]) + (tags.Length > 1 ? tags[1..] : "")) : "Vídeo Pixabay";

            return new VideoGalleryItem
            {
                Id = $"pixabay_{hit.Id}",
                Title = title,
                SourceType = VideoSourceType.Pixabay,
                VideoUri = bestVideo,
                PreviewUri = previewVideo,
                ThumbnailUri = thumbnail,
                Duration = hit.Duration,
                Tags = tags,
                Subtitle = $"Pixabay • {hit.Duration}s • por {hit.User ?? "anónimo"}"
            };
        }).Where(item => !string.IsNullOrEmpty(item.VideoUri)).ToList();

        var totalHits = apiData.TotalHits;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalHits / (double)perPage));

        var searchResult = new PixabaySearchResult
        {
            Total = apiData.Total,
            TotalHits = totalHits,
            CurrentPage = page,
            TotalPages = totalPages,
            Items = items
        };
        WriteCachedSearch(cacheKey, searchResult);
        return searchResult;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
            int.TryParse(resetValues.FirstOrDefault(), out var resetSeconds) &&
            resetSeconds > 0)
        {
            return TimeSpan.FromSeconds(resetSeconds);
        }

        return null;
    }

    private static string CreateSearchCacheKey(string query, string category, int page, int perPage)
    {
        // The API key is intentionally excluded: it is secret and does not alter public results.
        // v4 restores desktop quality after replacing seek-based priming with frame synchronization.
        var normalized = $"landscape-v4|{query.Trim().ToLowerInvariant()}|{category.Trim().ToLowerInvariant()}|{page}|{perPage}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }

    private static bool TryReadCachedSearch(string cacheKey, out PixabaySearchResult result)
    {
        result = null!;
        try
        {
            var cacheFile = Path.Combine(SearchCacheDirectory, $"{cacheKey}.json");
            if (!File.Exists(cacheFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) > SearchCacheLifetime)
            {
                return false;
            }

            var cached = JsonSerializer.Deserialize<PixabaySearchResult>(File.ReadAllText(cacheFile), JsonOptions);
            if (cached is null)
            {
                return false;
            }

            result = cached;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCachedSearch(string cacheKey, PixabaySearchResult result)
    {
        try
        {
            Directory.CreateDirectory(SearchCacheDirectory);
            var cacheFile = Path.Combine(SearchCacheDirectory, $"{cacheKey}.json");
            var temporaryFile = $"{cacheFile}.tmp";
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(result, JsonOptions));
            File.Move(temporaryFile, cacheFile, overwrite: true);
        }
        catch
        {
            // Search caching is an optimization; a filesystem failure must not break the gallery.
        }
    }

    private static bool IsLandscapeVideo(PixabayVideoVariants? videos)
    {
        if (videos is null)
        {
            return false;
        }

        var variants = new[] { videos.Large, videos.Medium, videos.Small, videos.Tiny };
        var measurableVariant = variants.FirstOrDefault(video =>
            video is not null &&
            !string.IsNullOrWhiteSpace(video.Url) &&
            video.Width > 0 &&
            video.Height > 0);

        return measurableVariant is not null && measurableVariant.Width > measurableVariant.Height;
    }

    private sealed class PixabayApiResponse
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("totalHits")]
        public int TotalHits { get; set; }

        [JsonPropertyName("hits")]
        public List<PixabayHit>? Hits { get; set; }
    }

    private sealed class PixabayHit
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("pageURL")]
        public string? PageUrl { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("tags")]
        public string? Tags { get; set; }

        [JsonPropertyName("duration")]
        public int Duration { get; set; }

        [JsonPropertyName("picture_id")]
        public string? PictureId { get; set; }

        [JsonPropertyName("videos")]
        public PixabayVideoVariants? Videos { get; set; }

        [JsonPropertyName("user")]
        public string? User { get; set; }
    }

    private sealed class PixabayVideoVariants
    {
        [JsonPropertyName("large")]
        public PixabayVideoDetail? Large { get; set; }

        [JsonPropertyName("medium")]
        public PixabayVideoDetail? Medium { get; set; }

        [JsonPropertyName("small")]
        public PixabayVideoDetail? Small { get; set; }

        [JsonPropertyName("tiny")]
        public PixabayVideoDetail? Tiny { get; set; }
    }

    private sealed class PixabayVideoDetail
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("thumbnail")]
        public string? Thumbnail { get; set; }
    }
}
