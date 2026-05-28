using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ARI.Discord;

public static class Common
{
    public static ILogger Logger { get; private set; } = NullLogger.Instance;

    public static void InitialiseLogger(ILoggerFactory factory)
    {
        Logger = factory.CreateLogger("ARI.Discord");
    }
}