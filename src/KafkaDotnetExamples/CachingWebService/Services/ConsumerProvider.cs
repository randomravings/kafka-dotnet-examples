using Confluent.Kafka;

namespace CachingWebService.Services
{
    public class ConsumerProvider
    {
        private readonly IServiceProvider _serviceProvider;

        public ConsumerProvider(
            IServiceProvider serviceProvider
        ) =>
            _serviceProvider = serviceProvider
        ;

        public IConsumer<string, string> GetConsumer() =>
            _serviceProvider.GetRequiredService<IConsumer<string, string>>()
        ;
    }
}
