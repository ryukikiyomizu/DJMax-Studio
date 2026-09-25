using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DJMaxEditor.Studio.Keyslicer
{
    internal static class KeyslicerAudioReader
    {
        public static WaveStream OpenPlaybackReader(string path)
        {
            var attempts = new List<string>();
            string ext = Path.GetExtension(path) ?? string.Empty;
            bool isVorbisExt = string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".oga", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".opus", StringComparison.OrdinalIgnoreCase);
            bool isWaveExt = string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ext, ".wave", StringComparison.OrdinalIgnoreCase);
            bool isAiffExt = string.Equals(ext, ".aif", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ext, ".aiff", StringComparison.OrdinalIgnoreCase);

            if (isVorbisExt && TryOpen(() => new VorbisWaveReader(path), attempts, out WaveStream vorbis))
                return vorbis;

            if (isWaveExt && TryOpen(() => new WaveFileReader(path), attempts, out WaveStream wave))
                return wave;

            if (isAiffExt && TryOpen(() => new AiffFileReader(path), attempts, out WaveStream aiff))
                return aiff;

            if (TryOpen(() => new AudioFileReader(path), attempts, out WaveStream audioFileReader))
                return audioFileReader;

            if (TryOpen(() => new MediaFoundationReader(path), attempts, out WaveStream mediaFoundationReader))
                return mediaFoundationReader;

            if (!isVorbisExt && TryOpen(() => new VorbisWaveReader(path), attempts, out WaveStream fallbackVorbis))
                return fallbackVorbis;

            throw new InvalidOperationException(
                "unsupported audio format for " + path + ": " + string.Join(" | ", attempts));
        }

        public static ISampleProvider OpenSampleProvider(string path, out IDisposable disposable)
        {
            var attempts = new List<string>();
            string ext = Path.GetExtension(path) ?? string.Empty;
            bool isVorbisExt = string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".oga", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".opus", StringComparison.OrdinalIgnoreCase);
            bool isWaveExt = string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ext, ".wave", StringComparison.OrdinalIgnoreCase);
            bool isAiffExt = string.Equals(ext, ".aif", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ext, ".aiff", StringComparison.OrdinalIgnoreCase);

            if (isVorbisExt && TryOpenProvider(() =>
                {
                    var v = new VorbisWaveReader(path);
                    return v;
                }, attempts, out ISampleProvider vorbis, out disposable))
                return vorbis;

            if (isWaveExt && TryOpenProvider(() =>
                {
                    var w = new WaveFileReader(path);
                    return w;
                }, attempts, out ISampleProvider wave, out disposable))
                return wave;

            if (isAiffExt && TryOpenProvider(() =>
                {
                    var a = new AiffFileReader(path);
                    return a;
                }, attempts, out ISampleProvider aiff, out disposable))
                return aiff;

            if (TryOpenProvider(() =>
                {
                    var a = new AudioFileReader(path);
                    return a;
                }, attempts, out ISampleProvider audioFileReader, out disposable))
                return audioFileReader;

            if (TryOpenProvider(() =>
                {
                    var m = new MediaFoundationReader(path);
                    return m;
                }, attempts, out ISampleProvider mediaFoundationReader, out disposable))
                return mediaFoundationReader;

            if (!isVorbisExt && TryOpenProvider(() =>
                {
                    var v = new VorbisWaveReader(path);
                    return v;
                }, attempts, out ISampleProvider fallbackVorbis, out disposable))
                return fallbackVorbis;

            throw new InvalidOperationException(
                "unsupported audio format for " + path + ": " + string.Join(" | ", attempts));
        }

        public static ISampleProvider DownmixToMono(ISampleProvider source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            int channels = source.WaveFormat.Channels;
            if (channels <= 1)
                return source;

            if (channels == 2)
                return new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };

            return new MultiChannelToMonoSampleProvider(source);
        }

        private static bool TryOpen(Func<WaveStream> factory, List<string> attempts, out WaveStream stream)
        {
            try
            {
                stream = factory();
                return true;
            }
            catch (Exception ex)
            {
                stream = null;
                attempts.Add(ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool TryOpenProvider(Func<WaveStream> factory, List<string> attempts, out ISampleProvider provider, out IDisposable disposable)
        {
            try
            {
                var stream = factory();
                disposable = stream;
                provider = stream is ISampleProvider sampleProvider
                    ? sampleProvider
                    : stream.ToSampleProvider();
                return true;
            }
            catch (Exception ex)
            {
                provider = null;
                disposable = null;
                attempts.Add(ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private sealed class MultiChannelToMonoSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly WaveFormat _waveFormat;
            private readonly float[] _sourceBuffer;

            public MultiChannelToMonoSampleProvider(ISampleProvider source)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
                _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
                _sourceBuffer = new float[Math.Max(4096, source.WaveFormat.Channels * 1024)];
            }

            public WaveFormat WaveFormat => _waveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

                int channels = _source.WaveFormat.Channels;
                int framesRequested = count;
                int produced = 0;

                while (produced < framesRequested)
                {
                    int framesRemaining = framesRequested - produced;
                    int sourceSamplesWanted = Math.Min(_sourceBuffer.Length, framesRemaining * channels);
                    sourceSamplesWanted -= sourceSamplesWanted % channels;
                    if (sourceSamplesWanted <= 0)
                        break;

                    int read = _source.Read(_sourceBuffer, 0, sourceSamplesWanted);
                    if (read <= 0)
                        break;

                    int framesRead = read / channels;
                    for (int frame = 0; frame < framesRead && produced < framesRequested; frame++, produced++)
                    {
                        int frameOffset = frame * channels;
                        float sum = 0f;
                        for (int ch = 0; ch < channels; ch++)
                            sum += _sourceBuffer[frameOffset + ch];
                        buffer[offset + produced] = sum / channels;
                    }
                }

                return produced;
            }
        }
    }
}
