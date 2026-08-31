using System;
using System.Globalization;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using SharpGen.Runtime.Win32;
using Vortice.MediaFoundation;

namespace DJMaxEditor.Studio.Video
{
    /// <summary>
    /// Reference-counted <c>MFStartup</c>/<c>MFShutdown</c>.
    /// <para>
    /// Media Foundation is per-process, not per-object: <c>MFShutdown</c> tears the platform down
    /// for everyone, so the first source to be disposed must not kill a second one that is still
    /// open (preview plus, later, a thumbnail strip). Counting also lets the failure be cached -
    /// on a Windows N edition <c>mfplat.dll</c> does not exist at all and the P/Invoke throws
    /// <see cref="DllNotFoundException"/>, which is not something to re-attempt on every file the
    /// user drops.
    /// </para>
    /// </summary>
    internal static class MediaFoundationRuntime
    {
        internal const string MissingPlatformHint =
            "Media Foundation is not available on this Windows installation. " +
            "Install the Media Feature Pack (Settings > Apps > Optional features) to enable BGA " +
            "video preview.";

        /// <summary>
        /// The softer version, for the failures that mean "this particular file" on a normal
        /// Windows install but mean "no decoders at all" on an N or KN edition. The two are
        /// indistinguishable from the HRESULT alone, so the message has to cover both without
        /// asserting either.
        /// </summary>
        internal const string MissingCodecHint =
            "If this is an ordinary MP4, WMV or AVI, this Windows edition may be missing the " +
            "Media Feature Pack (Settings > Apps > Optional features).";

        private static readonly object Gate = new object();

        private static int _refCount;
        private static string _failure;

