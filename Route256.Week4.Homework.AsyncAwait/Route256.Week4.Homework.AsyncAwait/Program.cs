
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Route256.PriceCalculator.Domain;
using Route256.PriceCalculator.Domain.Models.PriceCalculator;
using Route256.PriceCalculator.Domain.Separated;
using Route256.PriceCalculator.Domain.Services;
using Route256.Week4.Homework.AsyncAwait;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

var builder = new ConfigurationBuilder()
    .AddJsonFile($"appsettings.json", true, true);

IConfiguration config = builder.Build();

var asyncAwaitOptions = config.GetSection("AsyncAwaitOptions").Get<AsyncAwaitOptions>();

var channelReadCalc = Channel.CreateUnbounded<string>();
var channelCalcWrite = Channel.CreateUnbounded<CalcResult>();

var readFromFileTask = Task.Run(async () =>
{
    var countReadedFromFile = 0;
    var lineNum = 0;

    var path = Path.Combine(@"IOFiles\SourceFile.txt");
    using StreamReader reader = File.OpenText(path);

    Console.WriteLine($"Start reading");

    String? lineStr = reader.ReadLine();

    while (lineStr != null)
    {
        lineNum++;

        if (lineNum != 1)
        {
            await channelReadCalc.Writer.WriteAsync(lineStr);

            countReadedFromFile++;
        }

        lineStr = reader.ReadLine();
    }

    channelReadCalc.Writer.Complete();

    Console.WriteLine($"Readed lines from file: {countReadedFromFile}");
});

var calculatePriceTask = Task.Run(async () =>
{
    var countCaltulated = 0;

    var parallelOptions = new DynamicParallelOptions
    {
        MaxDegreeOfParallelism = asyncAwaitOptions.TaskCount
    };

    Console.WriteLine($"Start calculating");

    await DynamicParallelForEachAsync(channelReadCalc.Reader.ReadAllAsync(), parallelOptions, async (lineStr, token) =>
    {
        parallelOptions.MaxDegreeOfParallelism = RereadAsyncAwaitOptions().TaskCount;

        string[] values = Regex.Split(lineStr, ", ");

        bool isParsableId       = Int32.TryParse(values[0], out var id);
        bool isParsableWidth    = Int32.TryParse(values[1], out var width);
        bool isParsableLength   = Int32.TryParse(values[2], out var length);
        bool isParsableHeight   = Int32.TryParse(values[3], out var height);
        bool isParsableWeight   = Int32.TryParse(values[4], out var weight);

        if (   isParsableId
            && isParsableWidth
            && isParsableLength
            && isParsableHeight
            && isParsableWeight)
        {
            var options = new PriceCalculatorOptions
            {
                VolumeToPriceRatio = 1,
                WeightToPriceRatio = 1
            };

            var repositoryMock = new Mock<IStorageRepository>(MockBehavior.Default);

            var priceCalculatorService = new PriceCalculatorService(options, repositoryMock.Object);

            var price = priceCalculatorService.CalculatePrice(new[] { new GoodModel(
                            height,
                            length,
                            width,
                            weight) });

            await Task.Delay(2000);

            await channelCalcWrite.Writer.WriteAsync(new CalcResult(id, price));

            countCaltulated++;
        }
    });

    channelCalcWrite.Writer.Complete();
    Console.WriteLine($"Calculated lines: {countCaltulated}");
});

var writeToFileTask = Task.Run(async () =>
{
    var countWritedToFile = 0;

    var path = Path.Combine(@"IOFiles\DistinationFile.txt");
    using StreamWriter writer = new StreamWriter(path);

    Console.WriteLine($"Start writing");

    await writer.WriteLineAsync("id, delivery_price");

    await foreach (var calcResult in channelCalcWrite.Reader.ReadAllAsync())
    {
        await writer.WriteLineAsync($"{calcResult.Id}, {calcResult.Price}");

        countWritedToFile++;
    }

    Console.WriteLine($"Writed lines to file: {countWritedToFile}");
});

await Task.WhenAll(readFromFileTask, calculatePriceTask, writeToFileTask);


AsyncAwaitOptions RereadAsyncAwaitOptions()
{
    var asyncAwaitOptionsLocal = config.GetSection("AsyncAwaitOptions").Get<AsyncAwaitOptions>();

    return asyncAwaitOptionsLocal;
}

static Task DynamicParallelForEachAsync<TSource>(
    IAsyncEnumerable<TSource> source,
    DynamicParallelOptions options,
    Func<TSource, CancellationToken, ValueTask> body)
{
    if (source == null)     throw new ArgumentNullException(nameof(source));
    if (options == null)    throw new ArgumentNullException(nameof(options));
    if (body == null)       throw new ArgumentNullException(nameof(body));

    var semaphore = new SemaphoreSlim(options.MaxDegreeOfParallelism);
    options.DegreeOfParallelismChangedDelta += Options_ChangedDelta;

    void Options_ChangedDelta(object sender, int delta)
    {
        if (delta > 0)
            semaphore.Release(delta);
        else
            for (int i = delta; i < 0; i++) semaphore.WaitAsync();
    }

    async IAsyncEnumerable<TSource> GetSemaphoreSource()
    {
        await foreach (var item in source.ConfigureAwait(false))
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            yield return item;
        }
    }

    return Parallel.ForEachAsync(GetSemaphoreSource(), options, async (item, ct) =>
    {
        try 
        { 
            await body(item, ct).ConfigureAwait(false); 
        }
        finally 
        { 
            semaphore.Release(); 
        }
    }).ContinueWith(t =>
    {
        options.DegreeOfParallelismChangedDelta -= Options_ChangedDelta;

        return t;
    }, default, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default)
        .Unwrap();
}

public class DynamicParallelOptions : ParallelOptions
{
    private int _maxDegreeOfParallelism;

    public event EventHandler<int> DegreeOfParallelismChangedDelta;
    public DynamicParallelOptions()
    {
        base.MaxDegreeOfParallelism = Int32.MaxValue;
        _maxDegreeOfParallelism = Environment.ProcessorCount;
    }

    public new int MaxDegreeOfParallelism
    {
        get { return _maxDegreeOfParallelism; }
        set
        {
            if (value < 1) value = Environment.ProcessorCount;

            if (value == _maxDegreeOfParallelism) return;

            int delta = value - _maxDegreeOfParallelism;

            DegreeOfParallelismChangedDelta?.Invoke(this, delta);

            _maxDegreeOfParallelism = value;
        }
    }
}

public record CalcResult(
    int Id,
    decimal Price);




