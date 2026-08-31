using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;

namespace DJMaxEditor.Controls.TimelineV2
{
    /// <summary>
    /// Timeline V2's pointer editing model.
    /// <para>
    /// V2 shipped read-only behind a feature flag and deliberately registered no mutation. The
    /// playtest asked for it to become a real editor, so this half of the control adds the same
    /// gesture vocabulary V1 has — click to select, shift/ctrl to extend, drag to marquee, drag a
    /// selected note to move it, Draw to create, Erase to delete, Resize to stretch a sustain —
    /// and routes every mutation through <see cref="ChartEditController"/>. Nothing here touches
    /// the model directly: capability gating, undo grouping and the shared selection all come from
    /// the controller, which is why V1 and V2 cannot drift apart.
    /// </para>
    /// </summary>
    public sealed partial class TimelineV2Control
    {
        /// <summary>
        /// Click slack around a note, in pixels. A tap note is a 7px glyph, so an exact-pixel hit
        /// test would make it near-unclickable; this widens the target without letting one click
        /// claim two adjacent notes at normal zoom.
        /// </summary>
        private const int HitSlackPixels = 5;

        /// <summary>Pointer travel, in pixels, before a press becomes a drag.</summary>
        private const int DragThresholdPixels = 3;

        private object _moveUndoGroup;
        private object _resizeUndoGroup;
        private int _gestureLastTick;
        private int _gestureLastRow;
        private Point _gestureOrigin;
        private Rectangle _marquee = Rectangle.Empty;
        private bool _isMarqueeActive;
        private bool _isMovingSelection;
        private bool _gestureExceededThreshold;

        /// <summary>
        /// Prototype the Draw tool clones. Same contract as <c>EditorControl.TemplateEvent</c>;
        /// the shell assigns both from the Notes palette.
        /// </summary>
        public EventData TemplateEvent { get; set; }

        /// <summary>The live rubber band, empty when no marquee is in progress.</summary>
        internal Rectangle ActiveMarquee
        {
            get { return _isMarqueeActive ? _marquee : Rectangle.Empty; }
        }

        /// <summary>True when the bound document permits mutation.</summary>
        public bool CanMutateDocument
        {
            get { return Document != null && Document.Capabilities.CanEdit; }
        }

        private TimelineTool ActiveTool
        {
            get
            {
                return Document == null
                    ? TimelineTool.Select
                    : Document.Interaction.Tool;
            }
        }

