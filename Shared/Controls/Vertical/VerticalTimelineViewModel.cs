using System;
using System.Collections.Generic;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;

namespace DJMaxEditor.Controls.Vertical
{
    /// <summary>Carries a playhead move requested by a click on the vertical ruler.</summary>
    public sealed class VerticalSeekEventArgs : EventArgs
    {
        public VerticalSeekEventArgs(int virtualTick)
        {
            VirtualTick = virtualTick;
        }

        public int VirtualTick { get; private set; }
    }

    /// <summary>
    /// Pure, WinForms-free state machine behind the vertical timeline surface. It owns
    /// scroll/zoom/playhead state, rebuilds the projection when the chart is edited, and
    /// reads and writes the document's shared <see cref="ChartSelectionService"/> so the
    /// vertical surface, the V1 editor, the V2 timeline, and the Inspector always agree.
    /// </summary>
    /// <remarks>
    /// Nothing here touches System.Windows.Forms, so every synchronization rule
    /// (selection, hit testing, playhead follow, zoom) is directly unit testable.
    /// <see cref="VerticalTimelineControl"/> is a thin renderer/input adapter on top.
    /// </remarks>
    public sealed class VerticalTimelineViewModel : IDisposable
    {
        /// <summary>
        /// Pixels per virtual tick at zoom factor 1.0. Deliberately denser than V2's initial
        /// horizontal density: the vertical strip is read like a gameplay chart, so a
        /// measure has to be tall enough to tell 16ths apart at rest without zooming in.
        /// </summary>
        public const double BasePixelsPerTick = 0.55;

        public const double MinPixelsPerTick = 0.01;
        public const double MaxPixelsPerTick = 8.0;

        /// <summary>
        /// Floor for the column squeeze. Auto-fit may shrink columns to get a whole layout
        /// on screen, but past this point note bars stop being distinguishable, so wide
        /// layouts scroll horizontally instead of collapsing.
        /// </summary>
        public const double MinColumnScale = 0.55;
        public const double MaxColumnScale = 2.0;

        /// <summary>Left gutter reserved for measure/tick labels.</summary>
        public const int DefaultHeaderWidth = 44;

        /// <summary>Top strip reserved for column name labels.</summary>
        public const int DefaultRulerHeight = 22;

        private readonly HashSet<EventData> _selected = new HashSet<EventData>();
        private double _pixelsPerTick = BasePixelsPerTick;
        private double _noteThickness = 1.0;
        private double _columnScale = 1.0;
        private float _dpiScale = 1f;
        private double _originTick;
        private double _originNativeX;
        private int _playheadVirtualTick;
        private int _lastWidth;
        private int _lastHeight;
        private int _modeOverride;
        private VerticalTimeDirection _timeDirection = VerticalTimeDirection.Upward;
        private bool _disposed;

        public VerticalTimelineViewModel()
        {
            HeaderWidth = DefaultHeaderWidth;
            RulerHeight = DefaultRulerHeight;
            AutoFitColumns = true;
            ZoomFactor = 1f;
            RebuildCoordinates();
        }

        /// <summary>Raised whenever state changed in a way that needs a repaint.</summary>
        public event EventHandler RepaintRequested;

        /// <summary>Raised when the user clicked the ruler to move the playhead.</summary>
        public event EventHandler<VerticalSeekEventArgs> SeekRequested;

        /// <summary>
        /// Raised when a chart edit made a vertical layout appear or disappear, so the shell
        /// can show or hide the strip instead of leaving an empty panel docked.
        /// </summary>
        public event EventHandler LayoutAvailabilityChanged;

        public EditorDocumentContext Document { get; private set; }

        /// <summary>Null when the chart has no gameplay notes to lay out.</summary>
        public VerticalTimelineProjection Projection { get; private set; }

        public bool HasLayout
        {
            get { return Projection != null; }
        }

        public VerticalTrackLayout Layout
        {
            get { return Projection == null ? null : Projection.Layout; }
        }

        /// <summary>4/5/6/8, or 0 when there is nothing to lay out.</summary>
        public int Mode
        {
            get { return Projection == null ? 0 : Projection.Mode; }
        }

        public VerticalCoordinateSystem Coordinates { get; private set; }

        /// <summary>Most recently built frame; hit testing runs against it.</summary>
        public VerticalTimelineFrame LastFrame { get; private set; }

        public int HeaderWidth { get; private set; }

        public int RulerHeight { get; private set; }

