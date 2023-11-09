using Confluent.Kafka;

namespace CachingWebService
{
    public sealed record TopicPartitionInfo(
        int LeaderId,
        TopicPartition TopicPartition,
        Offset HighWatermark,
        Offset LowWatermark
    );
}
