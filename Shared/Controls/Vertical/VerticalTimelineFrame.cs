using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DJMaxEditor.Controls.TimelineV2;

namespace DJMaxEditor.Controls.Vertical
{
    /// <summary>One projected event placed in a vertical column, in device pixels.</summary>
    public sealed class VerticalPlacedItem
    {
        public VerticalPlacedItem(
            VerticalColumn column,
            TimelineItem item,
            double left,
            double top,
            double width,
            double height)
        {
            if (column == null) throw new ArgumentNullException("column");
            if (item == null) throw new ArgumentNullException("item");

            Column = column;
            Item = item;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public VerticalColumn Column { get; private set; }

        public TimelineItem Item { get; private set; }

        public double Left { get; private set; }

        public double Top { get; private set; }

        public double Width { get; private set; }

        public double Height { get; private set; }

        public double Right { get { return Left + Width; } }

        public double Bottom { get { return Top + Height; } }

        /// <summary>Half-open on the right/bottom edges so adjacent items never both hit.</summary>
        public bool Contains(double x, double y)
        {
            return x >= Left && x < Right && y >= Top && y < Bottom;
        }
    }

    /// <summary>What sits under a pointer on the vertical surface.</summary>
    public sealed class VerticalHitResult
    {
        public VerticalHitResult(VerticalColumn column, int tick, VerticalPlacedItem item)
        {
            Column = column;
            Tick = tick;
            Item = item;
        }

        /// <summary>The column under the pointer, or null outside every column.</summary>
        public VerticalColumn Column { get; private set; }

        /// <summary>The virtual tick under the pointer.</summary>
        public int Tick { get; private set; }

        /// <summary>The topmost placed item under the pointer, or null.</summary>
        public VerticalPlacedItem Item { get; private set; }

        public bool HasColumn { get { return Column != null; } }

        public bool HasItem { get { return Item != null; } }
    }

    /// <summary>
    /// An immutable, already-positioned vertical frame: the visible tick window's
    /// events resolved to device-pixel rectangles, plus the playhead. Building a
    /// frame performs one windowed index query, so cost scales with visible items
    /// rather than chart size — the same property the horizontal V2 surface has.
    /// </summary>
    public sealed class VerticalTimelineFrame
    {
        /// <summary>
        /// Device-pixel floor for an item's height so zero-duration taps stay
        /// visible and clickable at any zoom, matching ptSequencer's minimum bar.
        /// </summary>
        /// <remarks>
        /// One floor for every column, turntables included. A BMS scratch is told apart from a key by
        /// the width of the column it sits in, which is the layout's business - see
        /// <see cref="VerticalColumn.IsScratch"/>. It was briefly a taller bar here instead, and that
        /// was the wrong axis twice over: a scratch that really is a long note has to draw its own
        /// duration, and a height difference is invisible at the zoom a chart is edited at anyway,
        /// because a BMS object already spans 36 virtual ticks and clears every floor.
        /// </remarks>
        public const double MinimumItemHeight = 3.0;

        /// <summary>
        /// Longest time span (in virtual ticks) that still draws as a note *head* rather than
        /// a duration-bearing body. A tap placed by the editor carries the event default of six
        /// ticks (36 virtual ticks) even though it has no musical length, and every imported
        /// head note is at or below it; anything longer is a real hold/drag/roll.
        /// </summary>
        public const double HeadSpanVirtualTicks = 36.0;

        /// <summary>
        /// Constant screen thickness of a head note at the neutral note-height setting,
        /// independent of the zoom. It equals what a default tap spanned at zoom 1.0
        /// (36 virtual ticks at <see cref="VerticalTimelineViewModel.BasePixelsPerTick"/>),
        /// which freezes the shipped look while taking the head off the time map - moving
        /// the note-speed (zoom) slider changes spacing, never head thickness.
        /// </summary>
        public const double DefaultHeadHeight =
            HeadSpanVirtualTicks * VerticalTimelineViewModel.BasePixelsPerTick;

        private readonly ReadOnlyCollection<VerticalPlacedItem> _items;