        /// <summary>The shared zoom scalar last accepted from a horizontal surface.</summary>
        public float ZoomFactor { get; private set; }

        public double PixelsPerTick
        {
            get { return _pixelsPerTick; }
        }

        public double ColumnScale
        {
            get { return _columnScale; }
            set
            {
                // Clamped rather than rejected: this is driven by a slider whose ends are
                // MinColumnScale/MaxColumnScale, and a silently-ignored set would let the thumb
                // sit somewhere the layout is not.
                double clamped = Math.Max(MinColumnScale, Math.Min(MaxColumnScale, value));
                if (double.IsNaN(clamped) || Math.Abs(clamped - _columnScale) < 0.0001)
                {
                    return;
                }
                _columnScale = clamped;
                RebuildCoordinates();
                RequestRepaint();
            }
        }

        public float DpiScale
        {
            get { return _dpiScale; }
            set
            {
                if (value <= 0 || _dpiScale == value) return;
                _dpiScale = value;
                RebuildCoordinates();
                RequestRepaint();
            }
        }

        /// <summary>When true, a resize refits the gameplay columns to the viewport width.</summary>
        public bool AutoFitColumns { get; set; }

        /// <summary>
        /// Which way the time axis runs. Defaults to <see cref="VerticalTimeDirection.Upward"/>
        /// so notes travel downward toward the bottom of the strip, matching the game and
        /// ptSequencer rather than a top-down spreadsheet.
        /// </summary>
        public VerticalTimeDirection TimeDirection
        {
            get { return _timeDirection; }
            set
            {
                if (_timeDirection == value) return;
                _timeDirection = value;
                RebuildCoordinates();
                RequestRepaint();
            }
        }

        public bool FollowPlayback { get; set; }

        public bool IsPlaybackActive { get; set; }

        public const double MinNoteThickness = 0.25;
        public const double MaxNoteThickness = 4.0;

        /// <summary>
        /// Visual thickness of a note head, as a multiplier of the floor height a zero-duration
        /// note is drawn with. Deliberately independent of <see cref="PixelsPerTick"/>: how tall
        /// a note head looks and how fast the chart scrolls past are two different preferences,
        /// and coupling them meant the "note height" slider was secretly the scroll-speed slider.
        /// Long notes keep their true duration in ticks - only the minimum head height scales.
        /// </summary>
        public double NoteThickness
        {
            get { return _noteThickness; }
            set
            {
                double clamped = Math.Max(MinNoteThickness, Math.Min(MaxNoteThickness, value));
                if (double.IsNaN(clamped) || Math.Abs(clamped - _noteThickness) < 0.0001)
                {
                    return;
                }
                _noteThickness = clamped;
                LastFrame = null;
                RequestRepaint();
            }
        }

        /// <summary>The floor height a zero-duration note is drawn with, after the thickness scale.</summary>
        public double MinimumNoteHeight
        {
            get { return VerticalTimelineFrame.MinimumItemHeight * _noteThickness; }
        }

        /// <summary>
        /// 0 = detect the layout from the chart; 4/5/6/8 forces a button preset,
        /// <see cref="VerticalTrackLayout.TechnikaMode"/> forces the TECHNIKA lane layout, and
        /// <see cref="VerticalTrackLayout.BmsMode"/> forces the BMS channel layout. Anything else
        /// falls back to detection rather than throwing, so a stale override cannot wedge the view.
        /// </summary>
        public int ModeOverride
        {
            get { return _modeOverride; }
            set
            {
                int next = VerticalTrackLayout.IsSupportedLayout(value) ? value : 0;
                if (_modeOverride == next) return;
                _modeOverride = next;
                Rebuild();
            }
        }

        /// <summary>Virtual tick drawn immediately below the column-name strip.</summary>
        public double OriginTick
        {
            get { return _originTick; }
            set
            {
                double next = ClampOriginTick(value);
                if (_originTick == next) return;
                _originTick = next;
                RequestRepaint();
            }
        }

        /// <summary>Native column offset drawn immediately right of the tick gutter.</summary>
        public double OriginNativeX
        {
            get { return _originNativeX; }
            set
            {
                double next = ClampOriginNativeX(value);
                if (_originNativeX == next) return;
                _originNativeX = next;
                RequestRepaint();
            }
        }

