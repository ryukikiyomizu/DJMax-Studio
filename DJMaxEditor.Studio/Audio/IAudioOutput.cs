using System;
using NAudio.Wave;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// The seam between the keysound mixer graph and an actual audio device.
    /// <para>
    /// The whole point of this abstraction is testability: <see cref="NAudioKeysoundPlayer"/>
    /// builds one mixer graph and hands it to an <see cref="IAudioOutput"/>, so the voice
    /// bookkeeping (allocation, retrigger, removal of finished voices from the mixer) can be
    /// exercised headlessly through <see cref="NullAudioOutput"/> - no driver, no device, no
    /// real-time timing. The legacy FMOD Ex backend could not be tested at all without a
    /// machine that had a working output endpoint and the 32-bit fmodex.dll next to the exe.
    /// </para>
    /// </summary>
    public interface IAudioOutput : IDisposable
    {
        /// <summary>Human readable name of the concrete output, for the diagnostics overlay.</summary>
        string Name { get; }

        /// <summary>
        /// Output latency in milliseconds. This is the buffer size the backend was asked for -
        /// neither WASAPI shared mode nor waveOut reports the driver's real end-to-end latency
        /// through NAudio, so treat it as the nominal figure.
        /// </summary>
        int LatencyMilliseconds { get; }

        /// <summary>False for <see cref="NullAudioOutput"/>; true when a driver is really open.</summary>
        bool IsRealDevice { get; }

        /// <summary>
        /// Binds the mixer graph to the device. Implementations are expected to throw when no
        /// usable device exists - the player catches that and degrades to a silent output.
        /// </summary>
        void Init(ISampleProvider source);

        /// <summary>Starts pulling from the source bound by <see cref="Init"/>.</summary>
        void Play();

        /// <summary>Stops pulling. Must be safe to call when never started.</summary>
        void Stop();
    }
}