        /// <summary>
        /// The note under a client-space point, or null. Hit testing is done in pixels rather than
        /// ticks so it matches what <see cref="Renderers.ItemRenderer"/> actually painted: a note's
        /// target is its glyph plus <see cref="HitSlackPixels"/>, extended over its sustain bar.
        /// </summary>
        internal TimelineItem HitTest(int x, int y)
        {
            if (_index == null || _viewport == null || _projectionResult == null)
            {
                return null;
            }
            if (x < _coordinates.HeaderWidth || y < _coordinates.RulerHeight)
            {
                return null;
            }
            int canvasBottom = Math.Max(
                _coordinates.RulerHeight, Height - TimelineFrame.MinimapHeight);
            if (y >= canvasBottom)
            {
                return null;
            }

            int row = _coordinates.YToRow(y, _firstVisibleRow);
            if (row < 0 || row >= _projectionResult.Rows.Count)
            {
                return null;
            }

            double slackTicks = HitSlackPixels / Math.Max(1e-6, _coordinates.PixelsPerTick);
            double centreTick = _viewport.TickAtScreenX(x);
            IReadOnlyList<TimelineItem> candidates = _index.Query(
                new TimelineTimeRange(centreTick - slackTicks, centreTick + slackTicks),
                new TimelineRowRange(row, row),
                Math.Max(96, TicksPerMeasure),
                0);

            TimelineItem best = null;
            double bestDistance = double.MaxValue;
            foreach (TimelineItem candidate in candidates)
            {
                EventData source = candidate.SourceEvent;
                int startTick = source == null ? candidate.StartTick : source.VirtualTick;
                int endTick = source == null
                    ? candidate.EndTick
                    : source.VirtualTick + source.VirtualDuration;

                double startX = _viewport.ScreenXAtTick(startTick);
                double endX = _viewport.ScreenXAtTick(endTick);
                if (x < startX - HitSlackPixels || x > endX + HitSlackPixels)
                {
                    continue;
                }

                // Prefer the note whose head is nearest the pointer, so overlapping sustains
                // resolve to the one the user is most likely pointing at.
                double distance = Math.Abs(x - startX);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        /// <summary>Snaps a tick to the active quantize division.</summary>
        internal int QuantizeTick(double tick)
        {
            int ticksPerMeasure = TicksPerMeasure;
            if (ticksPerMeasure <= 0 || QuantizeDivision <= 0)
            {
                return Math.Max(0, (int)Math.Round(tick, MidpointRounding.AwayFromZero));
            }

            double step = (double)ticksPerMeasure / QuantizeDivision;
            if (step <= 0)
            {
                return Math.Max(0, (int)Math.Round(tick, MidpointRounding.AwayFromZero));
            }
            return Math.Max(0, (int)Math.Round(tick / step, MidpointRounding.AwayFromZero) *
                (int)Math.Round(step, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Moves the playhead to a client X on the ruler and asks the shell to follow. The shell
        /// owns the audio, so the surface only reports where the user pointed.
        /// </summary>
        private void SeekToScreenX(int screenX)
        {
            if (_viewport == null)
            {
                return;
            }

            int tick = QuantizeTick(Math.Max(0, _viewport.TickAtScreenX(
                Math.Max(_coordinates.HeaderWidth, screenX))));
            PlayheadVirtualTick = tick;
            Invalidate();
            if (SeekRequested != null)
            {
                SeekRequested(this, new VerticalSeekEventArgs(tick));
            }
        }

        private void BeginEditingGesture(MouseEventArgs e)
        {
            if (Document == null || _viewport == null)
            {
                return;
            }
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
            {
                return;
            }
            if (e.Y < _coordinates.RulerHeight ||
                e.X < _coordinates.HeaderWidth)
            {
                return;
            }

            _gestureOrigin = e.Location;
            _gestureExceededThreshold = false;
            TimelineItem hit = HitTest(e.X, e.Y);
            TimelineTool tool = ActiveTool;

            if (e.Button == MouseButtons.Left && tool == TimelineTool.Draw)
            {
                CreateEventAt(e.X, e.Y);
                return;
            }

            if (e.Button == MouseButtons.Left && tool == TimelineTool.Erase ||
                e.Button == MouseButtons.Right)
            {
                if (hit != null && hit.SourceEvent != null)
                {
                    Document.Selection.Replace(new[] { hit.SourceEvent });
                    if (Document.Edits.DeleteSelection())
                    {
                        Rebind();
                    }
                }
                return;
            }

            if (e.Button == MouseButtons.Left && tool == TimelineTool.Resize)
            {
                if (hit != null &&
                    hit.SourceEvent != null &&
                    hit.SourceEvent.EventType == EventType.Note)
                {
                    Document.Selection.Replace(new[] { hit.SourceEvent });
                    _resizeUndoGroup = new object();
                    _gestureLastTick = QuantizeTick(_viewport.TickAtScreenX(e.X));
                    Document.Interaction.Begin(
                        TimelineInteractionKind.ResizingEnd,
                        new TimelineInteractionAnchor(
                            hit.SourceEvent.VirtualTick + hit.SourceEvent.VirtualDuration,
                            (int)hit.SourceEvent.TrackId));
                    Capture = true;
                    Cursor = Cursors.SizeWE;
                }
                return;
            }

            bool additive = (ModifierKeys & Keys.Shift) == Keys.Shift ||
                (ModifierKeys & Keys.Control) == Keys.Control;

            if (hit == null || hit.SourceEvent == null)
            {
                if (!additive)
                {
                    Document.Selection.Clear();
                }
                _isMarqueeActive = true;
                _marquee = new Rectangle(e.X, e.Y, 0, 0);
                Document.Interaction.Begin(
                    TimelineInteractionKind.MarqueeSelecting,
                    new TimelineInteractionAnchor(
                        QuantizeTick(_viewport.TickAtScreenX(e.X)),
                        Math.Max(0, _coordinates.YToRow(e.Y, _firstVisibleRow))));
                Capture = true;
                Invalidate();
                return;
            }

            if (additive)
            {
                var extended = new List<EventData>(Document.Selection.Items);
                if (extended.Contains(hit.SourceEvent))
                {
                    extended.Remove(hit.SourceEvent);
                }
                else
                {
                    extended.Add(hit.SourceEvent);
                }
                Document.Selection.Replace(extended);
            }
            else if (!Document.Selection.Items.Contains(hit.SourceEvent))
            {
                Document.Selection.Replace(new[] { hit.SourceEvent });
            }

            _isMovingSelection = true;
            _moveUndoGroup = new object();
            _gestureLastTick = QuantizeTick(_viewport.TickAtScreenX(e.X));
            _gestureLastRow = _coordinates.YToRow(e.Y, _firstVisibleRow);
            Document.Interaction.Begin(
                TimelineInteractionKind.MovingSelection,
                new TimelineInteractionAnchor(_gestureLastTick, _gestureLastRow));
            Capture = true;
            Invalidate();
        }

        private void ContinueEditingGesture(MouseEventArgs e)
        {
            if (Document == null || _viewport == null)
            {
                UpdateGestureCursor(e);
                return;
            }

            if (!_gestureExceededThreshold &&
                (Math.Abs(e.X - _gestureOrigin.X) > DragThresholdPixels ||
                 Math.Abs(e.Y - _gestureOrigin.Y) > DragThresholdPixels))
            {
                _gestureExceededThreshold = true;
            }

            if (_isMarqueeActive)
            {
                _marquee = Rectangle.FromLTRB(
                    Math.Min(_gestureOrigin.X, e.X),
                    Math.Min(_gestureOrigin.Y, e.Y),
                    Math.Max(_gestureOrigin.X, e.X),
                    Math.Max(_gestureOrigin.Y, e.Y));
                ApplyMarqueeSelection();
                Invalidate();
                return;
            }

            if (_resizeUndoGroup != null && e.Button == MouseButtons.Left)
            {
                int snapped = QuantizeTick(_viewport.TickAtScreenX(e.X));
                int delta = snapped - _gestureLastTick;
                if (delta != 0 && Document.Edits.ResizeSelection(delta, _resizeUndoGroup))
                {
                    _gestureLastTick = snapped;
                    Invalidate();
                }
                return;
            }

            if (_isMovingSelection && e.Button == MouseButtons.Left)
            {
                if (!_gestureExceededThreshold)
                {
                    return;
                }

                int snappedTick = QuantizeTick(_viewport.TickAtScreenX(e.X));
                int row = _coordinates.YToRow(e.Y, _firstVisibleRow);
                int tickDelta = snappedTick - _gestureLastTick;
                int rowDelta = row - _gestureLastRow;
                if ((tickDelta != 0 || rowDelta != 0) &&
                    Document.Edits.MoveSelection(rowDelta, tickDelta, _moveUndoGroup))
                {
                    _gestureLastTick = snappedTick;
                    _gestureLastRow = row;
                    Invalidate();
                }
                return;
            }

            UpdateGestureCursor(e);
        }

        private void EndEditingGesture()
        {
            bool wasEditing = _isMarqueeActive || _isMovingSelection || _resizeUndoGroup != null;

            _isMarqueeActive = false;
            _isMovingSelection = false;
            _marquee = Rectangle.Empty;
            _moveUndoGroup = null;
            _resizeUndoGroup = null;
            _gestureExceededThreshold = false;

            if (Document != null)
            {
                Document.Interaction.Complete();
            }
            if (wasEditing)
            {
                Invalidate();
            }
        }

        /// <summary>
        /// Abandons an in-flight gesture without committing anything further. Undo groups already
        /// pushed stay on the stack — Escape stops the drag, it is not an undo — which is exactly
        /// how V1 behaves.
        /// </summary>
        private void CancelEditingGesture()
        {
            if (!_isMarqueeActive && !_isMovingSelection && _resizeUndoGroup == null)
            {
                return;
            }

            _isMarqueeActive = false;
            _isMovingSelection = false;
            _marquee = Rectangle.Empty;
            _moveUndoGroup = null;
            _resizeUndoGroup = null;
            _gestureExceededThreshold = false;
            Capture = false;
            Cursor = Cursors.Default;
            if (Document != null)
            {
                Document.Interaction.Cancel();
            }
            Invalidate();
        }

        private void UpdateGestureCursor(MouseEventArgs e)
        {
            if (Document == null || _isPanning || _isRulerScrubbing)
            {
                return;
            }

            if (e.Y < _coordinates.RulerHeight && e.X >= _coordinates.HeaderWidth)
            {
                Cursor = Cursors.VSplit;
                return;
            }

            TimelineTool tool = ActiveTool;
            if (tool == TimelineTool.Resize)
            {
                TimelineItem hover = HitTest(e.X, e.Y);
                Cursor = hover != null &&
                    hover.SourceEvent != null &&
                    hover.SourceEvent.EventType == EventType.Note
                        ? Cursors.SizeWE
                        : Cursors.Default;
                return;
            }
            Cursor = Cursors.Default;
        }

        private void ApplyMarqueeSelection()
        {
            if (Document == null || _viewport == null || _projectionResult == null)
            {
                return;
            }

            double startTick = _viewport.TickAtScreenX(_marquee.Left);
            double endTick = _viewport.TickAtScreenX(_marquee.Right);
            int firstRow = Math.Max(0, _coordinates.YToRow(_marquee.Top, _firstVisibleRow));
            int lastRow = Math.Max(firstRow, _coordinates.YToRow(_marquee.Bottom, _firstVisibleRow));

            IReadOnlyList<TimelineItem> hits = _index.Query(
                new TimelineTimeRange(startTick, endTick),
                new TimelineRowRange(firstRow, lastRow),
                Math.Max(96, TicksPerMeasure),
                0);

            var selected = new List<EventData>(hits.Count);
            foreach (TimelineItem item in hits)
            {
                if (item.SourceEvent != null)
                {
                    selected.Add(item.SourceEvent);
                }
            }
            Document.Selection.Replace(selected);
        }

        private void CreateEventAt(int screenX, int screenY)
        {
            if (!CanMutateDocument || TemplateEvent == null || _projectionResult == null)
            {
                return;
            }

            int row = _coordinates.YToRow(screenY, _firstVisibleRow);
            if (row < 0 || row >= _projectionResult.Rows.Count)
            {
                return;
            }

            int tick = QuantizeTick(_viewport.TickAtScreenX(screenX));
            EventData created = Document.Edits.CreateEvent(
                TemplateEvent,
                _projectionResult.Rows[row].SourceTrackId,
                tick);
            if (created != null)
            {
                Rebind();
            }
        }

        /// <summary>
        /// Re-projects after a structural edit. Moves and resizes are read straight off the live
        /// events by the renderer, so only add/remove needs this; rebinding on every drag frame
        /// would rebuild the whole interval index per pixel of pointer travel.
        /// </summary>
        /// <remarks>
        /// Marking the projection stale rather than rebuilding here is deliberate: the same signal
        /// arrives from <c>UndoManager.OnUndoRedo</c> for edits made anywhere else in the shell, so
        /// both paths converge on one lazy rebuild at the next frame instead of two eager ones.
        /// </remarks>
        private void Rebind()
        {
            if (Document == null)
            {
                return;
            }

            MarkProjectionStale();
            Invalidate();
        }
    }
}
