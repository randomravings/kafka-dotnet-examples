namespace CachingWebService.Configuration
{
    public sealed class CacheServiceConfig
    {
        public const string SECTION = "CacheService";
        public string SourceTopic { get; set; } = "";
        public ParallelInitMode ParallelInitMode { get; set; }
    }
}
