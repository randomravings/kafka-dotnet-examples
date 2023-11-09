using Confluent.Kafka;

namespace DataGenerator
{
    public static class DataProducer
    {
        public static async Task Produce(
            string topic,
            int uniqueKeys,
            int totalRecords,
            int maxDop,
            CancellationToken cancellationToken
        )
        {
            var tasks = new List<Task>(maxDop);
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = "localhost:9092",
                LingerMs = 100,
                BatchSize = 1024 * 1024,
                EnableIdempotence = false,
            };
            var producer = new ProducerBuilder<string, string>(producerConfig)
                .SetKeySerializer(Serializers.Utf8)
                .SetValueSerializer(Serializers.Utf8)
                .Build()
            ;
            for (int i = 0; i < totalRecords; i++)
            {
                var keyMod = i % uniqueKeys;
                var key = $"key_{keyMod:D8}";
                var value = $"value_{i:D8}";
                var task = producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value }, cancellationToken);
                tasks.Add(task);
                if (tasks.Count >= maxDop)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }
        }
    }
}
