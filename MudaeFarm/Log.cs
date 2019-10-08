using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MudaeFarm
{
    public static class Log
    {
        static TextWriter _writer = File.CreateText("_log.txt");

        public static void Close()
        {
            lock (_logLock)
            {
                if (_writer == null)
                    return;

                _writer.Dispose();
                _writer = null;
            }
        }

        static readonly object _logLock = new object();

        public static void Debug(string message, Exception exception = null) => Write(ConsoleColor.DarkGray, "[dbug] ", message, exception);
        public static void Info(string message, Exception exception = null) => Write(ConsoleColor.Gray, "[info] ", message, exception);
        public static void Warning(string message, Exception exception = null) => Write(ConsoleColor.Yellow, "[warn] ", message, exception);
        public static void Error(string message, Exception exception = null) => Write(ConsoleColor.Red, "[erro] ", message, exception);

        static void Write(ConsoleColor color, string prefix, string message, Exception e)
        {
            prefix += $"[{DateTime.Now:hh:mm:ss}] ";

            var builder = new StringBuilder();
            var title   = null as string;

            if (message != null)
                foreach (var line in SplitLines(message.Trim()))
                    builder.AppendLine(title = prefix + line);

            if (e != null)
                foreach (var line in SplitLines(e.ToString()))
                    builder.AppendLine(title = prefix + line);

            var text = builder.ToString();

            lock (_logLock)
            {
                Console.ForegroundColor = color;

                if (_writer != null)
                {
                    _writer.Write(text);
                    _writer.Flush();
                }

                Console.Write(text);

                if (title != null)
                {
                    if (title.Length > 100)
                        title = title.Substring(0, 97) + "...";

                    Console.Title = "MudaeFarm — " + title;
                }
            }
        }

        static IEnumerable<string> SplitLines(string str) => str.Replace("\r", "").Split('\n');
    }
}