        private VerticalTimelineFrame(
            VerticalTimelineProjection projection,
            VerticalCoordinateSystem coordinates,
            double originTick,
            double originNativeX,
            int width,
            int height,
            int playheadTick,
            int firstVisibleColumn,
            int lastVisibleColumn,
            IList<VerticalPlacedItem> items)
        {
            Projection = projection;
            Coordinates = coordinates;
            OriginTick = originTick;
            OriginNativeX = originNativeX;
            Width = width;
            Height = height;
            PlayheadTick = playheadTick;
            FirstVisibleColumn = firstVisibleColumn;
            LastVisibleColumn = lastVisibleColumn;
            _items = new ReadOnlyCollection<VerticalPlacedItem>(items);
        }

        public VerticalTimelineProjection Projection { get; private set; }

        public VerticalCoordinateSystem Coordinates { get; private set; }

        public VerticalTrackLayout Layout { get { return Projection.Layout; } }

        /// <summary>Virtual tick drawn immediately under the top ruler.</summary>
        public double OriginTick { get; private set; }

        /// <summary>Native column offset drawn immediately right of the left header.</summary>
        public double OriginNativeX { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public int PlayheadTick { get; private set; }

        public int FirstVisibleColumn { get; private set; }

        public int LastVisibleColumn { get; private set; }

        public ReadOnlyCollection<VerticalPlacedItem> Items { get { return _items; } }

        /// <summary>Playhead Y in device pixels; may fall outside the canvas when scrolled away.</summary>
        public double PlayheadY
        {
            get { return Coordinates.TickToY(PlayheadTick, OriginTick); }
        }

        /// <summary>
        /// Last visible tick, i.e. the tick at the far edge of the canvas. Which physical
        /// edge that is depends on <see cref="VerticalCoordinateSystem.TimeDirection"/>, but
        /// the tick span itself is the same either way, so this stays direction-neutral.
        /// </summary>
        public double LastVisibleTick
        {
            get { return OriginTick + ((Height - Coordinates.RulerHeight) / Coordinates.PixelsPerTick); }
        }

        /// <param name="noteThickness">
        /// Scale applied to <see cref="MinimumItemHeight"/> only - the shell's "note height"
        /// preference. Kept out of the tick-to-pixel map on purpose: it changes how tall a note
        /// head looks, never how far apart two ticks sit, so a note's timing read cannot be
        /// distorted by its own styling.
        /// </param>
        public static VerticalTimelineFrame Build(
            VerticalTimelineProjection projection,
            VerticalCoordinateSystem coordinates,
            double originTick,
            double originNativeX,
            int width,
            int height,
            int playheadTick,
            double noteThickness = 1.0)
        {
            if (projection == null) throw new ArgumentNullException("projection");
            if (coordinates == null) throw new ArgumentNullException("coordinates");

            if (double.IsNaN(noteThickness) || double.IsInfinity(noteThickness) || noteThickness <= 0)
            {
                noteThickness = 1.0;
            }
            double minimumItemHeight = MinimumItemHeight * noteThickness;

            int safeWidth = Math.Max(1, width);
            int safeHeight = Math.Max(1, height);

            // Visible tick window: from the ruler edge down to the canvas bottom.
            double visibleTicks =
                Math.Max(0, safeHeight - coordinates.RulerHeight) / coordinates.PixelsPerTick;
            double endTick = originTick + visibleTicks;

            // Visible column span: from the header edge right to the canvas edge.
            double visibleNativeWidth =
                Math.Max(0, safeWidth - coordinates.HeaderWidth) / coordinates.ColumnScale;
            double endNativeX = originNativeX + visibleNativeWidth;

            int firstColumn = -1;
            int lastColumn = -1;
            foreach (VerticalColumn column in projection.Layout.Columns)
            {
                if (column.NativeRight <= originNativeX || column.NativeLeft >= endNativeX)
                {
                    continue;
                }
                if (firstColumn < 0) firstColumn = column.Index;
                lastColumn = column.Index;
            }

            var placed = new List<VerticalPlacedItem>();
            if (firstColumn >= 0)
            {
                IReadOnlyList<TimelineItem> visible =
                    projection.Query(originTick, endTick, firstColumn, lastColumn);
                foreach (TimelineItem item in visible)
                {
                    VerticalColumn column = projection.Layout.Columns[item.RowIndex];
                    double startY = coordinates.TickToY(item.StartTick, originTick);
                    double endY = coordinates.TickToY(item.EndTick, originTick);

                    // Head notes draw a *constant* screen thickness so note speed - the time
                    // zoom - cannot change how tall a tap looks: zoom stretches the gaps
                    // between notes, while the head stays put. Only real duration-bearing
                    // events follow the tick map, and even those keep the minimum floor when
                    // zoomed far out. The note-height preference scales the head.
                    double spanTicks = Math.Abs(item.EndTick - item.StartTick);
                    double itemHeight;
                    if (spanTicks <= HeadSpanVirtualTicks)
                    {
                        itemHeight = DefaultHeadHeight * noteThickness;
                    }
                    else
                    {
                        itemHeight = Math.Max(minimumItemHeight, Math.Abs(endY - startY));
                    }
                    // The note head is always at StartTick, so upward time puts it at the
                    // *bottom* edge of the bar and the sustain grows above it. Taking a plain
                    // Min here would instead anchor a zero-duration tap's 3px bar below its
                    // own head, and the clamped span would drift off the grid line.
                    double top = coordinates.TimeDirection == VerticalTimeDirection.Upward
                        ? startY - itemHeight
                        : startY;
                    placed.Add(new VerticalPlacedItem(
                        column,
                        item,
                        coordinates.NativeXToScreen(column.NativeLeft, originNativeX),
                        top,
                        column.Width * coordinates.ColumnScale,
                        itemHeight));
                }
            }

            return new VerticalTimelineFrame(
                projection,
                coordinates,
                originTick,
                originNativeX,
                safeWidth,
                safeHeight,
                playheadTick,
                firstColumn,
                lastColumn,
                placed);
        }

        /// <summary>
        /// Resolves a pointer position to a column, a virtual tick, and the topmost
        /// item under it. Later-placed items win, matching paint order.
        /// </summary>
        public VerticalHitResult HitTest(double x, double y)
        {
            double nativeX = Coordinates.ScreenXToNative(x, OriginNativeX);
            VerticalColumn column = x < Coordinates.HeaderWidth
                ? null
                : Layout.ColumnAtNativeX(nativeX);
            int tick = Coordinates.YToTick(y, OriginTick);

            bool onsetAtBottom =
                Coordinates.TimeDirection == VerticalTimeDirection.Upward;
            VerticalPlacedItem hit = null;
            if (y >= Coordinates.RulerHeight)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (HitContainsNote(_items[i], x, y, onsetAtBottom))
                    {
                        hit = _items[i];
                        break;
                    }
                }
            }

