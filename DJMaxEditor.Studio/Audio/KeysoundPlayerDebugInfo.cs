using System;
using System.Globalization;
using System.Text;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// Snapshot returned by <see cref="NAudioKeysoundPlayer.GetDebugInfo"/>, for the diagnostics
    /// overlay.
    /// <para>
    /// The legacy backend returned an <c>AudioPlayerFmodExDebugInfo</c> struct of FMOD CPU-usage
    /// percentages, which the old WinForms panel type-checked and unpacked. Nothing in the studio
    /// shell can consume that shape, and none of those numbers exist in a managed mixer, so this is
    /// a different (and considerably more useful) payload: what is loaded, what is playing, and what
    /// has gone wrong. <see cref="ToString"/> is the overlay's single-glance form.
    /// </para>
    /// </summary>
    public sealed class KeysoundPlayerDebugInfo
    {
        /// <summary>Identifies the implementation, so an overlay can label the backend in use.</summary>
        public string Backend { get; internal set; }

        /// <summary>Name of the concrete <see cref="IAudioOutput"/> that ended up being used.</summary>
        public string Output { get; internal set; }

        /// <summary>True when a real driver is open; false in no-device / no-sound mode.</summary>
        public bool IsRealDevice { get; internal set; }

        /// <summary>Mixer sample rate in Hz.</summary>
        public int SampleRate { get; internal set; }

        /// <summary>Mixer channel count.</summary>
        public int Channels { get; internal set; }

        /// <summary>Nominal output latency in milliseconds - see <see cref="IAudioOutput.LatencyMilliseconds"/>.</summary>
        public int LatencyMs { get; internal set; }

        /// <summary>Voices currently owning a channel or ringing out after a retrigger.</summary>
        public int ActiveVoices { get; internal set; }

        /// <summary>Channels of MAX_CHANNEL that hold a live voice.</summary>
        public int BusyChannels { get; internal set; }

        /// <summary>Inputs attached to the mixer. Must track <see cref="ActiveVoices"/>.</summary>
        public int MixerInputs { get; internal set; }

        /// <summary>Sounds held in the decode cache.</summary>
        public int CachedSounds { get; internal set; }

        /// <summary>Bytes of decoded PCM held by the cache.</summary>
        public long CachedBytes { get; internal set; }

        /// <summary>Cache budget in bytes; loads that would exceed it fail.</summary>
        public long CacheLimitBytes { get; internal set; }

        /// <summary>True while the playback group is paused (PauseAllSounds).</summary>
        public bool GlobalPaused { get; internal set; }

        /// <summary>Voices started since construction.</summary>
        public long VoiceStarts { get; internal set; }

        /// <summary>Voices ended by the mixer because they played out or were stopped.</summary>
        public long VoicesEnded { get; internal set; }

        /// <summary>Retriggers that hit a channel which still had a live voice.</summary>
        public long VoicesStolen { get; internal set; }

        /// <summary>LoadSound calls that failed (missing file, bad index, cache full, decode error).</summary>
        public long LoadFailures { get; internal set; }

        /// <summary>PlaySound calls that failed (bad index or the sound was never loaded).</summary>
        public long PlayFailures { get; internal set; }

        /// <summary>See <see cref="PlaybackGroupSampleProvider.Underruns"/> - a graph invariant, not a device glitch count.</summary>
        public long Underruns { get; internal set; }

        /// <summary>Buffers handed to the device.</summary>
        public long BuffersRendered { get; internal set; }

        /// <summary>Why the real device was not used, when it wasn't. Empty otherwise.</summary>
        public string OutputFallbackReason { get; internal set; }

        /// <summary>Most recent load failure, verbatim, because "which sample" is the first question.</summary>
        public string LastLoadError { get; internal set; }

        /// <inheritdoc/>
        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append(Backend).Append(" | ").Append(Output);
            if (!IsRealDevice)
            {
                text.Append(" (silent)");
            }

            text.Append(" | ").Append(SampleRate.ToString(CultureInfo.InvariantCulture)).Append(" Hz x")
                .Append(Channels.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(LatencyMs.ToString(CultureInfo.InvariantCulture)).Append(" ms");
            text.Append(" | voices ").Append(ActiveVoices.ToString(CultureInfo.InvariantCulture))
                .Append('/').Append(NAudioKeysoundPlayer.MAX_CHANNEL.ToString(CultureInfo.InvariantCulture))
                .Append(" (mixer ").Append(MixerInputs.ToString(CultureInfo.InvariantCulture)).Append(')');
            text.Append(" | cache ").Append(CachedSounds.ToString(CultureInfo.InvariantCulture))
                .Append(" snd ").Append(FormatBytes(CachedBytes)).Append('/').Append(FormatBytes(CacheLimitBytes));
            if (GlobalPaused)
            {
                text.Append(" | PAUSED");
            }

            text.Append(" | starts ").Append(VoiceStarts.ToString(CultureInfo.InvariantCulture))
                .Append(", stolen ").Append(VoicesStolen.ToString(CultureInfo.InvariantCulture))
                .Append(", ended ").Append(VoicesEnded.ToString(CultureInfo.InvariantCulture));
            text.Append(" | fail load ").Append(LoadFailures.ToString(CultureInfo.InvariantCulture))
                .Append(", play ").Append(PlayFailures.ToString(CultureInfo.InvariantCulture))
                .Append(", underrun ").Append(Underruns.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(OutputFallbackReason))
            {
                text.Append(" | output fallback: ").Append(OutputFallbackReason);
            }

            if (!string.IsNullOrEmpty(LastLoadError))
            {
                text.Append(" | last load error: ").Append(LastLoadError);
            }

            return text.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024L * 1024L)
            {
                return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            }

            return (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
