using System.Text.Json.Serialization;

namespace Novalist.Extensions.Speech;

/// <summary>
/// What the host asks the sidecar for, and what comes back.
///
/// One JSON object per line, in both directions. A line-delimited protocol over
/// stdio rather than a socket or a local HTTP server: there is no port to
/// collide, nothing to firewall, nothing listening when Novalist is not running,
/// and the sidecar dies with the process that started it. It also keeps the
/// no-network promise structural rather than a matter of trust - this engine
/// opens no socket at all.
///
/// Audio never travels in these messages. The sidecar writes each clip to the
/// working directory and names it; the extension reads the file. Base64 over a
/// pipe would inflate every clip by a third and put a chapter of speech through
/// a JSON parser twice.
/// </summary>
internal static class SidecarProtocol
{
    /// <summary>Bump when the message shape changes. The sidecar reports the
    /// version it speaks in its ready line, and a mismatch is a clear failure
    /// rather than a field silently missing.</summary>
    public const int Version = 1;
}

/// <summary>A request to the sidecar.</summary>
internal sealed class SidecarRequest
{
    /// <summary>"status", "design" or "render".</summary>
    [JsonPropertyName("op")]
    public string Op { get; init; } = string.Empty;

    /// <summary>
    /// Which request this is. Echoed on every reply to it.
    ///
    /// The sidecar renders a window one line at a time and cannot be
    /// interrupted while it is inside the model, so a reading the writer
    /// stopped goes on being spoken into the working directory after the host
    /// has given up listening. Without a stamp, those replies arrive in the
    /// middle of the *next* request and are read as answers to it - and the
    /// files they name have since been deleted or written over, which is a
    /// FileNotFoundException, a dead reading and no sound at all.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("voiceId")]
    public string? VoiceId { get; init; }

    /// <summary>The design brief. Describes the instrument only - the host has
    /// already stripped the emotion vocabulary out of it.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>A few lines the character actually speaks, for a design model
    /// that can use them.</summary>
    [JsonPropertyName("sampleLines")]
    public string[]? SampleLines { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("rate")]
    public double? Rate { get; init; }

    /// <summary>Voice id to the file holding that voice's reference audio. The
    /// extension writes them into the working directory before asking.</summary>
    [JsonPropertyName("voices")]
    public Dictionary<string, string>? Voices { get; init; }

    [JsonPropertyName("segments")]
    public SidecarSegment[]? Segments { get; init; }
}

/// <summary>One stretch to speak.</summary>
internal sealed class SidecarSegment
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("voiceId")]
    public string VoiceId { get; init; } = string.Empty;

    [JsonPropertyName("isDialogue")]
    public bool IsDialogue { get; init; }

    /// <summary>The emotion as numbers, for the delivery model's own dimensions.
    /// Empty when the host decided this engine should not be steered.</summary>
    [JsonPropertyName("vector")]
    public Dictionary<string, double> Vector { get; init; } = [];

    /// <summary>The emotion as a sentence, for a model that takes one.</summary>
    [JsonPropertyName("instruction")]
    public string Instruction { get; init; } = string.Empty;

    /// <summary>The emotion's name, always present, for logging and for a model
    /// that takes a label.</summary>
    [JsonPropertyName("emotion")]
    public string Emotion { get; init; } = "neutral";
}

/// <summary>One line back from the sidecar.</summary>
internal sealed class SidecarReply
{
    /// <summary>"ready", "clip", "designed", "progress", "done" or "error".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>The request this answers. Empty on anything the sidecar says of
    /// its own accord, which the host judges on its own terms.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    /// <summary>What it is and where it runs - "IndexTTS-2 on cuda:0". Shown in
    /// settings and written to the log.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    /// <summary>The segment this clip belongs to.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The clip's file name inside the working directory.</summary>
    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; init; }

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; init; }

    /// <summary>Coarse step name while preparing or loading.</summary>
    [JsonPropertyName("step")]
    public string Step { get; init; } = string.Empty;

    [JsonPropertyName("fraction")]
    public double? Fraction { get; init; }

    /// <summary>What went wrong. Never shown to the writer as the whole
    /// explanation: it is the model's own words, untranslated, and may name a
    /// path.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
