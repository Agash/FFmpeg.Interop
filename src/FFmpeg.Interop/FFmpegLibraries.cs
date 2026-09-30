using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop;

/// <summary>
/// Locates and loads the native FFmpeg shared libraries the bindings call, and checks that each one
/// is the ABI major the bindings were generated for.
/// </summary>
/// <remarks>
/// <para>
/// The bindings import each library by its short name (<c>avutil</c>, <c>avcodec</c>, ...). The first
/// call into any of them installs a resolver that maps the short name to the versioned file for the
/// current OS and probes, in order: <see cref="SearchDirectory"/> when set,
/// <c>runtimes/&lt;rid&gt;/native</c> beside the application, the application directory, and finally
/// the OS loader's default search.
/// </para>
/// <para>
/// A library of another major loads without complaint and then corrupts memory on the first call
/// whose structure layout changed, so every library is checked against its bound major before any
/// binding uses it.
/// </para>
/// </remarks>
public static class FFmpegLibraries
{
    private static readonly Lock s_gate = new();
    private static readonly Dictionary<string, nint> s_handles = new(StringComparer.Ordinal);
    private static string? s_searchDirectory;
    private static bool s_resolverInstalled;

    /// <summary>The native libraries the bindings import, with the ABI major each was generated for.</summary>
    public static ImmutableArray<(string Name, int Major)> Libraries { get; } =
    [
        ("avutil", LibAVUtil.LIBAVUTIL_VERSION_MAJOR),
        ("avcodec", LibAVCodec.LIBAVCODEC_VERSION_MAJOR),
        ("avformat", LibAVFormat.LIBAVFORMAT_VERSION_MAJOR),
        ("avdevice", LibAVDevice.LIBAVDEVICE_VERSION_MAJOR),
        ("swscale", LibSwScale.LIBSWSCALE_VERSION_MAJOR),
        ("swresample", LibSwResample.LIBSWRESAMPLE_VERSION_MAJOR),
    ];

    // Every FFmpeg library, in link order: each depends only on libraries before it. On Linux a library
    // loaded by path finds its dependencies only on the loader's search path or through its RUNPATH,
    // which many builds (BtbN's among them) do not set; glibc does, however, satisfy a dependency with an
    // already loaded library of the same soname. Loading in this order is what lets a private directory
    // of FFmpeg libraries work without LD_LIBRARY_PATH. libavfilter is not bound, but libavdevice links it.
    private static readonly (string Name, int Major)[] s_linkOrder =
    [
        ("avutil", LibAVUtil.LIBAVUTIL_VERSION_MAJOR),
        ("swresample", LibSwResample.LIBSWRESAMPLE_VERSION_MAJOR),
        ("swscale", LibSwScale.LIBSWSCALE_VERSION_MAJOR),
        ("avcodec", LibAVCodec.LIBAVCODEC_VERSION_MAJOR),
        ("avformat", LibAVFormat.LIBAVFORMAT_VERSION_MAJOR),
        ("avfilter", LibAVFilter.LIBAVFILTER_VERSION_MAJOR),
        ("avdevice", LibAVDevice.LIBAVDEVICE_VERSION_MAJOR),
    ];

    /// <summary>
    /// A directory to load the libraries from before the default probe. Set it before the first call
    /// into FFmpeg; changing it after a library has loaded throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">A library has already been loaded.</exception>
    public static string? SearchDirectory
    {
        get
        {
            lock (s_gate)
            {
                return s_searchDirectory;
            }
        }
        set
        {
            lock (s_gate)
            {
                if (
                    s_handles.Count > 0
                    && !string.Equals(s_searchDirectory, value, StringComparison.Ordinal)
                )
                {
                    throw new InvalidOperationException(
                        "FFmpeg libraries are already loaded; set SearchDirectory before the first call into FFmpeg."
                    );
                }

                s_searchDirectory = value;
            }
        }
    }

    /// <summary>The file name of a library on the current OS, for example <c>avcodec-63.dll</c>.</summary>
    /// <param name="name">The short library name, as listed in <see cref="Libraries"/>.</param>
    /// <param name="major">The ABI major.</param>
    /// <returns>The platform file name.</returns>
    public static string FileName(string name, int major) =>
        OperatingSystem.IsWindows() ? $"{name}-{major}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{name}.{major}.dylib"
        : $"lib{name}.so.{major}";

    /// <summary>
    /// The version string of the loaded FFmpeg build (<c>av_version_info</c>), for example <c>n9.0.1</c>
    /// or a git description; loads the libraries if they are not loaded yet.
    /// </summary>
    public static unsafe string VersionInfo =>
        NativeString.Read(LibAVUtil.av_version_info()) ?? string.Empty;

    /// <summary>
    /// Loads every library now, so a missing or mismatched install fails at startup with a clear
    /// message rather than at the first codec call.
    /// </summary>
    /// <exception cref="DllNotFoundException">A library could not be found or loaded.</exception>
    /// <exception cref="BadImageFormatException">A library of another ABI major was found.</exception>
    public static void EnsureLoaded()
    {
        EnsureResolverInstalled();
        foreach ((string name, _) in Libraries)
        {
            _ = Load(name);
        }
    }

