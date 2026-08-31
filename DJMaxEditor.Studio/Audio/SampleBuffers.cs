using System;
using NAudio.Wave;

namespace DJMaxEditor.Studio.Audio
{
    /// <summary>
    /// The one safe way to zero part of a buffer that arrived through
    /// <see cref="ISampleProvider.Read"/>.
    /// <para>
    /// <see cref="Array.Clear(Array, int, int)"/> must never be used on such a buffer, and this is not
    /// a style preference. NAudio's <c>SampleToWaveProvider</c> does not allocate a float buffer at
    /// all: it wraps the device's <c>byte[]</c> in a <c>WaveBuffer</c>, an explicit-layout union whose
    /// <c>FloatBuffer</c> property hands back <i>the same object</i> retyped as <c>float[]</c>.
    /// Indexing it works, because <c>buffer[i] = 0f</c> is compiled against the static type and emits
    /// a four-byte store. Anything that asks the <i>runtime</i> what the array is gets <c>Byte[]</c> -
    /// and <c>Array.Clear</c> asks. Given <c>(buffer, 0, 966)</c> it clears 966 <i>bytes</i>: a quarter
    /// of the range, leaving samples 242 onwards holding whatever the previous read left there.
    /// <c>buffer.Length</c> lies the same way, reporting bytes; so would
    /// <see cref="Array.Copy(Array, int, Array, int, int)"/>.
    /// </para>
    /// <para>
    /// That was the whole of the pause bug the editor shipped with. A paused graph handed the endpoint
    /// three quarters of a buffer of pre-pause audio, once per device period, for as long as the pause
    /// lasted - which is why the measured tail was flat at -17 dBFS instead of decaying, why it ran
    /// 400 ms and beyond rather than one buffer, and why pausing with nothing sounding was silent. It
    /// reproduces only against a real device: the headless harness hands out honest <c>float[]</c>s,
    /// so every existing pause case stayed green while the editor squealed.
    /// </para>
    /// <para>
    /// The rest of the solution was then swept for the same class of mistake, since one instance of it
    /// cost six rounds. Every other bulk operation on a float array in the audio path works on an array
    /// the same method allocated - <c>NullAudioOutput.Pump</c>'s local buffer, <c>KeysoundDecoder</c>'s
    /// <c>accumulated</c>/<c>block</c> pair, <c>KeysoundRenderProbe</c>'s <c>mix</c> - so those are
    /// genuinely <c>float[]</c> and <c>Array.Clear</c>/<c>Array.Copy</c>/<c>Length</c> mean what they
    /// say there. The rule is only about arrays that arrive as a parameter of
    /// <see cref="ISampleProvider.Read"/>: those belong to the caller, and above the mixer the caller
    /// is a device. <c>GraphTapSampleProvider</c> and <c>KeysoundDecoder.BoundedSampleProvider</c> take
    /// such a buffer and correctly touch it only through indexing. The single surviving
    /// <c>buffer.Length</c> in the graph is in <c>PlaybackGroupSampleProvider.Trace</c>, which prints it
    /// next to <c>buffer.GetType().Name</c> on purpose: seeing <c>type=Byte[] len=23040</c> for a
    /// 966-sample read is what ended the investigation.
    /// </para>
    /// </summary>
    internal static class SampleBuffers
    {
        /// <summary>
        /// Zeroes <paramref name="count"/> <b>samples</b> from <paramref name="offset"/>, one indexed
        /// store at a time, so the element size comes from the static type rather than from whatever
        /// the array turns out to be at runtime.
        /// </summary>
        internal static void Silence(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] = 0f;
            }
        }
    }
}
