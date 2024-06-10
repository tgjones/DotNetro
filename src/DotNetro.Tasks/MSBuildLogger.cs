using DotNetro.Compiler;
using Microsoft.Build.Utilities;

namespace DotNetro.Tasks;

public sealed class MSBuildLogger(TaskLoggingHelper logger) : ILogger
{
    public void WriteLine(IFormattable message)
    {
        logger.LogMessage(message.ToString());
    }
}