    // The address of an exported symbol, for passing an FFmpeg function where FFmpeg takes a callback
    // (av_log_default_callback, to restore FFmpeg's own logging).
    internal static nint GetExport(string library, string symbol)
    {
        EnsureResolverInstalled();
        return NativeLibrary.GetExport(Load(library), symbol);
    }

    // Called from the static constructor of every bindings class, so the resolver is in place before
    // the runtime binds the first P/Invoke of any of them.
    internal static void EnsureResolverInstalled()
    {
        lock (s_gate)
        {
            if (s_resolverInstalled)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(FFmpegLibraries).Assembly, Resolve);
            s_resolverInstalled = true;
        }
    }

    internal static nint Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath
    ) => Libraries.Any(l => l.Name == libraryName) ? Load(libraryName) : 0;

    private static nint Load(string name)
    {
        lock (s_gate)
        {
            if (s_handles.TryGetValue(name, out nint cached))
            {
                return cached;
            }

            foreach ((string library, int major) in s_linkOrder)
            {
                if (s_handles.ContainsKey(library))
                {
                    if (library == name)
                    {
                        break;
                    }

                    continue;
                }

                if (library != name && !Libraries.Any(l => l.Name == library))
                {
                    // A prerequisite the bindings do not import (libavfilter): a build without it is valid,
                    // and a library that does need it fails below with the loader's own reason.
                    if (TryLoadFrom(library, major, out nint prerequisite))
                    {
                        s_handles[library] = prerequisite;
                    }

                    continue;
                }

                s_handles[library] = LoadFrom(
                    library,
                    major,
                    ProbeDirectories(s_searchDirectory, AppContext.BaseDirectory)
                );
                if (library == name)
                {
                    break;
                }
            }

            return s_handles[name];
        }
    }

    // Loads one library from the first directory that has it, falling back to the OS loader's search,
    // and checks its major. Stateless, so the failure paths can be exercised without touching the
    // process-wide handles.
    internal static nint LoadFrom(string name, int major, IEnumerable<string> directories)
    {
        string file = FileName(name, major);
        nint handle = 0;
        DllNotFoundException? refused = null;
        foreach (string directory in directories)
        {
            string path = Path.Combine(directory, file);
            if (NativeLibrary.TryLoad(path, out handle))
            {
                break;
            }

            // A library that is present but will not load (a dependency of it is missing, or it is built
            // for another architecture) is a different fault from one that is absent; keep the OS
            // loader's own reason, which names what it could not resolve.
            if (refused is null && File.Exists(path))
            {
                refused = LoadError(path);
            }
        }

        if (handle == 0 && !NativeLibrary.TryLoad(file, out handle))
        {
            throw refused is not null
                ? new DllNotFoundException(
                    $"FFmpeg library '{file}' was found but could not be loaded: {refused.Message}",
                    refused
                )
                : new DllNotFoundException(
                    $"FFmpeg library '{file}' was not found. Set FFmpegLibraries.SearchDirectory, or place it in "
                        + $"runtimes/{RuntimeInformation.RuntimeIdentifier}/native, the application directory, or on the "
                        + "OS library search path."
                );
        }

        VerifyMajor(name, major, handle, file);
        return handle;
    }

    private static bool TryLoadFrom(string name, int major, out nint handle)
    {
        try
        {
            handle = LoadFrom(
                name,
                major,
                ProbeDirectories(s_searchDirectory, AppContext.BaseDirectory)
            );
            return true;
        }
        catch (DllNotFoundException)
        {
            // Deliberately not logged: FFmpeg's logging is not set up while its libraries load, and the
            // absence surfaces as the dependent library's own load failure, which carries the reason.
            handle = 0;
            return false;
        }
    }

    // NativeLibrary.Load throws with the loader's message (dlerror, or the Windows error) where TryLoad
    // only returns false.
    private static DllNotFoundException? LoadError(string path)
    {
        try
        {
            NativeLibrary.Free(NativeLibrary.Load(path));
            return null;
        }
        catch (DllNotFoundException error)
        {
            return error;
        }
        catch (BadImageFormatException error)
        {
            return new DllNotFoundException(error.Message, error);
        }
    }

    internal static IEnumerable<string> ProbeDirectories(string? configured, string baseDirectory)
    {
        if (configured is not null)
        {
            yield return configured;
        }

        yield return Path.Combine(
            baseDirectory,
            "runtimes",
            RuntimeInformation.RuntimeIdentifier,
            "native"
        );
        yield return baseDirectory;
    }

    // Every FFmpeg library exports <name>_version() returning AV_VERSION_INT(major, minor, micro),
    // with the major in the top 16 bits.
    internal static unsafe void VerifyMajor(string name, int major, nint handle, string file)
    {
        if (!NativeLibrary.TryGetExport(handle, $"{name}_version", out nint export))
        {
            throw new BadImageFormatException(
                $"'{file}' does not export {name}_version(); it is not an FFmpeg library."
            );
        }

        uint version = ((delegate* unmanaged[Cdecl]<uint>)export)();
        int loadedMajor = (int)(version >> 16);
        if (loadedMajor != major)
        {
            throw new BadImageFormatException(
                $"'{file}' reports {name} major {loadedMajor}, but these bindings were generated for major {major}."
            );
        }
    }
}
