using System;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// The managed stand-in for the legacy backend's FMOD <c>ChannelGroup</c>: the single node the
    /// device pulls from, sitting directly on top of the voice mixer.
    /// <para>
    /// It exists for one behavioural reason. <c>PauseAllSounds</c> toggled the pause flag of the
    /// group that every channel belonged to, and a paused FMOD group <i>freezes</i> its channels -
    /// an existing repo test (<c>Bms_AudioPauseFreezesEveryOverlappingLongSample</c>) asserts that
    /// PCM positions of overlapping samples do not advance while paused. Reproducing that here is
    /// exact rather than approximate: while paused the group never reads the mixer at all, so no
    /// voice can advance, and it hands the device a full buffer of silence so the driver never
    /// starves.
    /// </para>
    /// <para>
    /// Freezing is not the same as cutting, though, and the difference is audible. Clearing the
    /// buffer the instant the flag goes up steps every sounding voice from wherever its waveform
    /// happened to be straight to zero, and lifting the flag steps it straight back - two full-scale
    /// discontinuities per pause, on every voice at once. <see cref="KeysoundVoice"/> already ramps
    /// its release over 8 ms for that exact reason ("a hard truncation mid-waveform is a step to
    /// zero, which is a click you hear on every hold"); the group did not, so pausing on top of a
    /// hold gave the click of the whole mix instead of silence, and a handful of quick pauses gave a
    /// handful of them. So the pause is faded: the flag still decides, but the mix reaches zero over
    /// <see cref="PauseFadeMilliseconds"/> and leaves it the same way. The freeze itself is
    /// unchanged - once the fade is out the mixer is not read at all - and the cost is bounded and
    /// stated: the voices advance by those 8 ms and no further.
    /// </para>
    /// <para>
    /// It is also where the mix gets its headroom. Keysounds ship mastered to full scale - measured:
    /// the loudest single sample in <c>sonoflong_pop_3</c> peaks at +0.6 dBFS on its own - and a
    /// chart plays several at once, so the sum overflows. Rendering 25 s of that chart through this
    /// graph with no headroom gave a peak of +6.7 dBFS and clipped 1.4% of all samples, which is
    /// audible as harsh crackling distortion on every dense bar and is what "the sound is horrifying"
    /// turned out to be. Both stages below exist to stop that, and both are measurable through
    /// <c>--audio-render</c>.
    /// </para>
    /// </summary>
    internal sealed class PlaybackGroupSampleProvider : ISampleProvider
    {
        /// <summary>
        /// Default master attenuation, linear. -6 dB, chosen from the measurement rather than by
        /// taste: it is the headroom two simultaneous full-scale keysounds need, which is what a
        /// TECHNIKA chart's densest bars actually produce.
        /// </summary>
        internal const float DefaultMasterGain = 0.5f;

        /// <summary>
        /// Where the limiter starts working, linear. Just under full scale so that the 16-bit
        /// conversion at the device cannot round a sample up to clipping.
        /// </summary>
        private const float LimitThreshold = 0.98f;

        /// <summary>
        /// Limiter release, seconds. Long enough that a struck note is not audibly ducked by the one
        /// before it, short enough that one loud bar does not hold the whole mix down.
        /// </summary>
        private const double ReleaseSeconds = 0.08;

        /// <summary>
        /// Length of the pause and resume fade, milliseconds. The same 8 ms
        /// <see cref="KeysoundVoice"/> releases a hold over, and for the same reason - short enough
        /// that the transport still feels like it stopped on the frame it was asked to, long enough
        /// that the mix arrives at zero instead of stepping there.
        /// </summary>
        private const double PauseFadeMilliseconds = 8.0;

        private readonly MixingSampleProvider _mixer;
        private readonly float _releaseCoefficient;

        /// <summary>
        /// Length of the pause fade in frames.
        /// <para>
        /// The ramp is counted in whole frames rather than accumulated as a float per buffer, so it
        /// is exactly this long every time and lands exactly on zero. Drifting by a frame would be
        /// inaudible; it would also mean the fade could finish a frame after the buffer that was
        /// sized to hold it, leaving the group reading one stray frame out of the mixer per pause
        /// and the counters below permanently one out.
        /// </para>
        /// </summary>
        private readonly int _fadeFrames;

        private volatile bool _paused;
        private volatile float _masterGain = DefaultMasterGain;
        private volatile bool _limiterEnabled = true;
        private float _reduction = 1f;

        /// <summary>
        /// Where the pause fade stands, in frames: <see cref="_fadeFrames"/> fully open, 0 fully
        /// shut. Touched only on the device thread, inside a read.
        /// </summary>
        private int _fadePosition;

        private int _activeVoiceHint;
        private long _buffersRendered;
        private long _silentBuffers;
        private long _pausedBuffers;
        private long _fadedFrames;
        private long _fadeOutsAnnounced;
        private long _shortReads;
        private long _limitedFrames;
        private int _peakBits;
        private string _lastRead;
        private readonly System.Collections.Generic.List<string> _recent = new System.Collections.Generic.List<string>();
        private int _traceThread;
        private long _traceThreadChanges;

        internal PlaybackGroupSampleProvider(MixingSampleProvider mixer)
        {
            _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));

            int rate = _mixer.WaveFormat.SampleRate;
            _releaseCoefficient = rate > 0
                ? (float)(1.0 - Math.Exp(-1.0 / (ReleaseSeconds * rate)))
                : 1f;
            _fadeFrames = Math.Max(1, (int)(PauseFadeMilliseconds * rate / 1000.0));
            _fadePosition = _fadeFrames;
        }

        /// <inheritdoc/>
        public WaveFormat WaveFormat => _mixer.WaveFormat;

        /// <summary>Group-wide pause, mirroring <c>ChannelGroup.setPaused</c>.</summary>
        internal bool Paused
        {
            get => _paused;
            set => _paused = value;
        }

        /// <summary>
        /// Called once, on the device thread, the frame the pause fade reaches zero.
        /// <para>
        /// This is the only moment at which a voice can be torn down without a click: the mix is
        /// already inaudible, and from the next buffer on the mixer is not read at all. The player
        /// hangs the transport's "pause means stop" teardown off it - see
        /// <c>NAudioKeysoundPlayer.FadeOutAndSilence</c> - because a caller on the UI thread cannot
        /// know when the ramp finished without either blocking or guessing at the device's latency.
        /// </para>
        /// </summary>
        internal Action FadedOut { get; set; }

        /// <summary>
        /// Linear master attenuation applied to the whole mix, before the limiter. 1.0 disables the
        /// headroom stage and leaves only the limiter, which is how a test can look at the raw sum.
        /// </summary>
        internal float MasterGain
        {
            get => _masterGain;
            set => _masterGain = value >= 0f ? value : 0f;
        }

        /// <summary>Frames the limiter had to pull down. Zero means the headroom alone was enough.</summary>
        internal long LimitedFrames => Interlocked.Read(ref _limitedFrames);

        /// <summary>
        /// Whether the peak limiter runs. Off leaves the master gain applied and nothing else, which
        /// is only useful for measuring what the mix would have done without it.
        /// </summary>
        internal bool LimiterEnabled
        {
            get => _limiterEnabled;
            set => _limiterEnabled = value;
        }

        /// <summary>Clears the peak and limiter counters so a following measurement stands alone.</summary>
        internal void ResetMeters()
        {
            Interlocked.Exchange(ref _limitedFrames, 0);
            Volatile.Write(ref _peakBits, 0);
            _reduction = 1f;
        }

        /// <summary>
        /// Loudest sample seen leaving this node since startup, linear. Should never exceed
        /// <see cref="LimitThreshold"/> once the limiter is in the path; that is the property worth
        /// asserting, because it is the one the device cares about.
        /// </summary>
        internal float PeakOut => BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakBits));

        /// <summary>
        /// How many voices the player believes are alive. Written by the player, read here on the
        /// device thread so silent buffers can be told apart from starved ones without touching the
        /// mixer's input list (which is not safe to enumerate from the device thread).
        /// </summary>
        internal int ActiveVoiceHint
        {
            get => Volatile.Read(ref _activeVoiceHint);
            set => Volatile.Write(ref _activeVoiceHint, value);
        }

        /// <summary>Buffers handed to the device since startup.</summary>
        internal long BuffersRendered => Interlocked.Read(ref _buffersRendered);

        /// <summary>Buffers rendered with no voice alive - the normal idle case, useful as a heartbeat.</summary>
        internal long SilentBuffers => Interlocked.Read(ref _silentBuffers);

        /// <summary>
        /// Buffers cleared because the group was paused.
        /// <para>
        /// Separate from <see cref="SilentBuffers"/>, which counts idling with no voices alive. This
        /// one is what makes a pause verifiable rather than assumed: across a pause the delta here
        /// should equal the delta in <see cref="BuffersRendered"/> less the buffers the fade out
        /// occupied - one, at any device buffer of 8 ms or more - meaning every buffer the device was
        /// handed after the mix reached zero was a cleared one and no voice advanced past it.
        /// </para>
        /// </summary>
        internal long PausedBuffers => Interlocked.Read(ref _pausedBuffers);

        /// <summary>
        /// Frames rendered while the pause fade was moving, since startup. The fade is the part of a
        /// pause that cannot be seen in <see cref="PausedBuffers"/> - those count the buffers the
        /// mixer was never read for - so this is how a diagnostic tells "faded to zero" apart from
        /// "cut to zero", which sound quite different and are one flag apart in the code.
        /// </summary>
        internal long FadedFrames => Interlocked.Read(ref _fadedFrames);

        /// <summary>
        /// How many times <see cref="FadedOut"/> has actually been raised.
        /// <para>
        /// Here because three rounds of this bug were each diagnosed from a log that could not answer
        /// it. <see cref="FadedFrames"/> says the ramp ran and <see cref="PausedBuffers"/> says the
        /// buffers afterwards were cleared, but neither says the announcement reached the player, and
        /// the mixer input count at the next transport edge cannot stand in for it - the shell's
        /// resume re-strikes voices before it logs, so a count of 3 there means three *new* voices,
        /// not three survivors. Reading it that way is what sent this session looking for a teardown
        /// failure that was not there. This counter is the fact itself.
        /// </para>
        /// </summary>
        internal long FadeOutsAnnounced => Interlocked.Read(ref _fadeOutsAnnounced);

        /// <summary>Length of the pause fade in frames, which is what a paused voice advances by.</summary>
        internal int FadeFrames => _fadeFrames;

        /// <summary>
        /// Where the pause fade stands, 1 open and 0 shut. Only meaningful between reads, which is
        /// the only place anything asks.
        /// </summary>
        internal float FadeLevel => (float)_fadePosition / _fadeFrames;

        /// <summary>
        /// Buffers the mixer failed to fill completely while voices were alive.
        /// <para>
        /// This is the closest thing to an underrun counter that is honestly measurable: NAudio
        /// exposes no glitch count for WASAPI shared mode or waveOut, so a real device-side dropout
        /// is invisible from managed code. What this does catch is the graph-level fault - the mixer
        /// is configured <c>ReadFully</c>, so a short read means something upstream misbehaved and
        /// the device was handed padding instead of audio. It should stay at 0 forever.
        /// </para>
        /// </summary>
        internal long Underruns => Interlocked.Read(ref _shortReads);

        /// <inheritdoc/>
        public int Read(float[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _buffersRendered);

            // Sampled once for the whole buffer: a pause pressed part-way through one takes effect
            // on the next, which is a tenth of a millisecond either way and keeps the ramp monotone.
            bool paused = _paused;
            int fadeAtEntry = _fadePosition;

            // The freeze, unchanged - but only once the fade has finished. While it is still moving
            // the mixer has to be read, because there is nothing to fade otherwise.
            if (paused && _fadePosition <= 0)
            {
                Interlocked.Increment(ref _pausedBuffers);
                Silence(buffer, offset, count);
                Trace("freeze", buffer, offset, count, count, count, paused, fadeAtEntry);
                return count;
            }

            int channels = Math.Max(1, _mixer.WaveFormat.Channels);
            int wanted = count;
            if (paused)
            {
                // While shutting, read no further into the mix than the fade itself needs. Reading
                // the whole buffer would work - everything past the ramp is multiplied by zero - but
                // the voices would advance by a device buffer instead of by 8 ms, so how far a
                // paused hold crept forward would depend on the driver's buffer size, and resuming
                // one would jump. Bounding it here keeps the freeze exact at any latency.
                int samples = _fadePosition * channels;
                if (samples < wanted)
                {
                    wanted = samples;
                }
            }

            int voices = Volatile.Read(ref _activeVoiceHint);
            bool wasSounding = _fadePosition > 0;
            int read = _mixer.Read(buffer, offset, wanted);

            if (read < wanted)
            {
                Silence(buffer, offset + read, wanted - read);
                if (voices > 0)
                {
                    Interlocked.Increment(ref _shortReads);
                }
            }

            if (voices == 0)
            {
                Interlocked.Increment(ref _silentBuffers);
            }

            Limit(buffer, offset, wanted, !paused);

            if (wanted < count)
            {
                Silence(buffer, offset + wanted, count - wanted);
            }

            // Announced after the buffer is finished rather than from inside the ramp: the handler
            // empties the mixer, and the mix this buffer is carrying was read out of it.
            if (paused && wasSounding && _fadePosition <= 0)
            {
                Interlocked.Increment(ref _fadeOutsAnnounced);
                FadedOut?.Invoke();
            }

            Trace(paused ? "ramp" : "open", buffer, offset, count, wanted, read, paused, fadeAtEntry);
            return count;
        }

        /// <summary>
        /// Zeroes samples rather than bytes - see <see cref="SampleBuffers"/> for why that distinction
        /// is the difference between a silent pause and a squealing one.
        /// </summary>
        private static void Silence(float[] buffer, int offset, int count)
        {
            SampleBuffers.Silence(buffer, offset, count);
        }

        /// <summary>
        /// Records how one buffer was filled, when <see cref="Tracing"/> is on. Off by default and
        /// never on in the editor: this allocates and walks the buffer a second time.
        /// <para>
        /// It exists because the counters were not enough. They said forty consecutive buffers had
        /// been cleared while paused, and a meter on the very next stage said those same buffers
        /// carried audio from sample 242 of 966 onwards. Both were telling the truth: the clear was
        /// covering 966 bytes of a buffer measured in samples. What settled it was printing the
        /// buffer's runtime type, which is why that is still printed here.
        /// </para>
        /// </summary>
        internal bool Tracing { get; set; }

        /// <summary>The last traced buffer, or null when nothing has been traced.</summary>
        internal string LastRead => Volatile.Read(ref _lastRead);

        /// <summary>
        /// The last ten traced buffers, oldest first - because the single last one turned out not to
        /// be enough. A clear that appears not to have happened is far more likely to be a clear that
        /// happened over a different length; that only shows up in the sequence of <c>count</c> values
        /// against what each buffer still holds, never in one of them alone.
        /// </summary>
        internal string RecentReads
        {
            get
            {
                lock (_recent)
                {
                    return string.Join(" | ", _recent);
                }
            }
        }

        private void Trace(string branch, float[] buffer, int offset, int count, int wanted, int read,
            bool paused, int fadeAtEntry)
        {
            if (!Tracing)
            {
                return;
            }

            int first = -1;
            int last = -1;
            int sounding = 0;
            for (int i = 0; i < count; i++)
            {
                if (buffer[offset + i] == 0f)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                }

                last = i;
                sounding++;
            }

            // Which thread pulled this, and how many buffers arrived on a thread other than the first
            // one seen. Not idle curiosity: stopping and restarting a WasapiOut leaves the old render
            // thread behind and starts a new one, and this is how that shows up.
            int thread = Environment.CurrentManagedThreadId;
            int seenThread = Interlocked.CompareExchange(ref _traceThread, thread, 0);
            if (seenThread != 0 && seenThread != thread)
            {
                Interlocked.Increment(ref _traceThreadChanges);
            }

            // type and len together, always: a Byte[] of length 23040 arriving as a float[] is a
            // 5760-sample buffer wearing its byte count, and every range argument in this file has to
            // be read in that light.
            Volatile.Write(ref _lastRead,
                branch + ": count=" + count + " offset=" + offset + " wanted=" + wanted +
                " read=" + read + " paused=" + paused + " fadeIn=" + fadeAtEntry +
                " fadeOut=" + _fadePosition + " nonZero=" + sounding +
                " first=" + first + " last=" + last +
                " type=" + buffer.GetType().Name + " len=" + buffer.Length +
                " thread=" + thread +
                " threadChanges=" + Interlocked.Read(ref _traceThreadChanges));

            lock (_recent)
            {
                _recent.Add(branch[0] + " c=" + count + " w=" + wanted + " nz=" + sounding +
                    " f=" + first + " t=" + thread);
                if (_recent.Count > 10)
                {
                    _recent.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Applies the master gain, then a peak limiter, then the pause fade, in place.
        ///
        /// <para>
        /// Attack is instantaneous and release is exponential, which is the shape a safety limiter
        /// wants: a look-ahead limiter would sound smoother but has to delay the whole mix to do it,
        /// and latency between a key and its sound is the one thing a rhythm editor cannot spend. The
        /// gain is per frame rather than per sample so the stereo image cannot wobble, and it is held
        /// across buffers so a peak at a buffer boundary is not released and re-attacked.
        /// </para>
        ///
        /// <para>
        /// The fade goes last and deliberately outside the limiter's own reading of the frame: the
        /// limiter must not see the mix getting quieter and start releasing, or a pause would end in
        /// a swell as the reduction came off under it.
        /// </para>
        /// </summary>
        private void Limit(float[] buffer, int offset, int count, bool fadeOpen)
        {
            float master = _masterGain;
            bool limiting = _limiterEnabled;
            int channels = Math.Max(1, _mixer.WaveFormat.Channels);
            float reduction = _reduction;
            int fade = _fadePosition;
            int fadeTarget = fadeOpen ? _fadeFrames : 0;
            float fadeScale = 1f / _fadeFrames;
            float peak = PeakOut;
            long limited = 0;
            long faded = 0;

            for (int frame = offset; frame + channels <= offset + count; frame += channels)
            {
                float loudest = 0f;
                for (int c = 0; c < channels; c++)
                {
                    float scaled = buffer[frame + c] * master;
                    buffer[frame + c] = scaled;
                    float magnitude = scaled >= 0f ? scaled : -scaled;
                    if (magnitude > loudest)
                    {
                        loudest = magnitude;
                    }
                }

                if (limiting)
                {
                    float target = loudest * reduction > LimitThreshold
                        ? LimitThreshold / loudest
                        : 1f;

                    if (target < reduction)
                    {
                        reduction = target;
                    }
                    else
                    {
                        reduction += (1f - reduction) * _releaseCoefficient;
                    }

                    if (reduction < 1f)
                    {
                        limited++;
                        for (int c = 0; c < channels; c++)
                        {
                            buffer[frame + c] *= reduction;
                        }
                    }
                }

                if (fade != fadeTarget)
                {
                    fade += fade < fadeTarget ? 1 : -1;
                    faded++;
                }

                if (fade < _fadeFrames)
                {
                    float level = fade * fadeScale;
                    for (int c = 0; c < channels; c++)
                    {
                        buffer[frame + c] *= level;
                    }
                }

                for (int c = 0; c < channels; c++)
                {
                    float magnitude = buffer[frame + c];
                    magnitude = magnitude >= 0f ? magnitude : -magnitude;
                    if (magnitude > peak)
                    {
                        peak = magnitude;
                    }
                }
            }

            _reduction = reduction;
            _fadePosition = fade;
            if (limited > 0)
            {
                Interlocked.Add(ref _limitedFrames, limited);
            }
            if (faded > 0)
            {
                Interlocked.Add(ref _fadedFrames, faded);
            }
            Volatile.Write(ref _peakBits, BitConverter.SingleToInt32Bits(peak));
        }
    }
}
