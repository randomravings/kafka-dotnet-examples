using Confluent.Kafka;

namespace CachingWebService.Logging
{
    public static class KafkaLog
    {
        public static void ConsumerLog<TKey, TValue>(
            ILogger<IConsumer<TKey, TValue>> logger,
            LogMessage logMessage
        ) =>
           logger.Log(
               (LogLevel)logMessage.LevelAs(LogLevelType.MicrosoftExtensionsLogging),
               "{name}|{facility}|{message}",
               logMessage.Name,
               logMessage.Facility,
               logMessage.Message
           )
        ;

        public static void AdminLog(
            ILogger<IAdminClient> logger,
            LogMessage logMessage
        ) =>
           logger.Log(
               (LogLevel)logMessage.LevelAs(LogLevelType.MicrosoftExtensionsLogging),
               "{name}|{facility}|{message}",
               logMessage.Name,
               logMessage.Facility,
               logMessage.Message
           )
        ;
    }
}
