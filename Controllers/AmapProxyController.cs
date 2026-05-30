using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Text.Json;

namespace ZIBOGIS.Controllers;

[ApiController]
[Route("api/amap")]
public class AmapProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailTtl = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim _rateLimit = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;

    public AmapProxyController(IHttpClientFactory httpClientFactory, IMemoryCache cache, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _configuration = configuration;
    }

    /// <summary>
    /// 代理高德 v3/bus/linename，避免前端直连导致 CORS/限流，并做缓存。
    /// </summary>
    [HttpGet("bus/linename")]
    public async Task<IActionResult> BusLineName(
        [FromQuery] string keywords,
        [FromQuery] string city = "淄博",
        [FromQuery] string extensions = "all",
        [FromQuery] int offset = 10,
        [FromQuery] int page = 1,
        [FromQuery] string output = "json")
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            return BadRequest(new { message = "keywords is required" });
        }

        var key = _configuration["Amap:SearchKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            return StatusCode(500, new { message = "Missing config: Amap:SearchKey" });
        }

        // 规范化参数，避免缓存被同义参数打散
        city = string.IsNullOrWhiteSpace(city) ? "淄博" : city.Trim();
        keywords = keywords.Trim();
        extensions = string.IsNullOrWhiteSpace(extensions) ? "all" : extensions.Trim();
        output = string.IsNullOrWhiteSpace(output) ? "json" : output.Trim();
        offset = Math.Clamp(offset, 1, 50);
        page = Math.Clamp(page, 1, 10);

        var cacheKey = $"amap:bus:linename:{city}:{keywords}:{extensions}:{offset}:{page}:{output}";
        if (_cache.TryGetValue(cacheKey, out string? cachedJson) && !string.IsNullOrWhiteSpace(cachedJson))
        {
            return Content(cachedJson, "application/json");
        }

        var client = _httpClientFactory.CreateClient("Amap");
        var path =
            $"v3/bus/linename?key={Uri.EscapeDataString(key)}" +
            $"&keywords={Uri.EscapeDataString(keywords)}" +
            $"&city={Uri.EscapeDataString(city)}" +
            $"&extensions={Uri.EscapeDataString(extensions)}" +
            $"&offset={offset}&page={page}&output={Uri.EscapeDataString(output)}";

        await _rateLimit.WaitAsync();
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            if (elapsed < TimeSpan.FromMilliseconds(250))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250) - elapsed);
            }
            _lastRequestTime = DateTime.UtcNow;
        }
        finally
        {
            _rateLimit.Release();
        }

        using var resp = await client.GetAsync(path);
        var json = await resp.Content.ReadAsStringAsync();

        // 上游异常：把状态码透传成 502（但仍返回 body 方便排查）
        if (!resp.IsSuccessStatusCode)
        {
            _cache.Set(cacheKey, json, FailTtl);
            return StatusCode((int)HttpStatusCode.BadGateway, new
            {
                message = "Amap upstream error",
                statusCode = (int)resp.StatusCode,
                body = json
            });
        }

        // 对业务层限流（10021）做显式 429，避免前端误判“无结果”
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            var infocode = root.TryGetProperty("infocode", out var ic) ? ic.GetString() : null;
            var info = root.TryGetProperty("info", out var infoEl) ? infoEl.GetString() : null;

            if (status != "1" && (infocode == "10021" || (info != null && info.Contains("CUQPS_HAS_EXCEEDED_THE_LIMIT"))))
            {
                _cache.Set(cacheKey, json, FailTtl);
                return StatusCode(429, new
                {
                    message = "Amap rate limit exceeded",
                    status,
                    infocode,
                    info
                });
            }
        }
        catch
        {
            // ignore parse errors; still cache + return raw
        }

        _cache.Set(cacheKey, json, CacheTtl);
        return Content(json, "application/json");
    }
}
