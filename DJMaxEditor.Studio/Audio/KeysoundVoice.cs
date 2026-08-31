using System;
using System.Threading;
using NAudio.Wave;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// One playing keysound: a fused volume + panning sample provider over a cached
    /// <see cref="KeysoundSample"/>.
    /// <para>
    /// Fused on purpose. The idiomatic NAudio chain would be
    /// <c>source -&gt; VolumeSampleProvider -&gt; PanningSampleProvider</c>, but
    /// <c>PanningSampleProvider</c> only accepts mono input while the cache is already in the
    /// mixer's stereo format, and a chart can have a hundred of these alive at once - three virtual
    /// calls and two intermediate buffers per voice per buffer is real work on the device thread.
    /// One loop does the same job with one multiply per sample.
    /// </para>
    /// <para>
    /// Lifetime rule that keeps <see cref="NAudio.Wave.SampleProviders.MixingSampleProvider"/>
    /// honest: NAudio drops a mixer input as soon as its <c>Read</c> returns <i>fewer samples than
    /// requested</i>. So a finished or stopped voice returns 0 and leaves the graph by itself, while
    /// a <b>paused</b> voice must return a completely zero-filled buffer - returning 0 there would
    /// silently evict it and it could never resume.
    /// </para>
    /// <para>
    /// A voice started with a hold length also <b>releases</b>: it fades to silence over the last few
    /// milliseconds of the note's body and then ends, instead of playing its file out. Without that,
    /// a one-beat hold on a two-second pad keeps sounding long after the note has left the playfield,
    /// which is the drone the arcade does not have.
    /// </para>
    /// </summary>
    internal sealed class KeysoundVoice : ISampleProvider
    {
        /// <summary>
        /// Per-channel gains, immutable so that a volume change is one atomic reference swap.
        /// Two separate float fields could tear across a buffer boundary (new left, old right);
        /// swapping one reference cannot.
        /// </summary>
        internal sealed class Gain
        {
            public Gain(float left, float right)
            {
                Left = left;
                Right = right;
            }

            public readonly float Left;
            public readonly float Right;
        }

        private readonly KeysoundSample _sample;
        private readonly int _channels;
        private readonly float _panLeft;
        private readonly float _panRight;

        private volatile Gain _gain;
        private volatile bool _paused;
        private volatile bool _stopped;
        private volatile bool _finished;

        /// <summary>Interleaved read cursor. Written by the device thread, read by anyone.</summary>
        private int _position;

        /// <summary>
        /// Interleaved index the voice must be silent at, or 0 for "ring out to the end of the file".
        /// </summary>
        private readonly int _limit;

        /// <summary>Interleaved index the release ramp starts at. Only meaningful when limited.</summary>
        private readonly int _fadeStart;

        /// <summary>
        /// Length of the release ramp. Short enough that a held pad still sounds like it was
        /// released on the beat, long enough that the cut is silence rather than a click - a hard
        /// truncation mid-waveform is a step to zero, which is a click you hear on every hold.
        /// </summary>
        private const double ReleaseMilliseconds = 8.0;

        internal KeysoundVoice(KeysoundSample sample, WaveFormat format, uint channelIndex, uint soundIndex,
            float volume, byte pan, int startFrame, int holdFrames = 0)
        {
            _sample = sample;
            WaveFormat = format;
            _channels = format.Channels;
            ChannelIndex = channelIndex;
            SoundIndex = soundIndex;
            Pan = pan;
            PanToGains(pan, out _panLeft, out _panRight);
            _gain = MakeGain(volume, _panLeft, _panRight);

            if (startFrame > 0)
            {
                long offset = (long)startFrame * _channels;
                if (offset >= sample.Samples.Length)
                {
                    // Seeking past the end starts the voice already finished, i.e. silent. FMOD
                    // would have failed the seek and played the sample from its beginning instead;
                    // that is worse musically - it fires a note the chart has already moved past at
                    // full volume. See NAudioKeysoundPlayer.PlaySound for the full note.
                    _finished = true;
                }
                else
                {
                    _position = (int)offset;
                }
            }

            if (holdFrames > 0)
            {
                // Measured from the sample's own start, not from _position: the cursor advances one
                // frame per elapsed frame, so an absolute limit is the note's length whether the
                // voice started at the note or was restored part-way into it after a seek.
                long limit = (long)holdFrames * _channels;
                if (limit < sample.Samples.Length)
                {
                    _limit = (int)limit;
                    int ramp = (int)(ReleaseMilliseconds * format.SampleRate / 1000.0) * _channels;
                    _fadeStart = Math.Max(_position, _limit - Math.Max(_channels, ramp));
                }
                // A hold longer than its own sample needs no limit: the file runs out first, which
                // is the one case where the arcade's keysound is what stops the note.
            }
        }

        /// <inheritdoc/>
        public WaveFormat WaveFormat { get; }

        /// <summary>Channel slot this voice was started on (already wrapped to MAX_CHANNEL).</summary>
        internal uint ChannelIndex { get; }

        /// <summary>Cache index of the sample being played.</summary>
        internal uint SoundIndex { get; }

        /// <summary>
        /// True while this voice still owns its channel: not stopped, not played out. A voice that
        /// is merely paused is still active, exactly like a paused FMOD channel.
        /// </summary>
        internal bool IsActive => !_stopped && !_finished;

        /// <summary>
        /// Playback position in the unit the legacy backend used: PCM frames counted at the
        /// <i>source file's</i> sample rate (FMOD's <c>TIMEUNIT.PCM</c>), not at the mixer rate.
        /// </summary>
        internal uint SourcePcmPosition
        {
            get
            {
                if (!IsActive)
                {
                    return 0u;
                }

                long mixerFrames = Volatile.Read(ref _position) / Math.Max(1, _channels);
                if (_sample.SourceSampleRate == _sample.SampleRate)
                {
                    return (uint)mixerFrames;
                }

                return (uint)(mixerFrames * _sample.SourceSampleRate / Math.Max(1, _sample.SampleRate));
            }
        }

        /// <summary>The pan byte this voice was started with, kept so SetVolume can rebuild the gains.</summary>
        internal byte Pan { get; }

        /// <summary>Replaces the linear volume, keeping the pan position. Lock-free by design.</summary>
        internal void SetVolume(float volume)
        {
            _gain = MakeGain(volume, _panLeft, _panRight);
        }

        private static Gain MakeGain(float volume, float panLeft, float panRight)
        {
            // Linear, and clamped at the bottom only.
            //
            // The legacy backend passed `volume` straight into FMOD's Channel::setVolume, which is a
            // linear 0..1 scale. It is deliberately not clamped at 1 here: MainForm computes
            // `track.Volume * velocity` where a .pt track volume can legitimately exceed unity, and
            // an existing repo comment states outright that clamping the product would quietly make
            // those charts play back softer than they used to. Negative values are clamped to
            // silence rather than treated as a phase inversion - nothing in the editor asks for one
            // and a stray sign would be an inaudible-yet-destructive bug.
            float gain = volume > 0f ? volume : 0f;
            return new Gain(gain * panLeft, gain * panRight);
        }

        /// <summary>
        /// Converts a charted pan byte into per-channel gains.
        /// <para>
        /// Range verified against the legacy backend rather than assumed: <c>AudioPlayerFmodEx</c>
        /// mapped the byte with <c>pan &gt; 64 ? (pan-64)/63 : (pan-64)/64</c> onto FMOD's -1..+1,
        /// and <c>EventData</c>'s constructor defaults <c>Pan</c> to 64. So the useful range is
        /// <b>0..127 with 64 as centre</b> - not 0..255 with 128 as centre. Values above 127 saturate,
        /// which is what FMOD did with the &gt;1.0 pan values that formula produced for them.
        /// </para>
        /// <para>
        /// The law is a linear balance: centre leaves both channels at unity and a hard pan silences
        /// the opposite channel. That is FMOD Ex's 2D pan behaviour, and matching it matters - a
        /// constant-power (-3 dB at centre) law would make every centred note in every existing
        /// chart quieter than it used to be, which is exactly the kind of silent global change that
        /// would be blamed on "the new audio engine".
        /// </para>
        /// </summary>
        internal static void PanToGains(byte pan, out float left, out float right)
        {
            float position;
            if (pan > 64)
            {
                position = (pan - 64) / 63f;
                if (position > 1f)
                {
                    position = 1f;
                }
            }
            else
            {
                position = (pan - 64) / 64f;
            }

            left = position <= 0f ? 1f : 1f - position;
            right = position >= 0f ? 1f : 1f + position;
        }

        /// <summary>Flips the pause flag and reports the new state, matching FMOD's toggle semantics.</summary>
        internal bool TogglePause()
        {
            bool paused = !_paused;
            _paused = paused;
            return paused;
        }

        internal void Stop()
        {
            _stopped = true;
        }

        /// <inheritdoc/>
        public int Read(float[] buffer, int offset, int count)
        {
            if (_stopped || _finished)
            {
                return 0; // NAudio removes us from the mixer on this return.
            }

            if (_paused)
            {
                // Full buffer of silence: stays on the mixer, position frozen. Samples, not bytes -
                // see SampleBuffers for what Array.Clear does to a Read buffer.
                SampleBuffers.Silence(buffer, offset, count);
                return count;
            }

            Gain gain = _gain;
            float[] source = _sample.Samples;
            int position = _position;
            int available = source.Length - position;
            if (available <= 0)
            {
                _finished = true;
                return 0;
            }

            if (_limit > 0)
            {
                // The note's body ended. Everything past the limit is the sample ringing on with
                // nothing on the playfield holding it, which is the drone the arcade does not have.
                int remaining = _limit - position;
                if (remaining <= 0)
                {
                    _finished = true;
                    return 0;
                }

                if (remaining < available)
                {
                    available = remaining;
                }
            }

            int copy = Math.Min(count, available);
            if (_channels == 2)
            {
                // Stereo fast path - the mixer format is always stereo, so this is the hot loop.
                float left = gain.Left;
                float right = gain.Right;
                int channel = position & 1;
                for (int i = 0; i < copy; i++)
                {
                    buffer[offset + i] = source[position + i] * (channel == 0 ? left : right);
                    channel ^= 1;
                }
            }
            else
            {
                int channel = _channels > 0 ? position % _channels : 0;
                for (int i = 0; i < copy; i++)
                {
                    buffer[offset + i] = source[position + i] * (channel == 0 ? gain.Left : gain.Right);
                    if (++channel >= _channels)
                    {
                        channel = 0;
                    }
                }
            }

            if (_limit > 0 && position + copy > _fadeStart)
            {
                ApplyRelease(buffer, offset, position, copy);
            }

            Volatile.Write(ref _position, position + copy);

            if (copy < count)
            {
                // Played out inside this buffer. Flag it before returning so the channel reads as
                // idle immediately (a finished FMOD channel handle went invalid the same way), and
                // the short return makes NAudio drop us from the mixer in this very pass.
                _finished = true;
            }
            else if (_limit > 0 && position + copy >= _limit)
            {
                // Filled the buffer and landed exactly on the release. The read was full length, so
                // the mixer keeps us for one more pass; the flag makes that pass return 0.
                _finished = true;
            }

            return copy;
        }

        /// <summary>
        /// Ramps the tail of a just-copied buffer down to silence over the last
        /// <see cref="ReleaseMilliseconds"/> before the note's release.
        /// <para>
        /// Linear, and applied per interleaved sample rather than per frame: at 48 kHz the two
        /// channels of one frame differ by 1/288th of the ramp, which is inaudible, and the frame
        /// arithmetic would put a division in the hot loop for nothing.
        /// </para>
        /// </summary>
        private void ApplyRelease(float[] buffer, int offset, int position, int copy)
        {
            int span = _limit - _fadeStart;
            if (span <= 0)
            {
                return;
            }

            int from = Math.Max(position, _fadeStart);
            int to = position + copy;
            for (int p = from; p < to; p++)
            {
                float scale = (_limit - p) / (float)span;
                buffer[offset + (p - position)] *= scale > 0f ? scale : 0f;
            }
        }
    }
}
