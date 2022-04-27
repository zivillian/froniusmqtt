using Microsoft.Extensions.Logging;

public class ConsoleLogger:ILogger
{
    private readonly bool _debug;

    public ConsoleLogger(bool debug)
    {
        _debug = debug;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Console.WriteLine($"{formatter(state, exception)}");
        }
    }

    public bool IsEnabled(LogLevel logLevel) => _debug || logLevel > LogLevel.Information;

    public IDisposable BeginScope<TState>(TState state) => default!;
}