// See https://aka.ms/new-console-template for more information
using DataGenerator;

Console.Write("Producing ... ");




await DataProducer.Produce(
    "state-topic",
    10000,
    1000000,
    1000,
    CancellationToken.None
);
Console.WriteLine("Done!");