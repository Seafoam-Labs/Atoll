using Atoll.Api.Services.Packages;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Atoll.Api.Tests.Extensions;

public class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task Package_conflict_maps_to_409_and_logs_at_debug()
    {
        var logger = new CapturingLogger();
        var handler = new GlobalExceptionHandler(logger, new StubProblemDetailsService());
        var context = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(
            context,
            new PackageConflictException("shelly"),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        var entry = Assert.Single(logger.Entries);
        Assert.Multiple(() =>
        {
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Contains("shelly", entry.Message);
        });
    }

    [Fact]
    public async Task Unexpected_exception_maps_to_500_and_logs_at_error()
    {
        var logger = new CapturingLogger();
        var handler = new GlobalExceptionHandler(logger, new StubProblemDetailsService());
        var context = new DefaultHttpContext();

        var boom = new InvalidOperationException("boom");
        var handled = await handler.TryHandleAsync(context, boom, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        var entry = Assert.Single(logger.Entries);
        Assert.Multiple(() =>
        {
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Same(boom, entry.Exception);
        });
    }

    private sealed class CapturingLogger : ILogger<GlobalExceptionHandler>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class StubProblemDetailsService : IProblemDetailsService
    {
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(true);
    }
}