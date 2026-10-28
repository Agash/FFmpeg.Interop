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

    // Loggers by the class_name pointer of a context's AVClass, a static string in FFmpeg, so a message
    // below the logger's level costs no string; 0 for messages without a context.
    private static readonly ConcurrentDictionary<nint, ILogger> s_loggers = new();
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
        Install();
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
            Install();
        }
    }

    // FFmpeg's own callback, which writes to stderr: where messages go when no factory is set.
    private static nint s_default;

    private static bool s_installed;

    // Takes FFmpeg's log once, for good: with no factory it still reaches stderr through FFmpeg's own
    // callback, demoted where a scope asks.
    private static void Install()
    {
        lock (s_gate)
        {
            if (!s_installed)
            {
                s_default = FFmpegLibraries.GetExport("avutil", "av_log_default_callback");
                av_log_set_callback(&Log);
                s_installed = true;

                // FFmpeg's and its libraries' threads outlive the runtime when the process exits (a codec
                // thread, a driver's teardown at exit), and a message then would call into managed code that
                // is gone: FFmpeg gets its own callback back first. Likewise when this assembly's load
                // context unloads.
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => Uninstall();
                System
                    .Runtime.Loader.AssemblyLoadContext.GetLoadContext(
                        typeof(FFmpegLogging).Assembly
                    )!
                    .Unloading += static _ => Uninstall();
            }
        }
    }

    private static void Uninstall()
    {
        lock (s_gate)
        {
            if (s_installed)
            {
                av_log_set_callback(
                    (delegate* unmanaged[Cdecl]<void*, int, sbyte*, void*, void>)s_default
                );
                s_installed = false;
            }
        }
    }

    /// <summary>
    /// Sends FFmpeg's log to loggers from <paramref name="factory"/> until the returned route is disposed,
    /// which sends it back to stderr unless another route or <see cref="UseLoggerFactory"/> has taken
    /// over since: for a container that owns the factory and may be disposed before the process ends.
    /// </summary>
    /// <param name="factory">The logger factory.</param>
    /// <returns>The route.</returns>
    public static IDisposable RouteTo(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        UseLoggerFactory(factory);
        return new Route(factory);
    }

    private sealed class Route(ILoggerFactory factory) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (s_gate)
            {
                if (ReferenceEquals(s_factory, factory))
                {
                    UseLoggerFactory(null);
                }
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Log(void* context, int level, sbyte* format, void* arguments)
    {
        try
        {
            ILoggerFactory? factory = s_factory;
            if (factory is null)
            {
                // Demoted below FFmpeg's default threshold, a probe's expected failure is not printed.
                int forwarded = t_demotions > 0 && level < AV_LOG_DEBUG ? AV_LOG_DEBUG : level;
                ((delegate* unmanaged[Cdecl]<void*, int, sbyte*, void*, void>)s_default)(
                    context,
                    forwarded,
                    format,
                    arguments
                );
                return;
            }

            if (level > av_log_get_level())
            {
                return;
            }

            AVClass* avClass = context is null ? null : *(AVClass**)context;
            ILogger logger = s_loggers.GetOrAdd(
                avClass is null ? 0 : (nint)avClass->class_name,
                static (_, state) => state.Factory.CreateLogger(Category(state.Class)),
                (Factory: factory, Class: (nint)avClass)
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
            string? component =
                avClass is null || avClass->item_name is null
                    ? null
                    : NativeString.Read(avClass->item_name(context));
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
    // The logger category of a context's class: "FFmpeg.<class name>".
    private static string Category(nint avClass)
    {
        string? className =
            avClass == 0 ? null : NativeString.Read(((AVClass*)avClass)->class_name);
        return className is null ? "FFmpeg" : $"FFmpeg.{className}";
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