            return new VerticalHitResult(column, tick, hit);
        }

        /// <summary>
        /// Whether a pointer lands on a note for interaction purposes. The placement rect is
        /// the note's *timing* band - at normal zoom only a sliver along the time axis - while
        /// the drawn head glyph is roughly as tall as the lane is wide (a square arcade sheet
        /// scaled to the lane) and half of it overhangs the onset edge. Testing the sliver alone
        /// made the visible middle of every head unclickable, so a note could not be pressed or
        /// dragged even though it was clearly under the pointer. The hit band grows along time
        /// by half a lane width past the onset edge, which is the glyph's own extent; the lane
        /// axis and marquee geometry keep using the exact placement rect.
        /// </summary>
        private static bool HitContainsNote(
            VerticalPlacedItem placed, double x, double y, bool onsetAtBottom)
        {
            if (x < placed.Left || x >= placed.Right)
            {
                return false;
            }

            if (y >= placed.Top && y < placed.Bottom)
            {
                return true;
            }

            // Only the onset side overhangs: downward time puts the onset at the cell top,
            // upward time at the bottom.
            double overhang = placed.Width * 0.5;
            if (onsetAtBottom)
            {
                return y >= placed.Bottom && y < placed.Bottom + overhang;
            }
            return y >= placed.Top - overhang && y < placed.Top;
        }
    }
}
