using CachingWebService.Services;
using Microsoft.AspNetCore.Mvc;

namespace CachingWebService.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class CacheController : ControllerBase
    {
        private readonly Cache _cache;
        private readonly ILogger<CacheController> _logger;

        public CacheController(
            Cache cache,
            ILogger<CacheController> logger
        )
        {
            _cache = cache;
            _logger = logger;
        }

        [HttpGet(Name = "GetData")]
        public IEnumerable<string> Get() => _cache
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}:{kv.Value}")
            .ToList()
        ;
    }
}