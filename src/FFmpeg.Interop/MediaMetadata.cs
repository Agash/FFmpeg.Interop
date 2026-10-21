using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVFormat;

namespace FFmpeg.Interop;

/// <summary>A chapter of a <see cref="MediaReader"/>'s input.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Metadata">Its tags, such as <c>title</c>.</param>
public sealed record Chapter(
    TimeSpan Start,
    TimeSpan End,
    ImmutableDictionary<string, string> Metadata
)
{
    /// <summary>Its title, when it has one.</summary>
    public string? Title => Metadata.GetValueOrDefault("title");
}

/// <summary>How a stream is meant to be used (<c>AV_DISPOSITION_*</c>).</summary>
[Flags]
public enum StreamDisposition
{
    /// <summary>None stated.</summary>
    None = 0,

    /// <summary>The stream to pick among those of its kind when nothing else decides.</summary>
    Default = AV_DISPOSITION_DEFAULT,

    /// <summary>A dub: audio in another language than the original.</summary>
    Dub = AV_DISPOSITION_DUB,

    /// <summary>The original language.</summary>
    Original = AV_DISPOSITION_ORIGINAL,

    /// <summary>A commentary.</summary>
    Comment = AV_DISPOSITION_COMMENT,

    /// <summary>Lyrics.</summary>
    Lyrics = AV_DISPOSITION_LYRICS,

    /// <summary>Karaoke.</summary>
    Karaoke = AV_DISPOSITION_KARAOKE,

    /// <summary>Subtitles shown whatever the viewer chose.</summary>
    Forced = AV_DISPOSITION_FORCED,

    /// <summary>For the hearing impaired.</summary>
    HearingImpaired = AV_DISPOSITION_HEARING_IMPAIRED,

    /// <summary>For the visually impaired.</summary>
    VisualImpaired = AV_DISPOSITION_VISUAL_IMPAIRED,

    /// <summary>Music and effects without dialogue.</summary>
    CleanEffects = AV_DISPOSITION_CLEAN_EFFECTS,

    /// <summary>A still picture, such as cover art, rather than video.</summary>
    AttachedPicture = AV_DISPOSITION_ATTACHED_PIC,

    /// <summary>Thumbnails at intervals.</summary>
    TimedThumbnails = AV_DISPOSITION_TIMED_THUMBNAILS,

    /// <summary>Audio that is not part of the scene, such as narration.</summary>
    NonDiegetic = AV_DISPOSITION_NON_DIEGETIC,

    /// <summary>Captions.</summary>
    Captions = AV_DISPOSITION_CAPTIONS,

    /// <summary>Descriptions of the video.</summary>
    Descriptions = AV_DISPOSITION_DESCRIPTIONS,

    /// <summary>Metadata rather than media.</summary>
    Metadata = AV_DISPOSITION_METADATA,

    /// <summary>Meant to be presented with another stream.</summary>
    Dependent = AV_DISPOSITION_DEPENDENT,

    /// <summary>A still image in a video stream.</summary>
    StillImage = AV_DISPOSITION_STILL_IMAGE,

    /// <summary>Several layers, such as multiview video.</summary>
    Multilayer = AV_DISPOSITION_MULTILAYER,
}

internal static unsafe class NativeMetadata
{
    /// <summary>A native dictionary's entries.</summary>
    public static ImmutableDictionary<string, string> Read(AVDictionary* dictionary)
    {
        if (dictionary is null)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        ImmutableDictionary<string, string>.Builder tags = ImmutableDictionary.CreateBuilder<
            string,
            string
        >(StringComparer.OrdinalIgnoreCase);
        AVDictionaryEntry* entry = null;
        while ((entry = LibAVUtil.av_dict_iterate(dictionary, entry)) is not null)
        {
            string? key = Marshal.PtrToStringUTF8((nint)entry->key);
            if (key is not null)
            {
                tags[key] = Marshal.PtrToStringUTF8((nint)entry->value) ?? string.Empty;
            }
        }

        return tags.ToImmutable();
    }
}
