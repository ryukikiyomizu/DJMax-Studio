using System;
using System.Collections.Generic;
using System.Drawing;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.Controls.Editor.Renderers.Zones;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.TimelineV2
{
    public sealed class TimelineFrame
    {
        public TimelineFrame(
            int width,
            int height,
            TimelineCoordinateSystem coordinates,
            TimelineViewport viewport,
            IReadOnlyList<TimelineRow> rows,
            IReadOnlyList<TimelineItem> visibleItems,
            int firstVisibleRow,
            int playheadTick,
            bool isReadOnly,
            string lockLabel,
            string statusText,
            int ticksPerMeasure,
            int beatsPerMeasure,
            IReadOnlyList<int> minimapDensity,
            int quantizeDivision = 8,
            IEventRenderer eventTheme = null,
            IZoneRenderer zoneTheme = null,
            EventDisplayMode eventDisplayMode = EventDisplayMode.None,
            ISet<EventData> selection = null,
            Rectangle? marquee = null,
            int scrollBandMargin = 0,
            double bandOriginTick = 0)
        {
            if (coordinates == null) throw new ArgumentNullException("coordinates");
            if (viewport == null) throw new ArgumentNullException("viewport");

            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            Coordinates = coordinates;
            Viewport = viewport;
            Rows = rows ?? new TimelineRow[0];
            VisibleItems = visibleItems ?? new TimelineItem[0];
            FirstVisibleRow = Math.Max(0, firstVisibleRow);
            PlayheadTick = playheadTick;
            IsReadOnly = isReadOnly;
            LockLabel = lockLabel ?? string.Empty;
            StatusText = statusText ?? string.Empty;
            TicksPerMeasure = ticksPerMeasure;
            BeatsPerMeasure = beatsPerMeasure;
            MinimapDensity = minimapDensity ?? new int[0];
            QuantizeDivision = Math.Max(1, quantizeDivision);
            EventTheme = eventTheme;
            ZoneTheme = zoneTheme;
            EventDisplayMode = eventDisplayMode;
            Selection = selection;
            Marquee = marquee;
            ScrollBandMargin = Math.Max(0, scrollBandMargin);
            BandOriginTick = bandOriginTick;
        }

        public const int MinimapHeight = 48;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public TimelineCoordinateSystem Coordinates { get; private set; }
        public TimelineViewport Viewport { get; private set; }
        public IReadOnlyList<TimelineRow> Rows { get; private set; }
        public IReadOnlyList<TimelineItem> VisibleItems { get; private set; }
        public int FirstVisibleRow { get; private set; }
        public int PlayheadTick { get; private set; }
        public bool IsReadOnly { get; private set; }
        public string LockLabel { get; private set; }
        public string StatusText { get; private set; }
        public int TicksPerMeasure { get; private set; }
        public int BeatsPerMeasure { get; private set; }
        public IReadOnlyList<int> MinimapDensity { get; private set; }
        public int QuantizeDivision { get; private set; }
        public IEventRenderer EventTheme { get; private set; }
        public IZoneRenderer ZoneTheme { get; private set; }
        public EventDisplayMode EventDisplayMode { get; private set; }

        /// <summary>
        /// The document's live selection, by reference identity, or null when the surface is
        /// not participating in selection. A set rather than a list because the renderer asks
        /// once per visible item and a dense frame has thousands of them.
        /// </summary>
        public ISet<EventData> Selection { get; private set; }

        /// <summary>The in-progress marquee rectangle in device pixels, if one is being dragged.</summary>
        public Rectangle? Marquee { get; private set; }

        /// <summary>
        /// Pixels of chart cached either side of the viewport so a scrolling origin is a blit rather
        /// than a re-render. Zero disables the band and the renderer falls back to caching exactly
        /// one screen, which is what every frame built directly by a test does.
        /// </summary>
        /// <remarks>
        /// Continuous follow-scroll is the reason this exists. The renderer's cache used to be keyed
        /// on the viewport origin, so a playhead that dragged the view along re-rendered every
        /// visible note and label per frame. Widening the cached area and moving the *read* window
        /// instead turns 60 rebuilds a second into roughly one per <see cref="ScrollBandMargin"/>
        /// pixels of travel.
        /// </remarks>
        public int ScrollBandMargin { get; private set; }

        /// <summary>
        /// Viewport origin the band was projected at, in virtual ticks. Normally
        /// <c>Viewport.OriginTick - ScrollBandMargin / PixelsPerTick</c>; the owning control holds it
        /// steady while scrolling so both <see cref="VisibleItems"/> and the cache key stay put.
        /// </summary>
        public double BandOriginTick { get; private set; }

        public bool IsSelected(EventData sourceEvent)
        {
            return sourceEvent != null && Selection != null && Selection.Contains(sourceEvent);
        }

        public int SelectionCount
        {
            get { return Selection == null ? 0 : Selection.Count; }
        }

        public int CanvasBottom
        {
            get { return Math.Max(Coordinates.RulerHeight, Height - MinimapHeight); }
        }
    }
}
