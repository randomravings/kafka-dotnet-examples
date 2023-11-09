using CachingWebService.Configuration;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;

namespace CachingWebService.Services
{
    public class CacheService :
        BackgroundService
    {
        private readonly Cache _cache;
        private readonly IAdminClient _adminClient;
        private readonly ConsumerProvider _consumerProvider;
        private readonly ILogger<CacheService> _logger;
        private readonly string _sourceTopic;
        private readonly ParallelInitMode _parallelInitMode;

        public CacheService(
            Cache cache,
            IAdminClient adminClient,
            ConsumerProvider consumerProvider,
            IOptions<CacheServiceConfig> options,
            ILogger<CacheService> logger
        )
        {
            _cache = cache;
            _adminClient = adminClient;
            _consumerProvider = consumerProvider;
            _sourceTopic = options.Value.SourceTopic;
            _parallelInitMode = options.Value.ParallelInitMode;
            _logger = logger;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.Run(async () => await ConsumerLoop(stoppingToken), CancellationToken.None)
        ;

        private async Task ConsumerLoop(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Cache Service starting");

                var metadata = _adminClient.GetMetadata(_sourceTopic, TimeSpan.FromSeconds(10));
                var topicMetadata = metadata
                    .Topics
                    .FirstOrDefault(t => string.CompareOrdinal(t.Topic, _sourceTopic) == 0)
                ;

                if (topicMetadata is null)
                {
                    _logger.LogError("Unable to find topic: {topic}", _sourceTopic);
                    return;
                }

                var topicPartitionInfos = await GetTopicPartitionInfos(
                    topicMetadata
                );

                var partitionsToFetch = topicPartitionInfos
                    .Where(t => t.LowWatermark < t.HighWatermark)
                    .ToImmutableArray();
                ;

                var highWatermarks = topicPartitionInfos
                    .ToImmutableSortedDictionary(
                        k => k.TopicPartition,
                        v => new Offset(v.HighWatermark.Value - 1)
                    )
                ;

                var start = DateTimeOffset.UtcNow;
                _logger.LogInformation("Init mode: {mode}", _parallelInitMode);
                var consumer = (_parallelInitMode, partitionsToFetch) switch
                {
                    (_, { Length: 0 }) => _consumerProvider.GetConsumer(),
                    (ParallelInitMode.Broker, _) =>
                        await InitFromBrokers(
                            partitionsToFetch,
                            highWatermarks,
                            cancellationToken
                        ).ConfigureAwait(false),
                    (ParallelInitMode.Partition, _) =>
                        await InitFromPartitions(
                            partitionsToFetch,
                            highWatermarks,
                            cancellationToken
                        ).ConfigureAwait(false),
                    _ =>
                        InitSingle(
                            partitionsToFetch,
                            highWatermarks,
                            cancellationToken
                        )
                };
                var elapsed = DateTimeOffset.UtcNow - start;
                _logger.LogInformation("Init duration: {duration}", elapsed);

                consumer.Assign(highWatermarks.Select(r => new TopicPartitionOffset(r.Key, r.Value)));
                RunContinously(
                    consumer,
                    cancellationToken
                );

                _logger.LogInformation("Cache Service stopped");
            }
            catch (OperationCanceledException)
            {
                // NOOP
            }
            catch (Exception ex)
            {
                _logger.LogError("{ex}", ex.ToString());
            }
        }

        private async ValueTask<ImmutableArray<TopicPartitionInfo>> GetTopicPartitionInfos(
            TopicMetadata topicMetadata
        )
        {
            var topicPartitionInfos = topicMetadata
                .Partitions
                .Select(p => new TopicPartitionInfo(
                    p.PartitionId,
                    new TopicPartition(topicMetadata.Topic, p.PartitionId),
                    Offset.Unset,
                    Offset.Unset
                ))
                .OrderBy(t => t.TopicPartition)
                .ToImmutableArray()
            ;

            var highWatermarks = await _adminClient.ListOffsetsAsync(
                topicPartitionInfos.Select(t => new TopicPartitionOffsetSpec
                {
                    TopicPartition = t.TopicPartition,
                    OffsetSpec = OffsetSpec.Latest()
                }),
                new ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }
            );

            var lowWatermarks = await _adminClient.ListOffsetsAsync(
                topicPartitionInfos.Select(t => new TopicPartitionOffsetSpec
                {
                    TopicPartition = t.TopicPartition,
                    OffsetSpec = OffsetSpec.Earliest()
                }),
                new ListOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }
            );

