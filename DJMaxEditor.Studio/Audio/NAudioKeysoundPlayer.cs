using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// Fully managed <see cref="IAudioPlayer"/> for keysound playback, replacing the 32-bit native
    /// FMOD Ex P/Invoke backend (<c>AudioPlayerFmodEx</c>).
    /// <para>
    /// Shape of the graph: many <see cref="KeysoundVoice"/> inputs -&gt; one
    /// <see cref="MixingSampleProvider"/> at 48 kHz stereo float -&gt; one
    /// <see cref="PlaybackGroupSampleProvider"/> (the group pause) -&gt; one shared
    /// <see cref="IAudioOutput"/>. One device for the whole application, never one per sound: a
    /// chart fires dozens of overlapping keysounds per second and opening a device costs
    /// milliseconds.
    /// </para>
    /// <para>
    /// Semantics reproduced from the FMOD backend, all verified in its source rather than assumed:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>MAX_CHANNEL</c> = 100 addressable channels, <c>MAX_SOUND</c> = 2000 cache slots.</description></item>
    /// <item><description>Channel indices are <b>wrapped</b> (<c>channelIndex %= MAX_CHANNEL</c>), not rejected.</description></item>
    /// <item><description><c>volume</c> is linear, nominally 0..1, not clamped at the top.</description></item>
    /// <item><description><c>pan</c> is 0..127 with <b>64</b> as centre.</description></item>
    /// <item><description><c>offset</c> and <c>GetPosition</c> are both in PCM frames counted at the
    /// source file's sample rate (FMOD's <c>TIMEUNIT.PCM</c>), despite the legacy log line calling
    /// the offset "ms".</description></item>
    /// <item><description><c>PauseSound</c> and <c>PauseAllSounds</c> are <b>toggles</b>, not setters.</description></item>
    /// <item><description><c>StopAllSounds</c> also clears the group's pause flag, so stopping while
    /// paused does not leave future playback muted.</description></item>
    /// </list>
    /// </summary>
    public sealed class NAudioKeysoundPlayer : IAudioPlayer, IDisposable
    {
        /// <summary>Addressable channels. Same value as the FMOD backend so track indices keep mapping identically.</summary>
        public const int MAX_CHANNEL = 100;

        /// <summary>Cache slots, indexed by <c>InstrumentData.InsNum</c>. Same value as the FMOD backend.</summary>
        public const int MAX_SOUND = 2000;

        /// <summary>Mixer rate. Everything is resampled to this once, at load time.</summary>
        public const int MixerSampleRate = 48000;

        /// <summary>Mixer channel count.</summary>
        public const int MixerChannels = 2;

        private const long DefaultMaxCacheBytes = 512L * 1024L * 1024L;
        private const long DefaultMaxBytesPerSound = 192L * 1024L * 1024L;

        private readonly object _gate = new object();
        private readonly WaveFormat _format;
        private readonly MixingSampleProvider _mixer;
        private readonly PlaybackGroupSampleProvider _group;
        private readonly KeysoundSample[] _samples = new KeysoundSample[MAX_SOUND];
        private readonly KeysoundVoice[] _channels = new KeysoundVoice[MAX_CHANNEL];

        /// <summary>
        /// Voices that lost their channel to a retrigger but are still sounding, used only when
        /// <see cref="AllowOverlappingRetrigger"/> is on. They are no longer addressable per channel
        /// but still obey <see cref="StopAllSounds"/> and <see cref="PauseAllSounds"/> - exactly the
        /// position a legacy FMOD channel ended up in once its handle had been overwritten.
        /// </summary>
        private readonly List<KeysoundVoice> _ringingOut = new List<KeysoundVoice>();

        private readonly long _maxCacheBytes;
        private readonly long _maxBytesPerSound;

        private IAudioOutput _output;
        private bool _ownsOutput;
        private string _outputFallbackReason = string.Empty;

        private int _cachedSounds;
        private long _cachedBytes;
        private long _voiceStarts;
        private long _voicesEnded;
        private long _voicesStolen;
        private long _loadFailures;
        private long _playFailures;
        private string _lastLoadError;
        private bool _disposed;

        /// <summary>
        /// Set by <see cref="FadeOutAndSilence"/> and cleared when the teardown happens: the pause
        /// the transport asks for is a stop, and this is what remembers that between the flag going
        /// up on the UI thread and the fade finishing on the device thread.
        /// </summary>
        private bool _silenceWhenFadedOut;

        /// <summary>
        /// Whether the output device is currently stopped because a pause flushed its queue.
        /// <para>
        /// Volatile because it is read on the note-start path without taking <see cref="_gate"/>: the
        /// check has to be free when it is false, which is always except between a pause and the next
        /// play. Writes are made under the gate.
        /// </para>
        /// </summary>
        private volatile bool _deviceStopped;

        private long _deviceStops;
        private long _deviceStarts;

        /// <summary>
        /// Times the pause-as-stop teardown has actually run, and the voices it took off the mixer.
        /// <para>
        /// Cumulative and never reset, so what matters in a log is the delta across one pause edge:
        /// a pause that silenced a sounding chart moves both, a pause on top of silence moves the
        /// first only, and a pause that did nothing at all moves neither. That last case is the one
        /// three rounds of this bug could not distinguish from the other two.
        /// </para>
        /// </summary>
        private long _fadeTeardowns;
        private long _voicesDroppedOnFade;

        /// <summary>Opens the default device, falling back through waveOut to silence.</summary>
        public NAudioKeysoundPlayer()
            : this(new NAudioDeviceOutput(), true, DefaultMaxCacheBytes, DefaultMaxBytesPerSound)
        {
        }

        /// <param name="output">
        /// Where the mix goes. Pass a <see cref="NullAudioOutput"/> for no-device mode; if
        /// <paramref name="output"/> cannot open a device the player degrades to a silent output on
        /// its own rather than throwing, mirroring the FMOD backend's <c>OUTPUTTYPE.NOSOUND</c> path.
        /// </param>
        /// <param name="ownsOutput">When true the player disposes the output.</param>
        /// <param name="maxCacheBytes">Total decoded-PCM budget. Loads that would exceed it fail.</param>
        /// <param name="maxBytesPerSound">Per-sound decoded-PCM ceiling.</param>
        public NAudioKeysoundPlayer(IAudioOutput output, bool ownsOutput = true,
            long maxCacheBytes = DefaultMaxCacheBytes, long maxBytesPerSound = DefaultMaxBytesPerSound)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            _maxCacheBytes = Math.Max(1L * 1024L * 1024L, maxCacheBytes);
            _maxBytesPerSound = Math.Max(1L * 1024L * 1024L, Math.Min(maxBytesPerSound, _maxCacheBytes));

            _format = WaveFormat.CreateIeeeFloatWaveFormat(MixerSampleRate, MixerChannels);
            _mixer = new MixingSampleProvider(_format)
            {
                // Mandatory. Without it the mixer returns 0 whenever every voice has finished and
                // the device would treat that as end-of-stream and stop the graph for good.
                ReadFully = true
            };
            _mixer.MixerInputEnded += OnMixerInputEnded;
            _group = new PlaybackGroupSampleProvider(_mixer);
            _group.FadedOut = OnGroupFadedOut;

            StartOutput(output, ownsOutput);
        }

        /// <summary>
        /// Builds a player with no device at all, and hands back the pump used to drive it. This is
        /// the headless seam: tests advance the graph by an exact number of frames and inspect the
        /// samples that come out, with no driver and no wall-clock timing involved.
        /// </summary>
        public static NAudioKeysoundPlayer CreateWithoutDevice(out NullAudioOutput output)
        {
            output = new NullAudioOutput(false);
            return new NAudioKeysoundPlayer(output, true);
        }

        /// <summary>
        /// Optional sink for load diagnostics, so the shell can route them to its own log without
        /// this backend taking a dependency on one. Called off the device thread only.
        /// </summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// Whether a retrigger on a busy channel lets the previous voice ring out.
        ///
        /// <para>
        /// <b>Default true</b>, and measured rather than assumed. With it off, a second note on the
        /// same track hard-stops whatever the first one was still playing: the voice's next read
        /// returns zero samples, so the waveform is truncated wherever it happened to be and the
        /// amplitude at that instant <em>is</em> the size of the step - which is a click. The render
        /// probe counts them, and they are not rare. Over 40 s: <c>access_pop_1.pt</c> **106** hard
        /// stops, 13 of them above -40 dBFS; <c>@ner_duo_1.pt</c> **77**, of which **38** audible and
        /// the loudest a -14.8 dBFS step; <c>sonoflong_pop_3.pt</c> 8, 7 audible, loudest -16.7 dBFS.
        /// That is the same defect the owner reported as "the hold ones were cut of their like the
        /// very end sound", reached by a second route: a keysound losing its tail. The hold-rule half
        /// of it was fixed by asking for no hold length at all; this is the other half.
        /// </para>
        ///
        /// <para>
        /// It is also what the source material expects. The legacy backend called FMOD with
        /// <c>CHANNELINDEX.FREE</c> and the "stop the old channel" block in
        /// <c>AudioPlayerFmodEx.PlaySound</c> is commented out, so a retriggered sound kept playing on
        /// a channel nobody held a handle to any more - and the composer authored the samples for
        /// that, which is why an arp line whose sample outlasts its own note interval sounds thin
        /// without it. The repo test <c>Bms_AudioPauseFreezesEveryOverlappingLongSample</c> depends on
        /// precisely this behaviour.
        /// </para>
        ///
        /// <para>
        /// The cost, stated plainly: a detached voice is no longer addressable per channel, so
        /// <c>StopSound</c> / <c>SetVolume</c> / <c>GetPosition</c> address the sound the caller most
        /// recently started on that channel and not the one ringing out behind it. That is the right
        /// trade for a keysound player - the caller has just retriggered, so the new voice is the one
        /// it means - and the detached set is bounded at <c>MAX_CHANNEL</c> and reaped like any other.
        /// Set false to get one-voice-per-channel back; the render probe's last pass does exactly that
        /// so the report carries its own before-and-after.
        /// </para>
        /// </summary>
        public bool AllowOverlappingRetrigger { get; set; } = true;

        /// <summary>The output actually in use (may be a silent fallback).</summary>
        public IAudioOutput Output => _output;

        /// <summary>Mixer format, for callers that want to know what the graph runs at.</summary>
        public WaveFormat WaveFormat => _format;

        #region IAudioPlayer

        /// <summary>
        /// Decodes <paramref name="name"/> into cache slot <paramref name="index"/>, replacing
        /// whatever was there. Returns false - never throws - for a bad index, a missing file, an
        /// undecodable file or a full cache.
        /// <para>
        /// About <paramref name="mode"/>: the legacy backend used it to pick between FMOD's
        /// <c>CREATESAMPLE</c> (0, decode to memory) and <c>CREATESTREAM</c> (non-zero, stream from
        /// disk), and <c>MainForm</c> passes 1 for the first instrument of a chart - conventionally
        /// the long BGM track - and 0 for the rest. This backend always decodes to memory, so the
        /// argument is accepted and recorded but does not change behaviour. That is a deliberate
        /// trade: a streaming voice would have to run Vorbis on the device thread, which is the
        /// classic source of dropouts when a chart triggers thirty notes in a bar, whereas the memory
        /// cost is bounded and measurable (~23 MB per decoded minute, capped per sound and in total,
        /// both reported by <see cref="GetDebugInfo"/>).
        /// </para>
        /// </summary>
        public bool LoadSound(uint index, string name, int mode = 0)
        {
            if (index >= MAX_SOUND || _disposed)
            {
                RecordLoadFailure(name, index >= MAX_SOUND ? "index out of range" : "player disposed");
                return false;
            }

            // Decode outside the lock: a Vorbis decode is tens of milliseconds and the sequencer
            // thread must never block behind a chart load.
            if (!KeysoundDecoder.TryLoad(name, _format, mode, _maxBytesPerSound, out KeysoundSample sample, out string error))
            {
                RecordLoadFailure(name, error);
                return false;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                KeysoundSample previous = _samples[index];
                long projected = _cachedBytes - (previous?.ByteCount ?? 0L) + sample.ByteCount;
                if (projected > _maxCacheBytes)
                {
                    RecordLoadFailureLocked(name, "sample cache budget exhausted (" + projected + " > " + _maxCacheBytes + " bytes)");
                    return false;
                }

                if (previous != null)
                {
                    // FMOD released the old Sound here, which stopped any channel still playing it.
                    // Reproduced so a re-imported instrument cannot be heard from the old file.
                    StopVoicesOfSoundLocked(index);
                    _cachedBytes -= previous.ByteCount;
                    _cachedSounds--;
                }

                _samples[index] = sample;
                _cachedBytes += sample.ByteCount;
                _cachedSounds++;
            }

            return true;
        }

        /// <summary>
        /// Toggles the pause state of the voice on <paramref name="channelIndex"/> and reports
        /// whether there was one. A toggle, not a setter - that is what the FMOD backend did
        /// (<c>getPaused</c> then <c>setPaused(!paused)</c>) and callers rely on it.
        /// </summary>
        public bool PauseSound(uint channelIndex)
        {
            uint index = channelIndex % MAX_CHANNEL;

            lock (_gate)
            {
                KeysoundVoice voice = LiveVoiceLocked(index);
                if (voice == null)
                {
                    return false;
                }

                voice.TogglePause();
                return true;
            }
        }

        /// <summary>
        /// Stops the voice on <paramref name="channelIndex"/>, returning false when the channel was
        /// already idle (the legacy version returned false there too, because a finished FMOD channel
        /// handle no longer answered <c>isPlaying</c>).
        /// </summary>
        public bool StopSound(uint channelIndex)
        {
            uint index = channelIndex % MAX_CHANNEL;

            lock (_gate)
            {
                KeysoundVoice voice = _channels[index];
                if (voice == null)
                {
                    return false;
                }

                bool wasActive = voice.IsActive;
                voice.Stop();
                _mixer.RemoveMixerInput(voice);
                _channels[index] = null;
                UpdateVoiceHintLocked();
                return wasActive;
            }
        }

        /// <summary>
        /// Sets the linear volume of the voice on <paramref name="channelIndex"/>, keeping its pan.
        /// Returns false for an idle channel or a non-finite volume (FMOD answered
        /// <c>ERR_INVALID_PARAM</c> for the latter).
        /// </summary>
        public bool SetVolume(uint channelIndex, float volume)
        {
            if (float.IsNaN(volume) || float.IsInfinity(volume))
            {
                return false;
            }

            uint index = channelIndex % MAX_CHANNEL;

            lock (_gate)
            {
                KeysoundVoice voice = LiveVoiceLocked(index);
                if (voice == null)
                {
                    return false;
                }

                voice.SetVolume(volume);
                return true;
            }
        }

        /// <summary>
        /// Position of the voice on <paramref name="channelIndex"/> in PCM frames at the source
        /// file's sample rate, or 0 when the channel is idle.
        /// <para>
        /// The unit is not milliseconds. The legacy backend read
        /// <c>channel.getPosition(ref result, FMODEX.TIMEUNIT.PCM)</c>, and FMOD counts
        /// <c>TIMEUNIT.PCM</c> at the sound's own default frequency, so a 44.1 kHz keysound reports
        /// 44 100 per second even though this mixer runs at 48 kHz. <see cref="KeysoundSample"/>
        /// keeps the source rate precisely so that conversion stays exact here.
        /// </para>
        /// </summary>
        public uint GetPosition(uint channelIndex)
        {
            uint index = channelIndex % MAX_CHANNEL;

            lock (_gate)
            {
                KeysoundVoice voice = _channels[index];
                return voice?.SourcePcmPosition ?? 0u;
            }
        }

        /// <summary>
        /// Stops every voice, empties the mixer and clears the group pause.
        /// <para>
        /// Clearing the pause is not incidental: the legacy version called
        /// <c>m_playbackGroup.setPaused(false)</c> right after <c>stop()</c>, and a repo test asserts
        /// that stopping while paused does not leave later playback muted.
        /// </para>
        /// </summary>
        public void StopAllSounds()
        {
            // Every play edge comes through here, so this is where a device parked by
            // FlushDeviceTail comes back. Before the pause is cleared, not after: a running device
            // that finds the group still paused renders cleared buffers, which is what it did all
            // through the pause anyway.
            EnsureDeviceRunning();

            lock (_gate)
            {
                _silenceWhenFadedOut = false;
                SilenceVoicesLocked();
                _group.Paused = false;
            }
        }

        /// <summary>
        /// The transport's pause: fade the mix out over the group's ramp, then stop every voice.
        /// <para>
        /// A group pause on its own <i>freezes</i> - FMOD's behaviour, and what
        /// <see cref="SetAllPaused"/> still does. Frozen is silent while it lasts, and it was still
        /// reported as "the hold sound is there when I pause the timeline": a frozen hold is a live
        /// voice sitting mid-waveform, so anything that clears the group pause - a fresh play, a
        /// stop, the resume itself - steps straight back into the middle of that sample. Stopping
        /// outright leaves nothing to step into. Pair it with a resume that plays from the playhead
        /// and the pair behaves like the stop-then-play-from-here the owner asked for, which is also
        /// what the editor's own <c>RestoreSoundingVoices</c> was already built to make sound right.
        /// </para>
        /// <para>
        /// The teardown waits for <see cref="PlaybackGroupSampleProvider.FadedOut"/> instead of
        /// happening here, because here the mix is still at full level: dropping the voices on this
        /// call would be the very click the fade exists to remove. If the fade is already out - a
        /// second pause, or a graph nothing is pumping - there is nothing to wait for and the
        /// teardown is immediate.
        /// </para>
        /// </summary>
        public void FadeOutAndSilence()
        {
            lock (_gate)
            {
                _group.Paused = true;

                if (_group.FadeLevel <= 0f)
                {
                    _silenceWhenFadedOut = false;
                    RecordTeardownLocked(SilenceVoicesLocked());
                    return;
                }

                _silenceWhenFadedOut = true;
            }
        }

        /// <summary>
        /// Stops the device outright, discarding whatever the driver has already been handed, and
        /// parks it until the next play edge. Returns false when there is nothing to flush.
        /// <para>
        /// <b>Not on the pause path, and deliberately so.</b> This was round five's attempted fix, on
        /// the theory that the tail the owner kept reporting was the 60 ms the driver was already
        /// committed to at the instant of the keypress. That theory was wrong, and the loopback probe
        /// is what showed it: a 60 ms queue cannot produce a 400 ms tail, and the tail measured flat at
        /// -17 dBFS for as long as the pause lasted rather than draining. The cause was in the graph
        /// after all - a byte-wise clear of a float buffer, see <see cref="SampleBuffers"/> - and with
        /// that fixed a plain pause goes quiet 17.8 ms after Escape's stop does. Stopping the device
        /// buys nothing over that, and costs a step to zero at whatever level the mix was at plus a
        /// fresh render thread on the next start.
        /// </para>
        /// <para>
        /// It stays because <see cref="PauseTailProbe"/> uses it as a control edge: a measurement that
        /// discards the driver queue outright is the baseline the graph's own silence is compared
        /// against, and it is the evidence that the remaining 17.8 ms is the ramp rather than a
        /// residue. Two ordering rules, both load-bearing:
        /// </para>
        /// <list type="bullet">
        /// <item><description><see cref="IAudioOutput.Stop"/> is called with <see cref="_gate"/>
        /// released. It joins the device thread, and the device thread takes <see cref="_gate"/> in
        /// <see cref="OnGroupFadedOut"/>; calling it under the lock is a deadlock, not a
        /// theoretical one.</description></item>
        /// <item><description>A pending teardown is run here instead of being left to
        /// <see cref="PlaybackGroupSampleProvider.FadedOut"/>. With the device stopped nothing reads
        /// the graph again, so the announcement would never come and the pause would leave every
        /// voice parked in the mixer - the round-two defect, reintroduced by the fix for round
        /// five.</description></item>
        /// </list>
        /// </summary>
        public bool FlushDeviceTail()
        {
            IAudioOutput output;
            bool teardownPending;

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                output = _output;

                // Nothing to flush without a driver queue in the first place: the null output's
                // pump is pulled by the caller, so the offline path keeps the ramp and the deferred
                // teardown it is tested on.
                if (output == null || !output.IsRealDevice || _deviceStopped)
                {
                    return false;
                }

                teardownPending = _silenceWhenFadedOut;
                _silenceWhenFadedOut = false;
                _deviceStopped = true;
                _deviceStops++;
            }

            try
            {
                output.Stop();
            }
            catch (Exception ex)
            {
                // A device the OS has taken away throws here; the pause still has to complete.
                Log?.Invoke("Audio device flush failed: " + ex);
            }

            if (teardownPending)
            {
                lock (_gate)
                {
                    RecordTeardownLocked(SilenceVoicesLocked());
                }
            }

            return true;
        }

        /// <summary>
        /// Restarts a device parked by <see cref="FlushDeviceTail"/>. Free when it is not, which is
        /// every call but the first one after a pause.
        /// </summary>
        private void EnsureDeviceRunning()
        {
            if (!_deviceStopped)
            {
                return;
            }

            IAudioOutput output;
            lock (_gate)
            {
                if (_disposed || !_deviceStopped)
                {
                    return;
                }

                output = _output;
                _deviceStopped = false;
                _deviceStarts++;
            }

            try
            {
                output?.Play();
            }
            catch (Exception ex)
            {
                // Put the flag back so the next edge tries again rather than playing to a dead
                // device for the rest of the session.
                _deviceStopped = true;
                Log?.Invoke("Audio device restart failed: " + ex);
            }
        }

        /// <summary>
        /// Stops every voice and empties the mixer, leaving the group pause exactly as it is.
        /// <para>
        /// The pause-as-stop path needs the graph emptied <i>without</i> the group un-pausing under
        /// it, which is the one thing <see cref="StopAllSounds"/> is required to do.
        /// </para>
        /// </summary>
        /// <returns>
        /// How many voices were actually sounding when it ran - channels plus detached ring-outs. The
        /// teardown paths record it; a pause that reports zero here silenced nothing, which is a
        /// different fault from a pause that never ran.
        /// </returns>
        private int SilenceVoicesLocked()
        {
            int stopped = 0;

            for (int i = 0; i < _channels.Length; i++)
            {
                if (_channels[i] != null)
                {
                    stopped++;
                }

                _channels[i]?.Stop();
                _channels[i] = null;
            }

            stopped += _ringingOut.Count;

            for (int i = 0; i < _ringingOut.Count; i++)
            {
                _ringingOut[i].Stop();
            }

            _ringingOut.Clear();

            // Belt and braces: the voices would leave on their next read anyway, but while the
            // group is paused nothing reads, so drop them now and guarantee an empty graph.
            _mixer.RemoveAllMixerInputs();
            UpdateVoiceHintLocked();

            return stopped;
        }

        /// <summary>
        /// Runs on the device thread when the pause fade has reached zero. Inaudible by
        /// construction: the mix is already at silence and the mixer is not read again until the
        /// group is un-paused.
        /// </summary>
        private void OnGroupFadedOut()
        {
            lock (_gate)
            {
                if (!_silenceWhenFadedOut)
                {
                    return;
                }

                _silenceWhenFadedOut = false;
                RecordTeardownLocked(SilenceVoicesLocked());
            }
        }

        /// <summary>
        /// Books one pause-as-stop teardown. Called under <c>_gate</c> from both teardown sites - the
        /// deferred one on the device thread and the inline one for a pause on top of silence - so the
        /// counters cover the pause edge however it was served.
        /// </summary>
        private void RecordTeardownLocked(int voicesStopped)
        {
            _fadeTeardowns++;
            _voicesDroppedOnFade += voicesStopped;
        }

        /// <summary>
        /// Toggles the pause state of every sound at once, freezing positions rather than muting.
        /// A toggle, matching <c>ChannelGroup.getPaused</c> / <c>setPaused(!paused)</c>.
        /// <para>
        /// Prefer <see cref="SetAllPaused"/> from new code. This overload exists to match the FMOD
        /// call the legacy editor makes, and a toggle is a trap for a transport that has more than
        /// one way in and out of a paused state.
        /// </para>
        /// </summary>
        public void PauseAllSounds()
        {
            lock (_gate)
            {
                _group.Paused = !_group.Paused;
            }
        }

        /// <summary>
        /// Sets the group pause outright.
        /// <para>
        /// A transport reaches "paused" from Space, from a stop, and from a fresh play, and it
        /// leaves it the same three ways. Driving <see cref="PauseAllSounds"/> from all six edges
        /// gets the parity wrong the moment two of them disagree, and the audible result is the
        /// group un-pausing while the sequencer stays stopped: the background track and every
        /// voice that was in flight keep sounding with nothing feeding them new events.
        /// </para>
        /// <para>
        /// This is the FMOD-parity freeze and nothing more - positions hold, voices stay alive, and
        /// un-pausing carries on from mid-waveform. The transport wants
        /// <see cref="FadeOutAndSilence"/> instead; a pending teardown from that call is dropped
        /// here, because a caller reaching for the plain group flag is asking for the freeze.
        /// </para>
        /// </summary>
        public void SetAllPaused(bool paused)
        {
            lock (_gate)
            {
                _silenceWhenFadedOut = false;
                _group.Paused = paused;
            }
        }

        /// <summary>True while the group is paused.</summary>
        public bool IsAllPaused
        {
            get { lock (_gate) { return _group.Paused; } }
        }

        /// <summary>
        /// Starts <paramref name="soundIndex"/> on <paramref name="channelIndex"/> at linear
        /// <paramref name="volume"/> and pan <paramref name="pan"/> (0..127, 64 = centre), optionally
        /// seeking <paramref name="offset"/> PCM frames (source rate) into the sample.
        /// <para>
        /// Two divergences from the legacy backend, both deliberate:
        /// </para>
        /// <list type="number">
        /// <item><description>The seek is applied before the voice is ever audible. FMOD was told to
        /// unpause and only then to seek, which leaked a few milliseconds of the sample's beginning.</description></item>
        /// <item><description>A seek past the end of the sample starts the voice silent instead of
        /// playing from the beginning as FMOD's failed seek did - firing a note the chart has already
        /// scrolled past, at full volume, is the worse of the two behaviours.</description></item>
        /// </list>
        /// </summary>
        public bool PlaySound(uint channelIndex, uint soundIndex, float volume, byte pan, uint offset = 0)
        {
            // The legacy backend wrapped the channel index and then re-tested it against
            // MAX_CHANNEL, which can no longer fail. Only the wrap is behaviour, so only the wrap is
            // kept.
            uint index = channelIndex % MAX_CHANNEL;

            if (soundIndex >= MAX_SOUND)
            {
                Interlocked.Increment(ref _playFailures);
                return false;
            }

            // A single preview - clicking a note in the editor while the transport is stopped - does
            // not come through StopAllSounds, so this is the other place a device parked by
            // FlushDeviceTail has to come back. One volatile read when it is not parked.
            EnsureDeviceRunning();

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                KeysoundSample sample = _samples[soundIndex];
                if (sample == null)
                {
                    // Charts routinely reference instruments whose file never shipped; this is the
                    // hot path for that and it must stay cheap and quiet.
                    _playFailures++;
                    return false;
                }

                ReleaseChannelLocked(index);

                int startFrame = 0;
                if (offset != 0)
                {
                    long frame = (long)offset * sample.SampleRate / Math.Max(1, sample.SourceSampleRate);
                    startFrame = frame > int.MaxValue ? int.MaxValue : (int)frame;
                }

                AttachVoiceLocked(index, soundIndex, sample, volume, pan, startFrame, 0);
                return true;
            }
        }

        /// <summary>
        /// Starts a charted note: like <see cref="PlaySound"/>, but in milliseconds and with the
        /// note's own length, so the voice is released when the note ends instead of playing its
        /// whole file.
        /// <para>
        /// Separate from <see cref="PlaySound"/> rather than another optional parameter on it,
        /// because the two speak different languages. <see cref="PlaySound"/> is the
        /// <see cref="IAudioPlayer"/> contract the legacy WinForms shell calls, and its offset is in
        /// PCM frames at the <i>source file's</i> rate, which is FMOD's unit and useless to a
        /// sequencer that knows only ticks and tempo. A note has a start time and a length in
        /// milliseconds; converting once, here, keeps sample rates out of the shell.
        /// </para>
        /// <para>
        /// <paramref name="holdMilliseconds"/> of 0 means "no release" - the voice rings out to the
        /// end of its sample exactly as before. Taps are passed 0 deliberately: a struck sound
        /// decaying naturally is what the arcade does, and clipping it at the note's tick would make
        /// every hi-hat a gated blip.
        /// </para>
        /// </summary>
        internal bool PlayNote(uint channelIndex, uint soundIndex, float volume, byte pan,
            double startMilliseconds, double holdMilliseconds)
        {
            uint index = channelIndex % MAX_CHANNEL;

            if (soundIndex >= MAX_SOUND)
            {
                Interlocked.Increment(ref _playFailures);
                return false;
            }

            // See PlaySound: the sequencer's first note after a resume may land before or after the
            // transport's own StopAllSounds, so both entry points restart the device.
            EnsureDeviceRunning();

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                KeysoundSample sample = _samples[soundIndex];
                if (sample == null)
                {
                    _playFailures++;
                    return false;
                }

                ReleaseChannelLocked(index);

                // Mixer-rate frames: the cached sample has already been resampled into the mixer's
                // format, so the voice's cursor advances at _format.SampleRate and both numbers must
                // be in that unit. Using the source rate here is the classic bug - it silently
                // shortens or lengthens every hold on any sample that was not already 48 kHz.
                int startFrame = MillisecondsToFrames(startMilliseconds);
                int holdFrames = MillisecondsToFrames(holdMilliseconds);

                AttachVoiceLocked(index, soundIndex, sample, volume, pan, startFrame, holdFrames);
                return true;
            }
        }

        private int MillisecondsToFrames(double milliseconds)
        {
            if (milliseconds <= 0.0)
            {
                return 0;
            }

            double frames = milliseconds * _format.SampleRate / 1000.0;
            return frames >= int.MaxValue ? int.MaxValue : (int)frames;
        }

        /// <summary>
        /// The tail every start shares: build the voice, own the channel, join the mixer, count it.
        /// Caller must hold <c>_gate</c> and must already have freed the channel.
        /// </summary>
        private void AttachVoiceLocked(uint index, uint soundIndex, KeysoundSample sample,
            float volume, byte pan, int startFrame, int holdFrames)
        {
            var voice = new KeysoundVoice(
                sample, _format, index, soundIndex, volume, pan, startFrame, holdFrames);
            _channels[index] = voice;
            _mixer.AddMixerInput(voice);
            _voiceStarts++;
            UpdateVoiceHintLocked();
        }

        /// <summary>
        /// A <see cref="KeysoundPlayerDebugInfo"/> snapshot for the diagnostics overlay. Also the
        /// point where finished voices are reconciled, so the numbers it reports are exact.
        /// </summary>
        public object GetDebugInfo()
        {
            lock (_gate)
            {
                ReapFinishedVoicesLocked();

                int active = 0;
                int busy = 0;
                for (int i = 0; i < _channels.Length; i++)
                {
                    if (_channels[i] != null)
                    {
                        busy++;
                        active++;
                    }
                }

                active += _ringingOut.Count;

                return new KeysoundPlayerDebugInfo
                {
                    Backend = "NAudioKeysoundPlayer",
                    Output = _output?.Name ?? "none",
                    IsRealDevice = _output?.IsRealDevice ?? false,
                    SampleRate = _format.SampleRate,
                    Channels = _format.Channels,
                    LatencyMs = _output?.LatencyMilliseconds ?? 0,
                    ActiveVoices = active,
                    BusyChannels = busy,
                    MixerInputs = active,
                    CachedSounds = _cachedSounds,
                    CachedBytes = _cachedBytes,
                    CacheLimitBytes = _maxCacheBytes,
                    GlobalPaused = _group.Paused,
                    VoiceStarts = _voiceStarts,
                    VoicesEnded = Interlocked.Read(ref _voicesEnded),
                    VoicesStolen = _voicesStolen,
                    LoadFailures = Interlocked.Read(ref _loadFailures),
                    PlayFailures = _playFailures,
                    Underruns = _group.Underruns,
                    BuffersRendered = _group.BuffersRendered,
                    OutputFallbackReason = _outputFallbackReason,
                    LastLoadError = _lastLoadError
                };
            }
        }

        #endregion // IAudioPlayer

        /// <summary>
        /// Number of inputs the mixer really holds, by walking its live list. Diagnostics and tests
        /// only: safe when the graph is pumped from the calling thread (no-device mode), not while a
        /// device thread is mixing.
        /// </summary>
        internal int SnapshotMixerInputCount()
        {
            lock (_gate)
            {
                int count = 0;
                foreach (ISampleProvider input in _mixer.MixerInputs)
                {
                    count++;
                }

                return count;
            }
        }

        /// <summary>True when the given channel currently holds a live voice.</summary>
        internal bool IsChannelBusy(uint channelIndex)
        {
            lock (_gate)
            {
                return LiveVoiceLocked(channelIndex % MAX_CHANNEL) != null;
            }
        }

        /// <summary>
        /// The decoded sample in a cache slot, or null when the slot is empty. For diagnostics that
        /// need the PCM itself - measuring where a voice was truncated, for instance - rather than
        /// only what the mixer produced.
        /// </summary>
        internal KeysoundSample SampleAt(uint soundIndex)
        {
            if (soundIndex >= MAX_SOUND)
            {
                return null;
            }

            lock (_gate)
            {
                return _samples[soundIndex];
            }
        }

        /// <summary>
        /// How long the sample in a cache slot runs, in milliseconds, or 0 when the slot is empty.
        ///
        /// <para>
        /// Exists because "is this keysound still sounding?" is the only honest way to decide what a
        /// seek has to put back on the air now that nothing truncates a voice. The chart cannot answer
        /// it: a TECHNIKA note's duration is its keysound's length only approximately, and the MR is
        /// one note with a duration of six ticks carrying a hundred seconds of audio. The decoded
        /// sample knows exactly, and it is already in memory.
        /// </para>
        ///
        /// <para>
        /// Measured at the mixer rate, which is what <see cref="PlayNote"/>'s milliseconds are in -
        /// deliberately not the source rate, so a 44.1 kHz keysound does not come back 9% long.
        /// </para>
        /// </summary>
        internal double SampleLengthMilliseconds(uint soundIndex)
        {
            if (soundIndex >= MAX_SOUND)
            {
                return 0.0;
            }

            lock (_gate)
            {
                KeysoundSample sample = _samples[soundIndex];
                if (sample == null || sample.SampleRate <= 0)
                {
                    return 0.0;
                }

                return sample.FrameCount * 1000.0 / sample.SampleRate;
            }
        }

        /// <summary>
        /// Notes the player was asked to start and could not, since startup.
        ///
        /// <para>
        /// Not a curiosity: a chart's keysounds decode on a background thread and a big TECHNIKA set
        /// takes seconds, so every note struck before its sample arrives fails here. That used to be
        /// invisible - the counter was only reachable through the debug overlay - which made "the
        /// other tracks do not even play" impossible to tell apart from a scheduling fault. The
        /// transport log now prints it on every edge.
        /// </para>
        /// </summary>
        internal long PlayFailures => Interlocked.Read(ref _playFailures);

        /// <summary>Voices that lost their channel to a retrigger and are still sounding.</summary>
        internal int RingingOutVoiceCount
        {
            get
            {
                lock (_gate)
                {
                    ReapFinishedVoicesLocked();
                    return _ringingOut.Count;
                }
            }
        }

        /// <summary>Buffers handed to the device since startup. See the group provider's counters.</summary>
        internal long BuffersRendered => _group.BuffersRendered;

        /// <summary>Buffers cleared because the group was paused.</summary>
        internal long PausedBuffers => _group.PausedBuffers;

        /// <summary>See <see cref="PlaybackGroupSampleProvider.Tracing"/> - diagnostics only.</summary>
        internal bool GraphTracing
        {
            get => _group.Tracing;
            set => _group.Tracing = value;
        }

        /// <summary>See <see cref="PlaybackGroupSampleProvider.LastRead"/>.</summary>
        internal string LastGraphRead => _group.LastRead;

        /// <summary>See <see cref="PlaybackGroupSampleProvider.RecentReads"/>.</summary>
        internal string RecentGraphReads => _group.RecentReads;

        /// <summary>
        /// Frames rendered while the pause fade was moving. Non-zero across a pause is what separates
        /// "faded to zero" from "cut to zero" in a log, and the cut is a click on every held note.
        /// </summary>
        internal long PauseFadeFrames => _group.FadedFrames;

        /// <summary>
        /// Times the group announced that the pause ramp had reached zero. See
        /// <see cref="PlaybackGroupSampleProvider.FadeOutsAnnounced"/> for why this is logged as a
        /// fact rather than inferred from the mixer input count at the next transport edge.
        /// </summary>
        internal long FadeOutsAnnounced => _group.FadeOutsAnnounced;

        /// <summary>Times the pause-as-stop teardown ran.</summary>
        internal long FadeTeardowns
        {
            get { lock (_gate) { return _fadeTeardowns; } }
        }

        /// <summary>Voices those teardowns took off the mixer, in total.</summary>
        internal long VoicesDroppedOnFade
        {
            get { lock (_gate) { return _voicesDroppedOnFade; } }
        }

        /// <summary>
        /// Times <see cref="FlushDeviceTail"/> actually stopped the device, and times a play edge
        /// started it again.
        /// <para>
        /// Logged on every transport edge because the flush is the one part of the pause that leaves
        /// no trace in the graph's own counters - it works by making sure the graph is <i>not</i>
        /// read - and because the two must stay within one of each other. Stops running ahead of
        /// starts by more than one means a play edge did not restart the device and the editor has
        /// gone silent, which is a far worse failure than the tail this fixes and needs to be
        /// visible in the log rather than reported by ear.
        /// </para>
        /// </summary>
        internal long DeviceStops
        {
            get { lock (_gate) { return _deviceStops; } }
        }

        /// <inheritdoc cref="DeviceStops"/>
        internal long DeviceStarts
        {
            get { lock (_gate) { return _deviceStarts; } }
        }

        /// <summary>True while the device is parked by a pause flush.</summary>
        internal bool DeviceStopped => _deviceStopped;

        /// <summary>
        /// Linear master attenuation on the whole mix. Defaults to
        /// <see cref="PlaybackGroupSampleProvider.DefaultMasterGain"/>, which is the headroom a chart
        /// playing several full-scale keysounds at once needs; see that class for the measurement.
        /// </summary>
        internal float MasterGain
        {
            get => _group.MasterGain;
            set => _group.MasterGain = value;
        }

        /// <summary>Whether the output peak limiter runs. Diagnostics only - always on in the shell.</summary>
        internal bool LimiterEnabled
        {
            get => _group.LimiterEnabled;
            set => _group.LimiterEnabled = value;
        }

        /// <summary>Frames the limiter pulled down since the last <see cref="ResetMeters"/>.</summary>
        internal long LimitedFrames => _group.LimitedFrames;

        /// <summary>Loudest sample that has left the graph since the last <see cref="ResetMeters"/>.</summary>
        internal float PeakOut => _group.PeakOut;

        /// <summary>Clears the output meters so one measurement cannot inherit another's peak.</summary>
        internal void ResetMeters()
        {
            _group.ResetMeters();
        }

        private void StartOutput(IAudioOutput output, bool ownsOutput)
        {
            try
            {
                output.Init(_group);
                output.Play();
                _output = output;
                _ownsOutput = ownsOutput;
                return;
            }
            catch (Exception ex)
            {
                _outputFallbackReason = ex.Message;
                Log?.Invoke("Audio output unavailable, falling back to silence: " + ex);
                if (ownsOutput)
                {
                    try
                    {
                        output.Dispose();
                    }
                    catch (Exception)
                    {
                        // Already failing; nothing to add.
                    }
                }
            }

            // No device: keep running silently, exactly as the FMOD backend switched itself to
            // OUTPUTTYPE.NOSOUND when the machine reported zero drivers. The pump keeps voice
            // positions and voice retirement behaving as they would with a device.
            var silent = new NullAudioOutput(true);
            silent.Init(_group);
            silent.Play();
            _output = silent;
            _ownsOutput = true;
        }

        private void OnMixerInputEnded(object sender, SampleProviderEventArgs e)
        {
            // Raised by the mixer, on the device thread, while it holds its own input lock. It must
            // therefore never touch _gate: a thread that holds _gate can be inside AddMixerInput
            // waiting for that same lock, and taking them in both orders is a deadlock. An
            // interlocked counter is all this needs; slot reconciliation happens lazily in
            // ReapFinishedVoicesLocked.
            Interlocked.Increment(ref _voicesEnded);
        }

        /// <summary>Returns the voice on a channel if it is still alive, clearing a dead slot.</summary>
        private KeysoundVoice LiveVoiceLocked(uint index)
        {
            KeysoundVoice voice = _channels[index];
            if (voice == null)
            {
                return null;
            }

            if (!voice.IsActive)
            {
                _mixer.RemoveMixerInput(voice);
                _channels[index] = null;
                UpdateVoiceHintLocked();
                return null;
            }

            return voice;
        }

        /// <summary>Frees a channel for a new voice, honouring <see cref="AllowOverlappingRetrigger"/>.</summary>
        private void ReleaseChannelLocked(uint index)
        {
            KeysoundVoice existing = _channels[index];
            if (existing == null)
            {
                return;
            }

            _channels[index] = null;

            if (!existing.IsActive)
            {
                _mixer.RemoveMixerInput(existing);
                return;
            }

            _voicesStolen++;

            if (!AllowOverlappingRetrigger)
            {
                existing.Stop();
                // Removed synchronously rather than waiting for the mixer to notice the 0-length
                // read: this is the path a fast retrigger takes hundreds of times a minute, and it
                // is where an NAudio mixer classically leaks inputs.
                _mixer.RemoveMixerInput(existing);
                return;
            }

            _ringingOut.Add(existing);

            // Bound the detached set. FMOD itself was initialised with 32 real channels, so the
            // legacy backend silently stole voices well before this point; capping at MAX_CHANNEL is
            // strictly more generous while still keeping the mixer's input list finite.
            while (_ringingOut.Count > MAX_CHANNEL)
            {
                KeysoundVoice oldest = _ringingOut[0];
                _ringingOut.RemoveAt(0);
                oldest.Stop();
                _mixer.RemoveMixerInput(oldest);
            }
        }

        /// <summary>
        /// Drops voices that have played out or been stopped from both the channel table and the
        /// mixer. The mixer discards them by itself as soon as a read comes up short, but doing it
        /// explicitly keeps the two views identical even when nothing is being read - while the group
        /// is paused, for instance - and makes the reported input count exact.
        /// </summary>
        private void ReapFinishedVoicesLocked()
        {
            for (int i = 0; i < _channels.Length; i++)
            {
                KeysoundVoice voice = _channels[i];
                if (voice != null && !voice.IsActive)
                {
                    _mixer.RemoveMixerInput(voice);
                    _channels[i] = null;
                }
            }

            for (int i = _ringingOut.Count - 1; i >= 0; i--)
            {
                KeysoundVoice voice = _ringingOut[i];
                if (!voice.IsActive)
                {
                    _mixer.RemoveMixerInput(voice);
                    _ringingOut.RemoveAt(i);
                }
            }

            UpdateVoiceHintLocked();
        }

        private void StopVoicesOfSoundLocked(uint soundIndex)
        {
            for (int i = 0; i < _channels.Length; i++)
            {
                KeysoundVoice voice = _channels[i];
                if (voice != null && voice.SoundIndex == soundIndex)
                {
                    voice.Stop();
                    _mixer.RemoveMixerInput(voice);
                    _channels[i] = null;
                }
            }

            for (int i = _ringingOut.Count - 1; i >= 0; i--)
            {
                KeysoundVoice voice = _ringingOut[i];
                if (voice.SoundIndex == soundIndex)
                {
                    voice.Stop();
                    _mixer.RemoveMixerInput(voice);
                    _ringingOut.RemoveAt(i);
                }
            }

            UpdateVoiceHintLocked();
        }

        private void UpdateVoiceHintLocked()
        {
            int count = _ringingOut.Count;
            for (int i = 0; i < _channels.Length; i++)
            {
                if (_channels[i] != null)
                {
                    count++;
                }
            }

            _group.ActiveVoiceHint = count;
        }

        private void RecordLoadFailure(string name, string error)
        {
            Interlocked.Increment(ref _loadFailures);
            string message = (name ?? "<null>") + ": " + (error ?? "unknown error");
            _lastLoadError = message;
            Log?.Invoke("LoadSound failed - " + message);
        }

        private void RecordLoadFailureLocked(string name, string error)
        {
            _loadFailures++;
            _lastLoadError = (name ?? "<null>") + ": " + (error ?? "unknown error");
            Log?.Invoke("LoadSound failed - " + _lastLoadError);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            IAudioOutput output;
            bool owns;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                for (int i = 0; i < _channels.Length; i++)
                {
                    _channels[i]?.Stop();
                    _channels[i] = null;
                }

                for (int i = 0; i < _ringingOut.Count; i++)
                {
                    _ringingOut[i].Stop();
                }

                _ringingOut.Clear();
                _mixer.RemoveAllMixerInputs();
                _mixer.MixerInputEnded -= OnMixerInputEnded;

                Array.Clear(_samples, 0, _samples.Length);
                _cachedBytes = 0;
                _cachedSounds = 0;

                output = _output;
                owns = _ownsOutput;
                _output = null;
            }

            // Outside the lock: stopping a device blocks until its callback thread has drained, and
            // that thread can be inside the mixer.
            if (output != null)
            {
                try
                {
                    output.Stop();
                }
                catch (Exception)
                {
                    // A device the OS has already reclaimed throws here; shutdown continues.
                }

                if (owns)
                {
                    try
                    {
                        output.Dispose();
                    }
                    catch (Exception)
                    {
                        // Same reasoning.
                    }
                }
            }
        }
    }
}
