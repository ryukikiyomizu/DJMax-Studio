using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// The real device output: one shared endpoint for the whole application.
    /// <para>
    /// Tier order, decided at <see cref="Init"/> time because that is where NAudio actually opens
    /// the driver and therefore where failures surface:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// WASAPI in <b>shared</b> mode with event-driven callbacks. Shared (not exclusive) is
    /// deliberate - an editor must never seize the endpoint and mute the rest of the desktop, and
    /// the user is usually watching a reference video in a browser while charting.
    /// </description></item>
    /// <item><description>waveOut with a float format - for legacy or unusual drivers.</description></item>
    /// <item><description>waveOut converted to 16-bit - for drivers that reject IEEE float.</description></item>
    /// </list>
    /// <para>
    /// If every tier fails, <see cref="Init"/> throws and <see cref="NAudioKeysoundPlayer"/>
    /// swaps in a <see cref="NullAudioOutput"/> so the editor still opens charts silently.
    /// </para>
    /// <para>
    /// A preferred endpoint may be requested by id. If it is missing at startup, or disappears while
    /// the editor is running (the classic unplugged headphones case), the output falls back to the
    /// system default render endpoint instead of staying stranded on a dead device.
    /// </para>
    /// </summary>
    public sealed class NAudioDeviceOutput : IAudioOutput
    {
        private const int AvailabilityProbeMilliseconds = 750;

        private readonly int _requestedLatencyMs;
        private readonly string _preferredDeviceId;
        private readonly object _gate = new object();

        private IWavePlayer _device;
        private string _name = "not initialised";
        private bool _disposed;
        private bool _playing;
        private string _currentEndpointId = string.Empty;
        private long _lastAvailabilityProbeTicks;
        private Timer _availabilityTimer;

        /// <param name="latencyMilliseconds">
        /// Buffer size to ask the driver for. 60 ms is the default: keysound triggers are already
        /// quantised to the sequencer's tick, so a few extra milliseconds of buffer are inaudible,
        /// while a too-small buffer glitches badly the moment a chart loads several hundred
        /// samples on the UI thread.
        /// </param>
        /// <param name="preferredDeviceId">
        /// Audio endpoint id to try first, or null/empty for the system default. If this endpoint is
        /// unavailable the output falls back to the default render device.
        /// </param>
        public NAudioDeviceOutput(int latencyMilliseconds = 60, string preferredDeviceId = null)
        {
            _requestedLatencyMs = Math.Max(10, latencyMilliseconds);
            _preferredDeviceId = string.IsNullOrWhiteSpace(preferredDeviceId)
                ? string.Empty
                : preferredDeviceId.Trim();
        }

        /// <inheritdoc/>
        public string Name => _name;

        /// <inheritdoc/>
        public int LatencyMilliseconds => _requestedLatencyMs;

        /// <inheritdoc/>
        public bool IsRealDevice => true;

        /// <summary>
        /// Which tier ended up being used, plus the errors from the tiers that were skipped.
        /// Surfaced through the diagnostics overlay so "why is there no sound" is answerable
        /// without a debugger.
        /// </summary>
        public string SelectionLog { get; private set; } = string.Empty;

        /// <summary>
        /// Optional diagnostics sink used by the owning player. Recovery failures are logged here
        /// because the editor can continue running silently after the OS takes a device away.
        /// </summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// What crossed into NAudio, once <see cref="Init"/> has picked a tier. Null before that.
        /// See <see cref="DeviceHandoffMeter"/> for why the boundary is worth metering.
        /// </summary>
        internal DeviceHandoffMeter Handoff { get; private set; }

        /// <summary>The same, one stage earlier and in floats. See <see cref="GraphTapSampleProvider"/>.</summary>
        internal GraphTapSampleProvider Tap { get; private set; }

        /// <summary>
        /// One selectable render endpoint.
        /// </summary>
        public sealed class OutputDeviceInfo
        {
            public OutputDeviceInfo(string id, string name, bool isDefault)
            {
                Id = id ?? string.Empty;
                Name = string.IsNullOrWhiteSpace(name) ? "(unnamed device)" : name;
                IsDefault = isDefault;
            }

            public string Id { get; }

            public string Name { get; }

            public bool IsDefault { get; }
        }

        /// <summary>
        /// Active render endpoints the user can choose from.
        /// </summary>
        public static IReadOnlyList<OutputDeviceInfo> EnumerateRenderDevices()
        {
            var devices = new List<OutputDeviceInfo>();
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    string defaultId = string.Empty;
                    try
                    {
                        MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        defaultId = defaultDevice == null ? string.Empty : defaultDevice.ID;
                    }
                    catch (Exception)
                    {
                        defaultId = string.Empty;
                    }

                    foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        if (device == null)
                        {
                            continue;
                        }

                        devices.Add(new OutputDeviceInfo(
                            device.ID,
                            device.FriendlyName,
                            string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
            catch (Exception)
            {
                // A preferences list that cannot be populated is still a usable preferences window.
            }

            return devices;
        }

        /// <inheritdoc/>
        public void Init(ISampleProvider source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NAudioDeviceOutput));
            }

            var log = new StringBuilder();

            // Both meters are inert unless something reads them; they exist because the pause tail
            // could not be attributed without knowing which side of NAudio it comes from.
            var tap = new GraphTapSampleProvider(source);
            Tap = tap;

            if (TryInitPreferredOrDefault(log, false))
            {
                return;
            }

            SelectionLog = log.ToString();
            throw new InvalidOperationException("No usable audio output device. " + SelectionLog);
        }

        /// <inheritdoc/>
        public void Play()
        {
            IWavePlayer device;
            lock (_gate)
            {
                _playing = true;
                device = _device;
                EnsureAvailabilityTimerLocked();
            }

            try
            {
                device?.Play();
            }
            catch (Exception)
            {
                EnsureAvailable();

                IWavePlayer recovered;
                lock (_gate)
                {
                    recovered = _device;
                }

                try
                {
                    recovered?.Play();
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Audio output start failed: " + ex.Message);
                }
            }
        }

        /// <inheritdoc/>
        public void EnsureAvailable()
        {
            if (_disposed)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastAvailabilityProbeTicks) < AvailabilityProbeMilliseconds)
            {
                return;
            }
            Interlocked.Exchange(ref _lastAvailabilityProbeTicks, now);

            string endpointId;
            lock (_gate)
            {
                endpointId = _currentEndpointId;
            }

            if (string.IsNullOrEmpty(endpointId) || IsEndpointActive(endpointId))
            {
                return;
            }

            var log = new StringBuilder();
            log.Append("Current endpoint vanished; ");
            if (!TryInitPreferredOrDefault(log, true))
            {
                SelectionLog = log.ToString();
                Log?.Invoke("Audio output recovery failed: " + SelectionLog);
            }
        }

        /// <inheritdoc/>
        public void Stop()
        {
            IWavePlayer device;
            lock (_gate)
            {
                _playing = false;
                DisposeAvailabilityTimerLocked();
                device = _device;
            }

            try
            {
                device?.Stop();
            }
            catch (Exception)
            {
                // Stopping a device that the OS already took away throws; shutdown continues.
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            try
            {
                _device?.Dispose();
            }
            catch (Exception)
            {
                // Same reasoning as Stop.
            }

            _device = null;
        }

        private bool TryInitPreferredOrDefault(StringBuilder log, bool ignorePreferred)
        {
            OutputDeviceInfo preferred = ignorePreferred ? null : FindPreferredActiveDevice();
            if (!ignorePreferred && !string.IsNullOrEmpty(_preferredDeviceId) && preferred == null)
            {
                log.Append("preferred endpoint unavailable, using system default; ");
            }

            OutputDeviceInfo target = preferred ?? DefaultActiveDevice();
            if (target != null && TryInitWasapi(log, target, preferred != null))
            {
                return true;
            }

            if (TryInitWaveOut(log, "waveOut float"))
            {
                return true;
            }

            if (TryInitWaveOut16(log, "waveOut 16-bit"))
            {
                return true;
            }

            return false;
        }

        private bool TryInitWasapi(StringBuilder log, OutputDeviceInfo target, bool preferred)
        {
            string successName = preferred
                ? "WASAPI shared - " + target.Name
                : "WASAPI shared - " + target.Name + " (system default)";

            return TryInit(
                log,
                successName,
                target.Id,
                () =>
                {
                    var enumerator = new MMDeviceEnumerator();
                    MMDevice endpoint = enumerator.GetDevice(target.Id);
                    return new WasapiOut(endpoint, AudioClientShareMode.Shared, true, _requestedLatencyMs);
                },
                () => new SampleToWaveProvider(Tap));
        }

        private bool TryInitWaveOut(StringBuilder log, string tier)
        {
            return TryInit(
                log,
                tier + " - system default",
                string.Empty,
                () => new WaveOutEvent { DesiredLatency = _requestedLatencyMs, NumberOfBuffers = 3 },
                () => new SampleToWaveProvider(Tap));
        }

        private bool TryInitWaveOut16(StringBuilder log, string tier)
        {
            return TryInit(
                log,
                tier + " - system default",
                string.Empty,
                () => new WaveOutEvent { DesiredLatency = _requestedLatencyMs, NumberOfBuffers = 3 },
                () => new SampleToWaveProvider16(Tap));
        }

        private bool TryInit(
            StringBuilder log,
            string successName,
            string endpointId,
            Func<IWavePlayer> createDevice,
            Func<IWaveProvider> createProvider)
        {
            IWavePlayer device = null;
            try
            {
                device = createDevice();
                var handoff = new DeviceHandoffMeter(createProvider());
                device.Init(handoff);

                IWavePlayer previous;
                bool shouldPlay;
                lock (_gate)
                {
                    previous = _device;
                    _device = device;
                    _name = successName;
                    Handoff = handoff;
                    _currentEndpointId = endpointId ?? string.Empty;
                    shouldPlay = _playing;
                }

                if (previous != null && !ReferenceEquals(previous, device))
                {
                    try
                    {
                        previous.Stop();
                    }
                    catch (Exception)
                    {
                    }
                    try
                    {
                        previous.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }

                if (shouldPlay)
                {
                    device.Play();
                }

                log.Append(successName).Append(": selected");
                SelectionLog = log.ToString();
                return true;
            }
            catch (Exception ex)
            {
                log.Append(successName).Append(": ").Append(ex.GetType().Name).Append(" - ").Append(ex.Message).Append("; ");
                if (device != null)
                {
                    try
                    {
                        device.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }

                return false;
            }
        }

        private OutputDeviceInfo FindPreferredActiveDevice()
        {
            if (string.IsNullOrEmpty(_preferredDeviceId))
            {
                return null;
            }

            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    MMDevice device = enumerator.GetDevice(_preferredDeviceId);
                    if (device == null || device.State != DeviceState.Active)
                    {
                        return null;
                    }

                    string defaultId = string.Empty;
                    try
                    {
                        MMDevice def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        defaultId = def == null ? string.Empty : def.ID;
                    }
                    catch (Exception)
                    {
                        defaultId = string.Empty;
                    }

                    return new OutputDeviceInfo(
                        device.ID,
                        device.FriendlyName,
                        string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static OutputDeviceInfo DefaultActiveDevice()
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (device == null || device.State != DeviceState.Active)
                    {
                        return null;
                    }

                    return new OutputDeviceInfo(device.ID, device.FriendlyName, true);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsEndpointActive(string endpointId)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                return true;
            }

            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    MMDevice device = enumerator.GetDevice(endpointId);
                    return device != null && device.State == DeviceState.Active;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void EnsureAvailabilityTimerLocked()
        {
            if (_availabilityTimer != null)
            {
                return;
            }

            _availabilityTimer = new Timer(
                _ => EnsureAvailable(),
                null,
                AvailabilityProbeMilliseconds,
                AvailabilityProbeMilliseconds);
        }

        private void DisposeAvailabilityTimerLocked()
        {
            Timer timer = _availabilityTimer;
            _availabilityTimer = null;
            if (timer != null)
            {
                try
                {
                    timer.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
