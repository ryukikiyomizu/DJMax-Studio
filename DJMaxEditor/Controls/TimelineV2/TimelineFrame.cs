using System;
using System.Collections.Generic;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.Controls.Editor.Renderers.Zones;

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
            EventDisplayMode eventDisplayMode = EventDisplayMode.Attribute)
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

        public int CanvasBottom
        {
            get { return Math.Max(Coordinates.RulerHeight, Height - MinimapHeight); }
        }
    }
}
