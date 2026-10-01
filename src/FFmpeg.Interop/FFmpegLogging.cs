using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.Interop.Native;
using Microsoft.Extensions.Logging;
using static FFmpeg.Interop.Native.LibAVUtil;

namespace FFmpeg.Interop;

/// <summary>FFmpeg's log levels (<c>AV_LOG_*</c>).</summary>
public enum FFmpegLogLevel
{
    /// <summary>Log nothing.</summary>
    Quiet = AV_LOG_QUIET,

    /// <summary>The process is about to abort.</summary>
    Panic = AV_LOG_PANIC,

    /// <summary>An unrecoverable error.</summary>
    Fatal = AV_LOG_FATAL,

    /// <summary>An error; the operation failed.</summary>
    Error = AV_LOG_ERROR,

    /// <summary>Something looks wrong and may cause problems.</summary>
    Warning = AV_LOG_WARNING,

    /// <summary>Standard information (FFmpeg's default level).</summary>
    Info = AV_LOG_INFO,

    /// <summary>Detailed information.</summary>
    Verbose = AV_LOG_VERBOSE,

    /// <summary>Information useful to FFmpeg developers.</summary>
    Debug = AV_LOG_DEBUG,

    /// <summary>Extremely verbose debugging.</summary>
    Trace = AV_LOG_TRACE,
}

/// <summary>
/// Routes FFmpeg's log (<c>av_log</c>) to <see cref="ILogger"/>. Messages are logged under a category
/// per FFmpeg component class (<c>FFmpeg.AVCodecContext</c>, <c>FFmpeg.AVFormatContext</c>, ...), with
/// the instance's name (the codec, the container) as the <c>Component</c> property.
/// </summary>
/// <remarks>
/// FFmpeg's log is process-wide, so this is too. <see cref="Level"/> is FFmpeg's own threshold, applied
/// before a message is formatted; the loggers' filters apply after.
/// </remarks>
public static unsafe class FFmpegLogging
{
    private const int LineCapacity = 1024;

    private static readonly Lock s_gate = new();
    private static readonly ConcurrentDictionary<string, ILogger> s_loggers = new(
        StringComparer.Ordinal
    );
    private static ILoggerFactory? s_factory;

    // FFmpeg writes one line in several calls when it builds it piece by piece (stream dumps, progress);
    // the pieces are joined per thread until the newline.
    [ThreadStatic]
    private static StringBuilder? t_pending;

    [ThreadStatic]
    private static int t_demotions;

    /// <summary>FFmpeg's log level: messages above it are dropped before they are formatted.</summary>
    public static FFmpegLogLevel Level
    {
        get => (FFmpegLogLevel)av_log_get_level();
        set => av_log_set_level((int)value);
    }

    /// <summary>
    /// Logs FFmpeg's messages from this thread at <see cref="LogLevel.Debug"/> at most, until the
    /// returned scope is disposed on this thread: for a probe whose failures are expected and reported by
    /// the caller, such as opening an encoder to learn whether the hardware has it.
    /// </summary>
    /// <remarks>
    /// Opening a codec logs on the calling thread; what FFmpeg logs from threads of its own, such as frame
    /// threads decoding, is not demoted.
    /// </remarks>
    /// <returns>The scope; scopes nest.</returns>
    public static DemotionScope Demote()
    {
        t_demotions++;
        return new DemotionScope(Environment.CurrentManagedThreadId);
    }