        /// <summary>
        /// Playhead position on the shared <see cref="EventData.VirtualTick"/> base, so
        /// the vertical playhead cannot drift from the horizontal surfaces or the audio.
        /// </summary>
        public int PlayheadVirtualTick
        {
            get { return _playheadVirtualTick; }
            set
            {
                int next = Math.Max(0, value);
                if (_playheadVirtualTick == next) return;
                _playheadVirtualTick = next;
                FollowPlayhead();
                RequestRepaint();
            }
        }

        /// <summary>Last tick of the chart, used to clamp scrolling.</summary>
        public int DocumentEndTick { get; private set; }

        /// <summary>Ticks that fit between the ruler and the bottom edge of the last frame.</summary>
        public double VisibleTickCount
        {
            get
            {
                if (_lastHeight <= 0) return 0;
                return Math.Max(0, _lastHeight - Coordinates.RulerHeight) /
                    Coordinates.PixelsPerTick;
            }
        }

        /// <summary>Highest origin tick that still shows chart content.</summary>
        public double MaxOriginTick
        {
            get { return Math.Max(0, DocumentEndTick - (VisibleTickCount / 2.0)); }
        }

        /// <summary>
        /// Highest <see cref="OriginNativeX"/> that still shows content, i.e. the offset at
        /// which the rightmost column's edge lines up with the right edge of the panel.
        /// </summary>
        /// <remarks>
        /// Public because the column scrollbar needs the same number the clamp uses; a
        /// scrollbar whose maximum disagreed with the clamp would leave dead travel at one
        /// end or make the last columns unreachable at the other.
        /// </remarks>
        public double MaxOriginNativeX
        {
            get
            {
                if (Layout == null) return 0;
                double visibleNative = _lastWidth > 0
                    ? Math.Max(0, _lastWidth - Coordinates.HeaderWidth) / Coordinates.ColumnScale
                    : 0;
                return Math.Max(0, Layout.NativeWidth - visibleNative);
            }
        }

        public void Bind(EditorDocumentContext document)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (object.ReferenceEquals(Document, document))
            {
                Rebuild();
                return;
            }

            Detach();
            Document = document;
            Document.Selection.SelectionChanged += DocumentSelectionChanged;
            Document.UndoManager.OnUndoRedo += DocumentMutated;
            _playheadVirtualTick = document.Model.VirtualCurrentTick;
            _originTick = 0;
            _originNativeX = 0;
            CacheSelection();
            Rebuild();
        }

        public void Unbind()
        {
            Detach();
            bool had = Projection != null;
            Document = null;
            Projection = null;
            LastFrame = null;
            _selected.Clear();
            DocumentEndTick = 0;
            if (had) RaiseLayoutAvailabilityChanged();
            RequestRepaint();
        }

        /// <summary>
        /// Re-projects the bound chart, preserving scroll and playhead. Called whenever
        /// the model is mutated through the undo manager, i.e. after every edit.
        /// </summary>
        public void Rebuild()
        {
            bool had = Projection != null;
            if (Document == null)
            {
                Projection = null;
                LastFrame = null;
                DocumentEndTick = 0;
                if (had) RaiseLayoutAvailabilityChanged();
                return;
            }

            Projection = _modeOverride == 0
                ? VerticalTimelineProjection.Project(Document.Model)
                : VerticalTimelineProjection.Project(Document.Model, _modeOverride);
            DocumentEndTick = ComputeDocumentEndTick();
            _originTick = ClampOriginTick(_originTick);
            _originNativeX = ClampOriginNativeX(_originNativeX);
            LastFrame = null;
            if (had != (Projection != null))
            {
                RaiseLayoutAvailabilityChanged();
            }
            RequestRepaint();
        }

        public VerticalTimelineFrame BuildFrame(int width, int height)
        {
            if (Projection == null) return null;

            int safeWidth = Math.Max(1, width);
            int safeHeight = Math.Max(1, height);
            bool heightChanged = safeHeight != _lastHeight;
            _lastWidth = safeWidth;
            _lastHeight = safeHeight;
            if (heightChanged)
            {
                // Upward time is measured up from the bottom of the body, so the coordinate
                // system has to be told how tall the body is; a resize moves that datum.
                RebuildCoordinates();
            }
            if (AutoFitColumns)
            {
                FitColumns(_lastWidth);
            }
            _originTick = ClampOriginTick(_originTick);
            _originNativeX = ClampOriginNativeX(_originNativeX);

            LastFrame = VerticalTimelineFrame.Build(
                Projection,
                Coordinates,
                _originTick,
                _originNativeX,
                _lastWidth,
                _lastHeight,
                _playheadVirtualTick,
                _noteThickness);
            return LastFrame;
        }

