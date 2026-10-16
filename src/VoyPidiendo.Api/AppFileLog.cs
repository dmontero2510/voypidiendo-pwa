using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VoyPidiendo.Api;

public static class AppFileLog
{
    private static readonly object Gate = new();
    private static string LogFolder => Environment.GetEnvironmentVariable("VOYPIDIENDO_LOG_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "Logs");

    public static void Write(string level, string message, Exception? exception = null)
    {
        try
        {
            var folder = LogFolder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"VoyPidiendo-{DateTime.Now:yyyy-MM-dd}.log");
            var text = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ").AppendLine(message);
            if (exception is not null) text.AppendLine(exception.ToString());
            lock (Gate) File.AppendAllText(path, text.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    public static SqlLogScope BeginSql(string operation, DbCommand command)
    {
        var parameters = string.Join(", ", command.Parameters.Cast<DbParameter>().Select(p => $"{p.ParameterName}={FormatValue(p.Value)}"));
        Write("SQL", $"INICIO {operation}\nParámetros: {parameters}\nSQL:\n{command.CommandText.Trim()}");
        return new SqlLogScope(operation);
    }

    private static string FormatValue(object? value)
    {
        if (value is null or DBNull) return "NULL";
        var text = Convert.ToString(value) ?? string.Empty;
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    public sealed class SqlLogScope(string operation) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _completed;
        public void Complete() => _completed = true;
        public void Dispose() => Write("SQL", $"FIN {operation} - {(_completed ? "OK" : "INTERRUMPIDA")} - {_watch.ElapsedMilliseconds} ms");
    }
}