    /// <summary>
    /// Sends FFmpeg's log to loggers from <paramref name="factory"/>, or back to FFmpeg's default (stderr)
    /// when null.
    /// </summary>
    /// <param name="factory">The logger factory, or null.</param>
    public static void UseLoggerFactory(ILoggerFactory? factory)
    {
        lock (s_gate)
        {
            s_factory = factory;
            s_loggers.Clear();
            av_log_set_callback(
                factory is null
                    ? (delegate* unmanaged[Cdecl]<void*, int, sbyte*, void*, void>)
                        FFmpegLibraries.GetExport("avutil", "av_log_default_callback")
                    : &Log
            );
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Log(void* context, int level, sbyte* format, void* arguments)
    {
        try
        {
            ILoggerFactory? factory = s_factory;
            if (factory is null || level > av_log_get_level())
            {
                return;
            }

            (string category, string? component) = Describe(context);
            ILogger logger = s_loggers.GetOrAdd(
                category,
                static (name, f) => f.CreateLogger(name),
                factory
            );
            LogLevel logLevel = ToLogLevel(level);
            if (t_demotions > 0 && logLevel > LogLevel.Debug)
            {
                logLevel = LogLevel.Debug;
            }

            if (!logger.IsEnabled(logLevel))
            {
                return;
            }

            // No FFmpeg prefix ("[h264 @ 0x...]"): the component is a property of its own.
            byte* line = stackalloc byte[LineCapacity];
            int printPrefix = 0;
            int length = av_log_format_line2(
                context,
                level,
                format,
                arguments,
                (sbyte*)line,
                LineCapacity,
                &printPrefix
            );
            if (length <= 0)
            {
                return;
            }

            ReadOnlySpan<byte> text = new(line, Math.Min(length, LineCapacity - 1));
            if (text[^1] != (byte)'\n')
            {
                (t_pending ??= new()).Append(Encoding.UTF8.GetString(text));
                return;
            }

            string message = Encoding.UTF8.GetString(text[..^1]);
            if (t_pending is { Length: > 0 } pending)
            {
                message = pending.Append(message).ToString();
                pending.Clear();
            }

            logger.Log(
                logLevel,
                new EventId(level),
                new LogState(component, message),
                null,
                static (state, _) => state.ToString()
            );
        }
        catch (Exception)
        {
            // Deliberately not logged or rethrown: this runs inside FFmpeg's C frames, which an exception
            // must not unwind, and the failure is in the logging path itself, so there is nowhere to report it.
        }
    }

    // A log context is a struct whose first member is its AVClass: the class names the component type,
    // item_name the instance.
    private static (string Category, string? Component) Describe(void* context)
    {
        AVClass* avClass = context is null ? null : *(AVClass**)context;
        if (avClass is null)
        {
            return ("FFmpeg", null);
        }

        string? className = NativeString.Read(avClass->class_name);
        string? itemName = avClass->item_name is null
            ? null
            : NativeString.Read(avClass->item_name(context));
        return (className is null ? "FFmpeg" : $"FFmpeg.{className}", itemName);
    }

    internal static LogLevel ToLogLevel(int level) =>
        level switch
        {
            <= AV_LOG_FATAL => LogLevel.Critical,
            <= AV_LOG_ERROR => LogLevel.Error,
            <= AV_LOG_WARNING => LogLevel.Warning,
            <= AV_LOG_INFO => LogLevel.Information,
            <= AV_LOG_VERBOSE => LogLevel.Debug,
            _ => LogLevel.Trace,
        };

    /// <summary>The scope of <see cref="Demote"/>.</summary>
    public readonly struct DemotionScope : IDisposable
    {
        private readonly int _thread;

        internal DemotionScope(int thread) => _thread = thread;

        /// <summary>Ends the scope.</summary>
        /// <exception cref="InvalidOperationException">On a thread other than the one that began it.</exception>
        public void Dispose()
        {
            if (_thread == 0)
            {
                return;
            }

            if (_thread != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException(
                    "A demotion scope ends on the thread that began it."
                );
            }

            t_demotions--;
        }
    }

    // The structured state: {Component} and {Message}, with the template ILogger providers expect.
    internal sealed class LogState(string? component, string message)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public string Message { get; } = message;

        public int Count => 3;

        public KeyValuePair<string, object?> this[int index] =>
            index switch
            {
                0 => new("Component", component),
                1 => new("Message", Message),
                2 => new("{OriginalFormat}", "{Component}: {Message}"),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() =>
            component is null ? Message : $"{component}: {Message}";
    }
}