        public VerticalHitResult HitTest(double x, double y)
        {
            return LastFrame == null ? null : LastFrame.HitTest(x, y);
        }

        /// <summary>
        /// Resizes the two chrome gutters, in unscaled DIPs.
        /// <para>
        /// Defaults to <see cref="DefaultHeaderWidth"/>/<see cref="DefaultRulerHeight"/> and no
        /// caller has to touch it. The Studio's horizontal orientation does: it reflects this
        /// surface across its diagonal, so on screen the left gutter is what you read as the time
        /// ruler across the top and the top strip is the track-name column down the left. For
        /// either one to be the right thickness the two have to trade places, and doing it here
        /// keeps the frame, hit testing and <c>OriginY</c> in agreement with what is drawn - a
        /// canvas that quietly used different numbers would put every note one gutter off.
        /// </para>
        /// </summary>
        public void SetChromeSizes(int headerWidth, int rulerHeight)
        {
            int header = Math.Max(0, headerWidth);
            int ruler = Math.Max(0, rulerHeight);
            if (header == HeaderWidth && ruler == RulerHeight) return;

            HeaderWidth = header;
            RulerHeight = ruler;
            RebuildCoordinates();
            _originTick = ClampOriginTick(_originTick);
            _originNativeX = ClampOriginNativeX(_originNativeX);
            RequestRepaint();
        }

        /// <summary>Identity test against the shared selection, cached for paint-time use.</summary>
        public bool IsSelected(EventData sourceEvent)
        {
            return sourceEvent != null && _selected.Contains(sourceEvent);
        }

        public int SelectedCount
        {
            get { return _selected.Count; }
        }

        /// <summary>
        /// Applies a click to the shared selection: an item replaces the selection (or
        /// toggles it when <paramref name="additive"/>), and a plain click on empty chart
        /// space clears it, matching the V1 editor's gesture. Returns true when the shared
        /// selection changed, which is what drives every other surface to repaint.
        /// </summary>
        public bool SelectAt(double x, double y, bool additive)
        {
            if (Document == null || LastFrame == null) return false;

            VerticalHitResult hit = LastFrame.HitTest(x, y);
            if (hit.HasItem)
            {
                EventData target = hit.Item.Item.SourceEvent;
                if (!additive)
                {
                    Document.Selection.Replace(new[] { target });
                    return true;
                }

                var next = new List<EventData>(Document.Selection.Items);
                if (!next.Remove(target))
                {
                    next.Add(target);
                }
                Document.Selection.Replace(next);
                return true;
            }

            if (additive || !hit.HasColumn || Document.Selection.Count == 0)
            {
                return false;
            }
            Document.Selection.Clear();
            return true;
        }

        /// <summary>
        /// Requests a playhead move for a click inside the ruler strip. The tick is on the
        /// shared virtual-tick base so the shell can drive audio and both horizontal
        /// surfaces from it unchanged.
        /// </summary>
        public bool SeekAt(double y)
        {
            if (Projection == null) return false;

            return SeekToTick(Coordinates.YToTick(y, _originTick));
        }

        /// <summary>
        /// Requests a playhead move to a virtual tick directly - the wheel scrub's version of a
        /// ruler click. Same contract as <see cref="SeekAt(double)"/>: clamped into the chart on
        /// the shared virtual-tick base, and announced through <see cref="SeekRequested"/> so the
        /// shell drives audio and the other surfaces from it unchanged.
        /// </summary>
        public bool SeekToTick(int virtualTick)
        {
            if (Projection == null) return false;

            int tick = Math.Max(0, virtualTick);
            if (DocumentEndTick > 0)
            {
                tick = Math.Min(tick, DocumentEndTick);
            }
            PlayheadVirtualTick = tick;
            if (SeekRequested != null)
            {
                SeekRequested(this, new VerticalSeekEventArgs(tick));
            }
            return true;
        }

        /// <summary>
        /// Pans the origin just enough that the playhead is inside the visible window, and does
        /// nothing when it already is. The wheel scrub's companion: a seek that walks off the
        /// edge of the screen has to pull the view after it whether or not playback-follow is
        /// on, or scrubbing reads as if it did nothing once the playhead leaves the window.
        /// </summary>
        public void RevealPlayhead()
        {
            if (Projection == null)
            {
                return;
            }

            double visible = VisibleTickCount;
            if (visible <= 0)
            {
                return;
            }

            double lead = visible * 0.15;
            if (_playheadVirtualTick < _originTick + lead)
            {
                OriginTick = _playheadVirtualTick - lead;
            }
            else if (_playheadVirtualTick > _originTick + visible - lead)
            {
                OriginTick = _playheadVirtualTick - visible + lead;
            }
        }

