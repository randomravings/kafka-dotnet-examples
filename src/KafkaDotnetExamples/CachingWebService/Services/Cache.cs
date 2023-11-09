using System.Collections.Concurrent;

namespace CachingWebService.Services
{
    public class Cache :
        ConcurrentDictionary<string, string>
    {
    }
}
