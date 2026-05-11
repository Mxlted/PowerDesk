using System;

namespace PowerDesk.Core.Logging;

public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
    string LogFilePath { get; }
}