        /// <summary>
        /// Accepts the shared zoom scalar from a horizontal surface. Vertical density is
        /// <see cref="BasePixelsPerTick"/> times that scalar, so one zoom gesture scales
        /// both surfaces by the same factor. The top tick stays put, matching
        /// <c>EditorControl.SetZoom</c>.
        /// </summary>
        public bool TrySetTimeZoom(float zoom)
        {
            if (zoom <= 0 || float.IsNaN(zoom) || float.IsInfinity(zoom))
            {
                return false;
            }

            SetPixelsPerTick(zoom * BasePixelsPerTick);
            // Report the density that was actually accepted, not the requested scalar, so a
            // clamped zoom cannot be captured into view state and replayed as a larger one.
            ZoomFactor = (float)(_pixelsPerTick / BasePixelsPerTick);
            return true;
        }

        /// <summary>Wheel zoom that keeps the tick under <paramref name="y"/> fixed.</summary>
        public void ZoomAt(double y, double factor)
        {
            if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor)) return;

            double anchorTick = TickAtExactY(y);
            double previous = _pixelsPerTick;
            SetPixelsPerTick(_pixelsPerTick * factor);
            if (_pixelsPerTick == previous) return;

            ZoomFactor = (float)(_pixelsPerTick / BasePixelsPerTick);
            // Keep the anchored tick exactly under the cursor by inverting TickAtExactY
            // against the *new* density. Coordinates has already been rebuilt by
            // SetPixelsPerTick, so OriginY and PixelsPerTick below are the post-zoom values.
            OriginTick = anchorTick - PixelOffsetFromOrigin(y);
        }

        /// <summary>Signed tick distance from the origin datum to a Y, in the current direction.</summary>
        private double PixelOffsetFromOrigin(double y)
        {
            double offset = _timeDirection == VerticalTimeDirection.Upward
                ? Coordinates.OriginY - y
                : y - Coordinates.OriginY;
            return offset / Coordinates.PixelsPerTick;
        }

        public void ScrollByTicks(double deltaTicks)
        {
            OriginTick = _originTick + deltaTicks;
        }

        public void ScrollByPixels(double deltaPixels)
        {
            OriginTick = _originTick + (deltaPixels / Coordinates.PixelsPerTick);
        }

        /// <summary>
        /// Scrolls by a screen-space gesture expressed as "the content should move this many
        /// pixels down" — a wheel notch or a drag. Whether that means earlier or later ticks
        /// depends on <see cref="TimeDirection"/>, so input handlers call this and stay
        /// direction-agnostic; <see cref="ScrollByPixels"/> stays in tick space.
        /// </summary>
        public void ScrollByScreenDelta(double contentPixelsDown)
        {
            double sign = _timeDirection == VerticalTimeDirection.Upward ? 1.0 : -1.0;
            ScrollByPixels(sign * contentPixelsDown);
        }

        public void ScrollByNativeX(double deltaNativeX)
        {
            OriginNativeX = _originNativeX + deltaNativeX;
        }

        /// <summary>Centres the view on a tick, clamped to the chart.</summary>
        public void ScrollToTick(double tick)
        {
            OriginTick = tick - (VisibleTickCount / 2.0);
        }

        /// <summary>
        /// Scales columns so the gameplay span fits <paramref name="viewportWidth"/>.
        /// Returns true when the scale changed. Utility/BG columns stay reachable by
        /// horizontal scrolling rather than being squeezed below legibility.
        /// </summary>
        public bool FitColumns(int viewportWidth)
        {
            if (Layout == null || Layout.GameplayNativeWidth <= 0) return false;

            double available = Math.Max(1, viewportWidth - Coordinates.HeaderWidth);
            double desired = available / (Layout.GameplayNativeWidth * Math.Max(0.01f, _dpiScale));
            double clamped = Math.Max(MinColumnScale, Math.Min(MaxColumnScale, desired));
            if (Math.Abs(clamped - _columnScale) < 0.0001) return false;

            _columnScale = clamped;
            RebuildCoordinates();
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
            Document = null;
            Projection = null;
            LastFrame = null;
        }

        private void SetPixelsPerTick(double value)
        {
            double clamped = Math.Max(MinPixelsPerTick, Math.Min(MaxPixelsPerTick, value));
            if (Math.Abs(clamped - _pixelsPerTick) < 1e-9) return;

            _pixelsPerTick = clamped;
            RebuildCoordinates();
            _originTick = ClampOriginTick(_originTick);
            RequestRepaint();
        }

        private void RebuildCoordinates()
        {
            int scaledRuler = VerticalCoordinateSystem.ScaleToDevice(RulerHeight, _dpiScale);
            int bodyHeight = _lastHeight > 0 ? Math.Max(0, _lastHeight - scaledRuler) : 0;
            Coordinates = new VerticalCoordinateSystem(
                _pixelsPerTick,
                _columnScale,
                HeaderWidth,
                RulerHeight,
                _dpiScale,
                _timeDirection,
                bodyHeight);
        }

        /// <summary>
        /// Tick under an exact Y, without the rounding <c>YToTick</c> applies. Anchoring a
        /// zoom needs the fractional tick or the view creeps a little on every wheel notch.
        /// </summary>
        private double TickAtExactY(double y)
        {
            double offset = _timeDirection == VerticalTimeDirection.Upward
                ? Coordinates.OriginY - y
                : y - Coordinates.OriginY;
            return _originTick + (offset / Coordinates.PixelsPerTick);
        }

        private void FollowPlayhead()
        {
            double visible = VisibleTickCount;

            if (IsPlaybackActive)
            {
                if (!FollowPlayback) return;
                double lead = visible > 0 ? visible / 3.0 : 150.0;
                double target = _playheadVirtualTick - lead;
                _originTick = ClampOriginTick(target);
                return;
            }

            // A seek while stopped still has to bring the playhead on screen, and this is
            // unconditional on purpose: FollowPlayback is the user's "follow while playing"
            // preference, whereas a seek is an explicit navigation command. Without this the
            // strip stayed wherever it was last scrolled - which is tick 0 for a freshly
            // opened chart, where most charts have nothing to draw, so every navigation
            // except live playback left an empty grid on screen.
            if (visible <= 0) return;

            // Scroll into view, not re-centre on every nudge: an author stepping the
            // playhead a few ticks must not have the strip jump under their cursor.
            double margin = visible / 8.0;
            if (_playheadVirtualTick >= _originTick + margin &&
                _playheadVirtualTick <= _originTick + visible - margin)
            {
                return;
            }
            _originTick = ClampOriginTick(_playheadVirtualTick - (visible / 2.0));
        }

        private double ClampOriginTick(double value)
        {
            if (double.IsNaN(value)) return 0;
            double maximum = MaxOriginTick;
            if (value < 0) return 0;
            return value > maximum ? maximum : value;
        }

        private double ClampOriginNativeX(double value)
        {
            if (double.IsNaN(value) || Layout == null || value < 0) return 0;

            double maximum = MaxOriginNativeX;
            return value > maximum ? maximum : value;
        }

        private int ComputeDocumentEndTick()
        {
            int end = Document.Model.VirtualMaxTick;
            if (Projection != null)
            {
                foreach (TimelineItem item in Projection.Items)
                {
                    if (item.EndTick > end) end = item.EndTick;
                }
            }
            return Math.Max(0, end);
        }

        private void CacheSelection()
        {
            _selected.Clear();
            if (Document == null) return;
            foreach (EventData item in Document.Selection.Items)
            {
                _selected.Add(item);
            }
        }

        private void DocumentSelectionChanged(object sender, EventArgs e)
        {
            CacheSelection();
            RequestRepaint();
        }

        private void DocumentMutated(object sender, UndoManager.Action action)
        {
            // Every chart mutation - create, move, resize, delete, undo, redo - reaches
            // the model through UndoManager, so this is the single authoritative signal
            // that the vertical projection is stale.
            Rebuild();
        }

        private void Detach()
        {
            if (Document == null) return;
            Document.Selection.SelectionChanged -= DocumentSelectionChanged;
            Document.UndoManager.OnUndoRedo -= DocumentMutated;
        }

        private void RequestRepaint()
        {
            if (RepaintRequested != null)
            {
                RepaintRequested(this, EventArgs.Empty);
            }
        }

        private void RaiseLayoutAvailabilityChanged()
        {
            if (LayoutAvailabilityChanged != null)
            {
                LayoutAvailabilityChanged(this, EventArgs.Empty);
            }
        }
    }
}
