using System;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// One fully decoded keysound, already converted to the mixer's format.
    /// <para>
    /// Decoding and resampling happen once, in <see cref="KeysoundDecoder"/>, at load time. That is
    /// the single most important performance decision in this backend: a Respect-style chart fires
    /// dozens of overlapping notes per second, so anything left to do per playback (opening a file,
    /// running Vorbis, resampling 44.1 -> 48 kHz) would land on the device thread and glitch. A
    /// voice therefore does nothing but multiply and copy floats out of <see cref="Samples"/>.
    /// </para>
    /// <para>
    /// Instances are immutable once handed to the player, so any number of voices can read the same
    /// sample concurrently without locking. Replacing an index in the cache allocates a new
    /// instance rather than mutating this one, which is why voices already playing the old buffer
    /// stay valid.
    /// </para>
    /// </summary>
    internal sealed class KeysoundSample
    {
        public KeysoundSample(float[] samples, int sampleRate, int channels, int sourceSampleRate, int sourceChannels, string path, int mode)
        {
            Samples = samples ?? throw new ArgumentNullException(nameof(samples));
            SampleRate = sampleRate;
            Channels = channels;
            SourceSampleRate = sourceSampleRate;
            SourceChannels = sourceChannels;
            Path = path;
            Mode = mode;
        }

        /// <summary>Interleaved PCM at <see cref="SampleRate"/> / <see cref="Channels"/>.</summary>
        public float[] Samples { get; }

        /// <summary>Always the mixer rate - see the class remarks.</summary>
        public int SampleRate { get; }

        /// <summary>Always the mixer channel count.</summary>
        public int Channels { get; }

        /// <summary>
        /// Rate of the file on disk. Kept because the legacy backend expressed both the
        /// <c>offset</c> argument of <c>PlaySound</c> and the return value of <c>GetPosition</c> in
        /// FMOD's <c>TIMEUNIT.PCM</c>, which is counted at the sound's own default frequency - not
        /// at the output rate. Preserving it is what lets this backend keep those units identical.
        /// </summary>
        public int SourceSampleRate { get; }

        /// <summary>Channel count of the file on disk, for diagnostics only.</summary>
        public int SourceChannels { get; }

        /// <summary>Resolved path that was actually decoded (may differ from the charted name).</summary>
        public string Path { get; }

        /// <summary>
        /// The <c>mode</c> argument <c>LoadSound</c> was called with. Recorded for diagnostics; see
        /// <see cref="NAudioKeysoundPlayer.LoadSound"/> for why it no longer changes behaviour.
        /// </summary>
        public int Mode { get; }

        /// <summary>Length in frames (per-channel samples).</summary>
        public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;

        /// <summary>Heap cost of the decoded buffer, for the cache budget and the debug overlay.</summary>
        public long ByteCount => (long)Samples.Length * sizeof(float);
    }
}
