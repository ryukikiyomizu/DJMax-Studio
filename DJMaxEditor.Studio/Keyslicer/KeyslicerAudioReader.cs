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
            return OpenDecodedWaveStream(path);
        }

        public static ISampleProvider OpenSampleProvider(string path, out IDisposable disposable)
        {
            var stream = OpenDecodedWaveStream(path);
            disposable = stream;
            return stream is ISampleProvider sampleProvider
                ? sampleProvider
                : stream.ToSampleProvider();
        }

        private static WaveStream OpenDecodedWaveStream(string path)
        {
            var attempts = new List<string>();
            string ext = Path.GetExtension(path) ?? string.Empty;
            bool isVorbisExt = string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".oga", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".opus", StringComparison.OrdinalIgnoreCase);

            if (isVorbisExt && TryOpen(() => new VorbisWaveReader(path), attempts, out WaveStream vorbis))
                return vorbis;

            if (TryOpen(() => new AudioFileReader(path), attempts, out WaveStream audioFileReader))
                return audioFileReader;

            if (TryOpen(() => new MediaFoundationReader(path), attempts, out WaveStream mediaFoundationReader))
                return mediaFoundationReader;

            if (!isVorbisExt && TryOpen(() => new VorbisWaveReader(path), attempts, out WaveStream fallbackVorbis))
                return fallbackVorbis;

            throw new InvalidOperationException(
                "unsupported audio format for " + path + ": " + string.Join(" | ", attempts));
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
    }
}
