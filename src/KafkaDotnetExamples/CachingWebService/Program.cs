using CachingWebService.Configuration;
using CachingWebService.Logging;
using CachingWebService.Services;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var parallel = Environment.GetEnvironmentVariable("PROJECT_PARALLEL") ?? "0";

// Add services to the container.

builder.Services.Configure<AdminClientConfig>(config =>
{
    var section = builder.Configuration.GetSection("KafkaAdmin");
    foreach(var s in section.GetChildren())
        config.Set(s.Key, s.Value);
});
builder.Services.Configure<ConsumerConfig>(config =>
{
    var section = builder.Configuration.GetSection("KafkaConsumer");
    foreach (var s in section.GetChildren())
        config.Set(s.Key, s.Value);
});
builder.Services.Configure<CacheServiceConfig>(builder.Configuration.GetSection(CacheServiceConfig.SECTION));

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<IAdminClient>>();
    var config = sp.GetRequiredService<IOptions<AdminClientConfig>>();
    var builder = new AdminClientBuilder(config.Value);
    return builder
        .SetLogHandler((c, l) => KafkaLog.AdminLog(logger, l))
        .Build()
    ;
});

builder.Services.AddTransient(sp =>
{
    var logger = sp.GetRequiredService<ILogger<IConsumer<string, string>>>();
    var config = sp.GetRequiredService<IOptions<ConsumerConfig>>();
    var builder = new ConsumerBuilder<string, string>(config.Value);
    return builder
        .SetKeyDeserializer(Deserializers.Utf8)
        .SetValueDeserializer(Deserializers.Utf8)
        .SetLogHandler((c, l) => KafkaLog.ConsumerLog(logger, l))
        .Build()
    ;
});

builder.Services.AddSingleton<ConsumerProvider>();
builder.Services.AddSingleton<Cache>();
builder.Services.AddSingleton<CacheService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHostedService<CacheService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