            return topicPartitionInfos
                .Zip(highWatermarks.ResultInfos.OrderBy(r => r.TopicPartitionOffsetError.TopicPartition))
                .Select(t => t.First with { HighWatermark = t.Second.TopicPartitionOffsetError.Offset })
                .Zip(lowWatermarks.ResultInfos.OrderBy(r => r.TopicPartitionOffsetError.TopicPartition))
                .Select(t => t.First with { LowWatermark = t.Second.TopicPartitionOffsetError.Offset })
                .ToImmutableArray()
            ;
        }

        private IConsumer<string, string> InitSingle(
            IReadOnlyList<TopicPartitionInfo> topicPartitions,
            IReadOnlyDictionary<TopicPartition, Offset> highWatermarks,
            CancellationToken cancellationToken
        )
        {
            var topicPartitionOffsets = topicPartitions
                .Where(t => t.LowWatermark < t.HighWatermark)
                .Select(t => new TopicPartitionOffset(
                    t.TopicPartition,
                    t.LowWatermark
                ))
            ;
            var consumer = _consumerProvider.GetConsumer();
            consumer.Assign(topicPartitionOffsets);
            var count = InitializeCacheFromConsumer(
                consumer,
                highWatermarks,
                cancellationToken
            );
            _logger.LogInformation("Init count: {}", count);
            return consumer;
        }

        private async Task<IConsumer<string, string>> InitFromBrokers(
            IReadOnlyList<TopicPartitionInfo> topicPartitions,
            IReadOnlyDictionary<TopicPartition, Offset> highWatermarks,
            CancellationToken cancellationToken
        )
        {
            var topicPartitionOffsets = topicPartitions
                .Where(t => t.LowWatermark < t.HighWatermark)
                .GroupBy(p => p.LeaderId)
                .ToDictionary(
                    k => k.Key,
                    v => v.ToDictionary(
                        k => k.TopicPartition,
                        v => v.LowWatermark
                    )
                )
            ;

            var consumers = new List<IConsumer<string, string>>(topicPartitionOffsets.Count);
            var tasks = new List<Task<int>>(topicPartitionOffsets.Count);
            foreach (var (nodeId, tpos) in topicPartitionOffsets)
            {
                var c = _consumerProvider.GetConsumer();
                c.Assign(tpos.Select(kv => new TopicPartitionOffset(kv.Key, kv.Value)));
                consumers.Add(c);
                var task = Task.Run(
                    () => InitializeCacheFromConsumer(
                        c,
                        highWatermarks,
                        cancellationToken
                    ),
                    CancellationToken.None
                );
                tasks.Add(task);
            }
            await Task.WhenAll(tasks);
            var count = tasks.Sum(t => t.Result);
            _logger.LogInformation("Init count: {}", count);
            var consumer = consumers[0];
            for (int i = 1; i < consumers.Count; i++)
            {
                consumers[i].Close();
                consumers[i].Dispose();
            }
            consumers.Clear();
            tasks.Clear();
            return consumer;
        }

        private async Task<IConsumer<string, string>> InitFromPartitions(
            IReadOnlyList<TopicPartitionInfo> topicPartitions,
            IReadOnlyDictionary<TopicPartition, Offset> highWatermarks,
            CancellationToken cancellationToken
        )
        {
            var topicPartitionOffsets = topicPartitions
                .Where(t => t.LowWatermark < t.HighWatermark)
                .ToDictionary(
                    k => k.TopicPartition,
                    v => v.LowWatermark
                )
            ;

            var consumers = new List<IConsumer<string, string>>(topicPartitionOffsets.Count);
            var tasks = new List<Task<int>>(topicPartitionOffsets.Count);
            foreach (var (topicPartition, offset) in topicPartitionOffsets)
            {
                var c = _consumerProvider.GetConsumer();
                c.Assign(new TopicPartitionOffset(topicPartition, offset));
                consumers.Add(c);
                var task = Task.Run(
                    () => InitializeCacheFromConsumer(
                        c,
                        highWatermarks,
                        cancellationToken
                    ),
                    CancellationToken.None
                );
                tasks.Add(task);
            }
            await Task.WhenAll(tasks);
            var count = tasks.Sum(t => t.Result);
            _logger.LogInformation("Init count: {}", count);
            var consumer = consumers[0];
            for (int i = 1; i < consumers.Count; i++)
            {
                consumers[i].Close();
                consumers[i].Dispose();
            }
            consumers.Clear();
            tasks.Clear();
            return consumer;
        }

        private int InitializeCacheFromConsumer(
            IConsumer<string, string> consumer,
            IReadOnlyDictionary<TopicPartition, Offset> highWatermarks,
            CancellationToken cancellationToken
        )
        {
            var count = 0;
            while (consumer.Assignment.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                var consumeResult = consumer.Consume(cancellationToken);
                var topicPartition = consumeResult.TopicPartition;
                var offset = consumeResult.Offset;
                var watermark = highWatermarks[topicPartition];
                if (offset <= watermark)
                {
                    var message = consumeResult.Message;
                    _cache[message.Key] = message.Value;
                    count++;
                    if (offset == watermark)
                        consumer.IncrementalUnassign(ImmutableArray.Create(topicPartition));
                }
            }
            return count;
        }

        private void RunContinously(
            IConsumer<string, string> consumer,
            CancellationToken cancellationToken
        )
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var consumeResult = consumer.Consume(cancellationToken);
                if (consumeResult == null)
                    continue;
                var message = consumeResult.Message;
                _cache[message.Key] = message.Value;
            }
        }
    }
}