        public static bool TryAcquire(out string error)
        {
            lock (Gate)
            {
                if (_failure != null)
                {
                    error = _failure;
                    return false;
                }

                if (_refCount > 0)
                {
                    _refCount++;
                    error = null;
                    return true;
                }

                try
                {
                    Result hr = MediaFactory.MFStartup(false);
                    if (hr.Failure)
                    {
                        _failure = MissingPlatformHint + " (MFStartup returned 0x" +
                            hr.Code.ToString("X8", CultureInfo.InvariantCulture) + ")";
                        DJMaxEditor.Logs.Write("BGA: MFStartup failed 0x{0:X8}", hr.Code);
                        error = _failure;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _failure = MissingPlatformHint + " (" + ex.GetType().Name + ": " + ex.Message + ")";
                    DJMaxEditor.Logs.Write("BGA: MFStartup threw {0}: {1}", ex.GetType().Name, ex.Message);
                    error = _failure;
                    return false;
                }

                _refCount = 1;
                error = null;
                return true;
            }
        }

        public static void Release()
        {
            lock (Gate)
            {
                if (_refCount <= 0)
                {
                    return;
                }

                _refCount--;
                if (_refCount > 0)
                {
                    return;
                }

                try
                {
                    MediaFactory.MFShutdown();
                }
                catch (Exception ex)
                {
                    DJMaxEditor.Logs.Write("BGA: MFShutdown threw {0}", ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Frame-accurate BGA decoding on top of Media Foundation's <c>IMFSourceReader</c>, driven
    /// synchronously from whatever thread the editor scrubs on.
    /// <para>
    /// The source reader is the only MF component that fits an editor. <c>MediaElement</c> and
    /// <c>MediaPlayer</c> instantiate the Windows Media Player engine and seek through a
    /// <see cref="double"/> of seconds with no frame-step, no seek-completed signal and no way to
    /// ask what was actually presented. <c>IMFMediaEngine</c>, even in frame-server mode, keeps
    /// the presentation clock and insists on an audio stream. The source reader documents the
    /// opposite contract - it manages no presentation clock, and Microsoft lists "you already have
    /// a media pipeline that is not based on Media Foundation" as a reason to pick it - which is
    /// exactly our situation: the sequencer owns time and the BGA is a slave to it.
    /// </para>
    /// <para>
    /// The exact-frame recipe is the documented one. <c>SetCurrentPosition</c> seeks to the nearest
    /// key frame at or before the request, so reaching a specific frame means reading samples and
    /// discarding them until the timestamps arrive - the same discard loop as
    /// <c>VideoThumbnail/Thumbnail.cpp</c> in the Windows classic samples, including its bound on
    /// the number of frames skipped.
    /// </para>
    /// </summary>
    public sealed class MediaFoundationBgaSource : IBgaSource
    {
        /// <summary>
        /// Sentinel for "no timestamp known yet". Real presentation times are non-negative.
        /// </summary>
        private const long NoTimestamp = long.MinValue;

        /// <summary>
        /// How far ahead of the reader's current position we decode-and-discard instead of seeking.
        /// <para>
        /// Seeking is the expensive operation here: it flushes the decoder and forces it to restart
        /// from a key frame, which on long-GOP H.264 means decoding up to a couple of hundred
        /// frames before the wanted one appears. Ordinary playback advances the playhead by one
        /// frame at a time, so seeking on every step would turn a 33 ms job into a 300 ms one.
        /// Below this threshold, walking forward through the samples we would have decoded anyway
        /// is strictly cheaper.
        /// </para>
        /// </summary>
        private const long ForwardSeekToleranceTicks = TimeSpan.TicksPerSecond;

        /// <summary>
        /// Ceiling on the discard loop, after Thumbnail.cpp's MAX_FRAMES_TO_SKIP. This runs on the
        /// UI thread, so a file that reports timestamps which never reach the target - truncated,
        /// or with a broken index - must give up rather than spin.
        /// </summary>
        private const int MaxFramesToSkip = 300;

        /// <summary>
        /// Used to decide "is this sample the frame for the requested time" when neither the sample
        /// nor the media type says how long a frame lasts. 25 fps is the pessimistic guess: too
        /// small only costs an extra loop iteration, too large would return a frame that is
        /// visibly early.
        /// </summary>
        private const long FallbackFrameSpanTicks = TimeSpan.TicksPerSecond / 25;

        /// <summary>
        /// How many probes the bisection in <see cref="SeekTo"/> gets when the source refuses a
        /// position the declared duration said was legal. Twelve halvings resolve a five second lie
        /// to well under one frame, and the bound is what stops a file that refuses everything from
        /// spinning on the UI thread.
        /// </summary>
        private const int SeekBackOffAttempts = 12;

        // mferror.h. Kept as raw HRESULTs because these are the values the reader actually hands
        // back for the two failures a user can hit without doing anything wrong: an unrecognised
        // container, and a container we can parse but whose codec is not installed (N/KN editions).
        private const int ErrorUnsupportedByteStreamType = unchecked((int)0xC00D36C4);
        private const int ErrorTopoCodecNotFound = unchecked((int)0xC00D5212);
        private const int ErrorInvalidMediaType = unchecked((int)0xC00D36B4);
        private const int ErrorInvalidFileFormat = unchecked((int)0xC00D36BE);
        private const int ErrorUnsupportedScheme = unchecked((int)0xC00D36C3);
        private const int ErrorInvalidPosition = unchecked((int)0xC00D36E5);

        private IMFSourceReader _reader;
        private bool _platformHeld;
        private bool _disposed;

        private int _width;
        private int _height;
        private int _defaultStride;
        private double _frameRate;
        private TimeSpan _duration;
        private string _description = string.Empty;

        /// <summary>Timestamp of the most recent sample the reader handed us.</summary>
        private long _readerTicks = NoTimestamp;

        /// <summary>Timestamp of the frame we last copied out, and how long it lasts.</summary>
        private long _heldTicks = NoTimestamp;
        private long _heldSpanTicks;

        private bool _endOfStream;

        public bool IsOpen => _reader != null;

        public int PixelWidth => _width;

        public int PixelHeight => _height;

        public TimeSpan Duration => _duration;

        public double FrameRate => _frameRate;

        public string Description => _description;

        public bool Open(string path, out string error)
        {
            Close();

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No video file specified.";
                return false;
            }

            if (!System.IO.File.Exists(path))
            {
                error = "File not found: " + path;
                return false;
            }

            if (!MediaFoundationRuntime.TryAcquire(out error))
            {
                return false;
            }

            _platformHeld = true;

            try
            {
                _reader = CreateReader(path);
                ConfigureStreams();
                ReadFormat();
                ReadDuration();
                ReadDescription();

                if (_width <= 0 || _height <= 0)
                {
                    error = "The file has no readable video stream.";
                    Close();
                    return false;
                }

                _readerTicks = 0;
                error = null;
                DJMaxEditor.Logs.Write(
                    "BGA: opened {0} - {1}x{2} stride {3} {4:0.###} fps {5}",
                    path, _width, _height, _defaultStride, _frameRate, _duration);
                return true;
            }
            catch (SharpGenException ex)
            {
                error = DescribeHResult(ex.ResultCode.Code, ex.Message);
                DJMaxEditor.Logs.Write("BGA: open failed 0x{0:X8} for {1}", ex.ResultCode.Code, path);
                Close();
                return false;
            }
            catch (Exception ex)
            {
                error = "Could not open the video: " + ex.Message;
                DJMaxEditor.Logs.Write("BGA: open threw {0}: {1}", ex.GetType().Name, ex.Message);
                Close();
                return false;
            }
        }

        public void Close()
        {
            if (_reader != null)
            {
                try
                {
                    _reader.Dispose();
                }
                catch (Exception ex)
                {
                    DJMaxEditor.Logs.Write("BGA: reader dispose threw {0}", ex.Message);
                }
                _reader = null;
            }

            _width = 0;
            _height = 0;
            _defaultStride = 0;
            _frameRate = 0.0;
            _duration = TimeSpan.Zero;
            _description = string.Empty;
            _readerTicks = NoTimestamp;
            _heldTicks = NoTimestamp;
            _heldSpanTicks = 0;
            _endOfStream = false;

            if (_platformHeld)
            {
                _platformHeld = false;
                MediaFoundationRuntime.Release();
            }
        }

        public bool TryGetFrame(TimeSpan position, BgaFrameBuffer destination)
        {
            if (destination == null || _reader == null)
            {
                return false;
            }

            try
            {
                return ReadInto(position, destination);
            }
            catch (Exception ex)
            {
                DJMaxEditor.Logs.Write(
                    "BGA: decode failed at {0} - {1}: {2}", position, ex.GetType().Name, ex.Message);
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Close();
        }

        private IMFSourceReader CreateReader(string path)
        {
            using (IMFAttributes attributes = MediaFactory.MFCreateAttributes(1))
            {
                // Without this the reader hands back the decoder's native output - NV12 or YUY2 for
                // anything modern - and we would be writing a colour-space converter by hand.
                // With it MF inserts the Video Processor MFT, which is what makes the RGB32 output
                // type below negotiable at all.
                attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, (uint)1);
                return MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
            }
        }

        private void ConfigureStreams()
        {
            // Audio must not be decoded at all: the editor's own mixer owns sound, and leaving the
            // audio stream selected makes the reader decode and buffer it on every ReadSample.
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using (IMFMediaType target = MediaFactory.MFCreateMediaType())
            {
                // A partial type - major type and subtype only. Leaving the frame size, stride and
                // frame rate unset is what lets the reader keep the file's own geometry instead of
                // making us guess it before we have read it.
                target.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                target.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, target);
            }
        }

        private void ReadFormat()
        {
            using (IMFMediaType current = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                ulong packed;
                if (current.GetUInt64(MediaTypeAttributeKeys.FrameSize, out packed).Success)
                {
                    _width = (int)(packed >> 32);
                    _height = (int)(packed & 0xFFFFFFFFUL);
                }

                ulong rate;
                if (current.GetUInt64(MediaTypeAttributeKeys.FrameRate, out rate).Success)
                {
                    uint numerator = (uint)(rate >> 32);
                    uint denominator = (uint)(rate & 0xFFFFFFFFUL);
                    _frameRate = denominator == 0 ? 0.0 : numerator / (double)denominator;
                }

                uint stride;
                if (current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out stride).Success)
                {
                    // MF_MT_DEFAULT_STRIDE is stored in a UINT32 slot but is a signed value, and a
                    // negative stride is the normal case for RGB32 rather than an oddity: it means
                    // the surface is bottom-up in memory. Reading it as unsigned yields ~4 billion
                    // and an upside-down picture.
                    _defaultStride = unchecked((int)stride);
                }
                else if (_width > 0)
                {
                    // MSDN's Image Stride topic: when an uncompressed RGB type does not carry a
                    // stride, the orientation to assume is bottom-up.
                    _defaultStride = -(_width * 4);
                }
            }
        }

        private void ReadDuration()
        {
            try
            {
                Variant value = _reader.GetPresentationAttribute(
                    SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
                long ticks = Convert.ToInt64(value.Value, CultureInfo.InvariantCulture);
                if (ticks > 0)
                {
                    _duration = TimeSpan.FromTicks(ticks);
                }
            }
            catch (Exception)
            {
                // MF_PD_DURATION is absent for live and for some malformed containers. An unknown
                // duration is a documented state of IBgaSource, not an error.
                _duration = TimeSpan.Zero;
            }
        }

        private void ReadDescription()
        {
            string codec = null;
            try
            {
                using (IMFMediaType native =
                    _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
                {
                    Guid subtype;
                    if (native.GetGUID(MediaTypeAttributeKeys.Subtype, out subtype).Success)
                    {
                        codec = SubtypeName(subtype);
                    }
                }
            }
            catch (Exception)
            {
                codec = null;
            }

            string container = null;
            try
            {
                Variant value = _reader.GetPresentationAttribute(
                    SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.MimeType);
                container = value.Value as string;
            }
            catch (Exception)
            {
                container = null;
            }

            if (!string.IsNullOrEmpty(codec) && !string.IsNullOrEmpty(container))
            {
                _description = codec + " (" + container + ")";
            }
            else
            {
                _description = codec ?? container ?? string.Empty;
            }
        }

        private bool ReadInto(TimeSpan position, BgaFrameBuffer destination)
        {
            long target = position.Ticks;
            if (target < 0)
            {
                target = 0;
            }
            if (_duration > TimeSpan.Zero && target >= _duration.Ticks)
            {
                // Seeking to exactly the declared duration lands past the last frame and returns
                // end-of-stream with nothing decoded, so a playhead parked on the final tick would
                // show whatever happened to be on screen. Back off one frame to stay inside it.
                target = Math.Max(0, _duration.Ticks - NominalFrameSpanTicks);
            }

            bool wantsHeldFrame = HoldsFrameFor(target);
            if (wantsHeldFrame && destination.Timestamp.Ticks == _heldTicks &&
                destination.Width == _width && destination.Height == _height)
            {
                // The single most important line in this class. A 30 fps BGA changes every ~33 ms
                // but the playhead moves every composition frame, so the overwhelming majority of
                // Seek calls resolve to the frame already on screen and must cost nothing.
                return true;
            }

            bool seek = _readerTicks == NoTimestamp
                || target < _readerTicks
                || target - _readerTicks > ForwardSeekToleranceTicks
                || wantsHeldFrame;

            if (seek)
            {
                SeekTo(target);
            }

            long lastSeen;
            if (DecodeTo(target, destination, out lastSeen))
            {
                return true;
            }

            // Containers over-declare their duration - Matroska routinely reports one a frame or
            // more past its last sample - and then even the backed-off target above sits beyond
            // every remaining frame, so the whole tail is discarded as "too early" and the stream
            // ends with nothing copied. The newest timestamp the loop saw is by definition the last
            // frame, so aim at that. This costs a second pass, but only ever at the end of a file.
            if (lastSeen != NoTimestamp && lastSeen < target)
            {
                SeekTo(lastSeen);
                long ignored;
                if (DecodeTo(lastSeen, destination, out ignored))
                {
                    // That was provably the final frame, and saying so is what lets every later
                    // position take the held-frame short circuit instead of repeating this.
                    _endOfStream = true;
                    return true;
                }
            }

            return _heldTicks != NoTimestamp && destination.Timestamp.Ticks == _heldTicks;
        }

        private void SeekTo(long ticks)
        {
            if (TrySetPosition(ticks))
            {
                return;
            }

            // The wanted position is past the real presentation end even though the declared duration
            // said it was legal - Matroska routinely over-declares, and there is no API for "where
            // does this actually end". Bisect for the last position the source will accept. A seek
            // costs a decoder flush but no decoding, so this is cheap next to one wrong frame, and
            // landing within a frame of the end lets the end-of-stream retry in ReadInto finish the
            // job in this same call rather than creeping closer over several scrubs.
            long low = _heldTicks != NoTimestamp ? _heldTicks : 0;
            long high = ticks;
            long best = NoTimestamp;
            long precision = NominalFrameSpanTicks;

            for (int i = 0; i < SeekBackOffAttempts && high - low > precision; i++)
            {
                long middle = low + ((high - low) / 2);
                if (middle <= low)
                {
                    break;
                }

                if (TrySetPosition(middle))
                {
                    best = middle;
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            if (best != NoTimestamp)
            {
                if (_readerTicks != best)
                {
                    TrySetPosition(best);
                }
                return;
            }

            // Not even the floor was accepted. Leaving the reader untouched lets DecodeTo read on
            // from wherever it is, which is the best answer available and, unlike letting this throw,
            // keeps the end-of-stream retry in ReadInto reachable.
            DJMaxEditor.Logs.Write(
                "BGA: seek to {0} refused, reading on from {1}",
                TimeSpan.FromTicks(ticks),
                _readerTicks == NoTimestamp ? TimeSpan.Zero : TimeSpan.FromTicks(_readerTicks));
        }

        private bool TrySetPosition(long ticks)
        {
            try
            {
                _reader.SetCurrentPosition(ticks);
                _readerTicks = ticks;
                _endOfStream = false;
                return true;
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == ErrorInvalidPosition)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads forward from wherever the reader is, discarding frames that end before
        /// <paramref name="target"/>, and copies the first one that covers it.
        /// <paramref name="lastSeen"/> reports the newest timestamp read even when nothing was
        /// copied, which is what makes the end-of-stream retry in <see cref="ReadInto"/> possible.
        /// </summary>
        private bool DecodeTo(long target, BgaFrameBuffer destination, out long lastSeen)
        {
            lastSeen = NoTimestamp;

            for (int skipped = 0; skipped < MaxFramesToSkip && !_endOfStream; skipped++)
            {
                int actualStream;
                SourceReaderFlag flags;
                long timestamp;
                IMFSample sample = _reader.ReadSample(
                    SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                    out actualStream, out flags, out timestamp);

                try
                {
                    if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
                    {
                        ReadFormat();
                    }

                    if ((flags & SourceReaderFlag.EndOfStream) != 0)
                    {
                        // Clamp: the last frame stays on screen for the rest of the chart rather
                        // than the preview going black past the end of a short BGA.
                        _endOfStream = true;
                        break;
                    }

                    if (sample == null)
                    {
                        // A stream tick - a documented gap in the stream, not a failure. Keep
                        // reading; the loop bound is what stops this becoming a hang.
                        continue;
                    }

                    _readerTicks = timestamp;
                    lastSeen = timestamp;
                    long span = FrameSpanTicks(sample);

                    bool stillEarly = timestamp + span <= target;
                    if (stillEarly && skipped + 1 < MaxFramesToSkip)
                    {
                        continue;
                    }

                    if (!CopyFrame(sample, destination))
                    {
                        return false;
                    }

                    destination.Timestamp = TimeSpan.FromTicks(timestamp);
                    _heldTicks = timestamp;
                    _heldSpanTicks = span;
                    return true;
                }
                finally
                {
                    if (sample != null)
                    {
                        sample.Dispose();
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="target"/> falls inside the frame we already decoded. Past the
        /// end of the stream every later time maps to the last frame.
        /// </summary>
        private bool HoldsFrameFor(long target)
        {
            if (_heldTicks == NoTimestamp || target < _heldTicks)
            {
                return false;
            }
            if (_endOfStream)
            {
                return true;
            }
            long span = _heldSpanTicks > 0 ? _heldSpanTicks : NominalFrameSpanTicks;
            return target < _heldTicks + span;
        }

        private long NominalFrameSpanTicks =>
            _frameRate > 0.0
                ? (long)Math.Round(TimeSpan.TicksPerSecond / _frameRate)
                : FallbackFrameSpanTicks;

        private long FrameSpanTicks(IMFSample sample)
        {
            try
            {
                long duration = sample.SampleDuration;
                if (duration > 0)
                {
                    return duration;
                }
            }
            catch (Exception)
            {
                // MF_E_NO_SAMPLE_DURATION. Variable-frame-rate captures and some AVI sources leave
                // it off every sample.
            }

            if (_frameRate > 0.0)
            {
                return NominalFrameSpanTicks;
            }
            return FallbackFrameSpanTicks;
        }

        private bool CopyFrame(IMFSample sample, BgaFrameBuffer destination)
        {
            if (_width <= 0 || _height <= 0)
            {
                return false;
            }

            destination.Resize(_width, _height);

            IMFMediaBuffer buffer = sample.BufferCount == 1
                ? sample.GetBufferByIndex(0)
                : sample.ConvertToContiguousBuffer();

            try
            {
                if (CopyVia2D(buffer, destination))
                {
                    return true;
                }
                return CopyViaContiguous(buffer, destination);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        /// <summary>
        /// The preferred path: the 2D interfaces report the surface's real pitch, so a decoder that
        /// pads rows out to a hardware alignment is handled without guessing.
        /// </summary>
        private bool CopyVia2D(IMFMediaBuffer buffer, BgaFrameBuffer destination)
        {
            IMF2DBuffer2 buffer2 = buffer.QueryInterfaceOrNull<IMF2DBuffer2>();
            if (buffer2 != null)
            {
                try
                {
                    IntPtr scanline0;
                    int pitch;
                    IntPtr start;
                    int length;
                    buffer2.Lock2DSize(Buffer2DLockFlags.Read, out scanline0, out pitch, out start, out length);
                    try
                    {
                        CopyRowsFromTop(scanline0, pitch, destination);
                        return true;
                    }
                    finally
                    {
                        buffer2.Unlock2D();
                    }
                }
                finally
                {
                    buffer2.Dispose();
                }
            }

            IMF2DBuffer legacy = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
            if (legacy == null)
            {
                return false;
            }

            try
            {
                IntPtr scanline0;
                int pitch;
                legacy.Lock2D(out scanline0, out pitch);
                try
                {
                    CopyRowsFromTop(scanline0, pitch, destination);
                    return true;
                }
                finally
                {
                    legacy.Unlock2D();
                }
            }
            finally
            {
                legacy.Dispose();
            }
        }

        /// <summary>
        /// Last resort, for a buffer that implements neither 2D interface. <see cref="CopyFrame"/>
        /// has already made sure this is a single contiguous buffer.
        /// </summary>
        private bool CopyViaContiguous(IMFMediaBuffer buffer, BgaFrameBuffer destination)
        {
            IntPtr data;
            int maxLength;
            int currentLength;
            buffer.Lock(out data, out maxLength, out currentLength);
            try
            {
                CopyRowsFromMemoryOrder(data, _defaultStride, destination);
                return true;
            }
            finally
            {
                buffer.Unlock();
            }
        }

        /// <summary>
        /// Lock2D's contract is that scanline 0 is the <em>top</em> row as presented and that the
        /// pitch may be negative for a bottom-up surface. Stepping the source by the signed pitch
        /// therefore walks the image top to bottom whichever way it is stored, and no explicit flip
        /// is needed.
        /// </summary>
        private static void CopyRowsFromTop(IntPtr scanline0, int pitch, BgaFrameBuffer destination)
        {
            int rowBytes = Math.Min(Math.Abs(pitch), destination.Stride);
            if (rowBytes <= 0)
            {
                rowBytes = destination.Stride;
            }

            for (int y = 0; y < destination.Height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(scanline0, y * pitch),
                    destination.Pixels,
                    y * destination.Stride,
                    rowBytes);
            }
        }

        /// <summary>
        /// The fallback path, where the pointer is simply the first byte of the buffer. Here a
        /// negative <paramref name="stride"/> does mean the rows are stored bottom-up, so the first
        /// row in memory is the last row on screen and the copy has to reverse them. Getting this
        /// wrong is not subtle - the whole video appears upside down.
        /// </summary>
        private static void CopyRowsFromMemoryOrder(IntPtr data, int stride, BgaFrameBuffer destination)
        {
            bool bottomUp = stride < 0;
            int sourceStride = Math.Abs(stride);
            if (sourceStride == 0)
            {
                sourceStride = destination.Stride;
            }

            if (!bottomUp && sourceStride == destination.Stride)
            {
                Marshal.Copy(data, destination.Pixels, 0, destination.Stride * destination.Height);
                return;
            }

            int rowBytes = Math.Min(sourceStride, destination.Stride);
            for (int y = 0; y < destination.Height; y++)
            {
                int sourceRow = bottomUp ? destination.Height - 1 - y : y;
                Marshal.Copy(
                    IntPtr.Add(data, sourceRow * sourceStride),
                    destination.Pixels,
                    y * destination.Stride,
                    rowBytes);
            }
        }

        private static string DescribeHResult(int code, string fallback)
        {
            switch (code)
            {
                case ErrorUnsupportedByteStreamType:
                case ErrorInvalidFileFormat:
                    return "Media Foundation does not recognise this file as a media container. " +
                        MediaFoundationRuntime.MissingCodecHint;
                case ErrorTopoCodecNotFound:
                case ErrorInvalidMediaType:
                    return "No decoder is installed for this video's codec. " +
                        MediaFoundationRuntime.MissingCodecHint;
                case ErrorUnsupportedScheme:
                    return "Media Foundation cannot read from this location.";
                default:
                    return "Could not open the video (0x" +
                        code.ToString("X8", CultureInfo.InvariantCulture) + "): " + fallback;
            }
        }

        /// <summary>
        /// Media Foundation's video subtype GUIDs are the FourCC in the first four bytes followed
        /// by the fixed <c>0000-0010-8000-00AA00389B71</c> tail, so H.264 reads back literally as
        /// "H264" and Windows Media 9 as "WMV3". That beats a hand-maintained GUID table.
        /// </summary>
        private static string SubtypeName(Guid subtype)
        {
            byte[] bytes = subtype.ToByteArray();
            bool fourCc =
                bytes[4] == 0x00 && bytes[5] == 0x00 && bytes[6] == 0x10 && bytes[7] == 0x00 &&
                bytes[8] == 0x80 && bytes[9] == 0x00 && bytes[10] == 0x00 && bytes[11] == 0xAA &&
                bytes[12] == 0x00 && bytes[13] == 0x38 && bytes[14] == 0x9B && bytes[15] == 0x71;

            if (fourCc)
            {
                char[] chars = new char[4];
                for (int i = 0; i < 4; i++)
                {
                    byte b = bytes[i];
                    if (b < 0x20 || b > 0x7E)
                    {
                        return subtype.ToString("D", CultureInfo.InvariantCulture);
                    }
                    chars[i] = (char)b;
                }
                return new string(chars).Trim();
            }

            return subtype.ToString("D", CultureInfo.InvariantCulture);
        }
    }
}
