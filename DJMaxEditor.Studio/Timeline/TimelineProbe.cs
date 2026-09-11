using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;
using DJMaxEditor.Studio.Documents;

namespace DJMaxEditor.Studio.Timeline
{
    /// <summary>
    /// Renders the editing timeline to PNG without showing a window, the timeline counterpart of
    /// <see cref="Preview.PlayfieldProbe"/> and for the same reason: screen-scraping the shell
    /// needs the window in the foreground, which Windows will not grant a background script while
    /// someone is using the machine.
    ///
    /// <para>
    /// The report matters as much as the image. Whether a note draws its arcade art rather than a
    /// plain rectangle is decided by three independent gates in
    /// <c>StudioVerticalCanvas.DrawNoteArt</c> - the chart must be TECHNIKA-shaped, the note must
    /// sit on a playable lane, and the lane must be thick enough to be worth a glyph - and a
    /// screenshot of a rectangle cannot say which gate closed. So every note in frame is printed
    /// with its classified kind, its column role, its thickness and the sheets its kind resolves
    /// to, which names the gate directly.
    /// </para>
    /// </summary>
    internal static class TimelineProbe
    {
        /// <summary>Command-line switch that selects this mode.</summary>
        public const string Switch = "--timeline-shot";

        private const int DefaultWidth = 1200;
        private const int DefaultHeight = 320;
        private const float DefaultZoom = 1.6f;

        /// <summary>
        /// Runs the probe. Returns the process exit code: 0 when frames were written, non-zero
        /// when the chart could not be opened or carries no timeline layout.
        /// </summary>
        public static int Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(Switch + " <chart> <output-directory> [width=..] " +
                    "[height=..] [zoom=..] [ticks=a,b,c] [orientation=horizontal|vertical] " +
                    "[labels] [no-assets]");
                return 2;
            }

            string chartPath = args[1];
            string outputDirectory = args[2];
            var options = Options.Parse(args, 3);

            var report = new StringBuilder();
            report.Append("chart: ").AppendLine(chartPath);
            report.AppendFormat(CultureInfo.InvariantCulture,
                "canvas: {0}x{1}  zoom={2}  orientation={3}  assets={4}  labels={5}",
                options.Width, options.Height, options.Zoom, options.Orientation,
                options.Assets, options.Labels).AppendLine();

            PlayerData model = Preview.PlayfieldProbe.Open(chartPath, report);
            if (model == null)
            {
                Write(outputDirectory, report);
                return 3;
            }

            var document = new EditorDocumentContext(model, chartPath, new UndoManager());
            var viewModel = new VerticalTimelineViewModel();
            viewModel.Bind(document);
            viewModel.TimeDirection = options.Direction;
            viewModel.TrySetTimeZoom(options.Zoom);

            if (!viewModel.HasLayout)
            {
                report.AppendLine("no timeline layout - nothing to draw");
                Write(outputDirectory, report);
                return 4;
            }

            report.AppendFormat(CultureInfo.InvariantCulture,
                "layout: mode={0} technika={1}  columns={2}  endTick={3}",
                viewModel.Mode, VerticalTrackLayout.IsTechnikaMode(viewModel.Layout.Mode),
                viewModel.Layout.Columns.Count, viewModel.DocumentEndTick).AppendLine();
            DescribeColumns(viewModel.Layout, report);
            DescribeKinds(model, report);

            var canvas = new StudioVerticalCanvas
            {
                Orientation = options.Orientation,
                ShowNoteAssets = options.Assets,
                ShowNoteLabels = options.Labels,
                ViewModel = viewModel
            };

            // Optional headless exercise of the real mouse gesture: a surface press on a note,
            // a series of drag points across lanes and through time, and a release, through the
            // same internal surface API the mouse handlers call.
            if (options.MouseDrag != null)
            {
                canvas.Measure(new Size(options.Width, options.Height));
                canvas.Arrange(new Rect(0, 0, options.Width, options.Height));
                canvas.UpdateLayout();
                int mouseExit = RunMouseDrag(canvas, viewModel, model,
                    options.MouseDrag, options, report);
                if (mouseExit != 0)
                {
                    Write(outputDirectory, report);
                    return mouseExit;
                }
            }

            // Optional headless exercise of the note-drag path: select one note and run the
            // same public nudge the arrow keys (and the drag gesture's column math) use, so a
            // rendered shot can show where the note ends up.
            if (options.Drag != null)
            {
                canvas.Measure(new Size(options.Width, options.Height));
                canvas.Arrange(new Rect(0, 0, options.Width, options.Height));
                canvas.UpdateLayout();

                EventData picked = PickEvent(model, options.Drag.Lane, options.Drag.Tick);
                if (picked != null)
                {
                    document.Selection.Replace(new[] { picked });
                    for (int i = 0; i < options.Drag.Times; i++)
                    {
                        bool moved = canvas.NudgeSelection(options.Drag.Dx, options.Drag.Dy);
                        report.AppendFormat(CultureInfo.InvariantCulture,
                            "drag nudge {0}: x={1} y={2} moved={3} -> track={4} tick={5}",
                            i, options.Drag.Dx, options.Drag.Dy, moved,
                            picked.TrackId, picked.Tick).AppendLine();
                    }
                }
                else
                {
                    report.AppendLine("drag: no note found at the requested lane/tick");
                }
            }

            // `ticks=` is given in the chart's own raw ticks, the same units the sibling playfield
            // probe takes and the same ones the report prints, so a tick read off one report can be
            // handed straight to the other. Everything past here is virtual.
            int[] ticks = options.Ticks == null
                ? ChooseTicks(viewModel, model)
                : Scale(options.Ticks);
            Directory.CreateDirectory(outputDirectory);

            for (int i = 0; i < ticks.Length; i++)
            {
                Shoot(canvas, viewModel, ticks[i], i, options, outputDirectory, report);
            }

            Write(outputDirectory, report);
            return 0;
        }

        /// <summary>
        /// The lane axis, so a note that is simply scrolled off the side of the canvas is not read
        /// as a note whose art failed to draw. In the horizontal reading the lane axis is the
        /// canvas <em>height</em>, which is easy to under-budget: twenty-four columns do not fit in
        /// a strip sized for a screenshot.
        /// </summary>
        private static void DescribeColumns(VerticalTrackLayout layout, StringBuilder report)
        {
            report.Append("columns:");
            for (int i = 0; i < layout.Columns.Count; i++)
            {
                VerticalColumn column = layout.Columns[i];
                report.AppendFormat(CultureInfo.InvariantCulture, " [{0}]{1}@{2}+{3}",
                    i, column.Kind, column.NativeLeft, column.Width);
            }
            report.AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "nativeWidth: {0}  gameplay: {1}  overflow: {2}",
                layout.NativeWidth, layout.GameplayNativeWidth,
                layout.OverflowColumnCount).AppendLine();
        }

        /// <summary>
        /// A census of the note kinds the chart actually contains, with where to find each one.
        /// "The repeat art looks wrong" and "this chart has no repeat notes" are different bugs, and
        /// a shot aimed at a tick chosen by guesswork cannot tell them apart - these ticks can be
        /// handed straight back through <c>ticks=</c>.
        /// </summary>
        private static void DescribeKinds(PlayerData model, StringBuilder report)
        {
            var counts = new System.Collections.Generic.Dictionary<TechnikaNoteKind, int>();
            var where = new System.Collections.Generic.Dictionary<TechnikaNoteKind, string>();

            for (int t = 0; t < model.Tracks.Count; t++)
            {
                TrackData track = model.Tracks.GetTrackAtIndex((uint)t);
                if (track == null)
                {
                    continue;
                }

                foreach (EventData note in track.Events)
                {
                    TechnikaNoteKind kind = TechnikaNoteClassifier.Classify(note);
                    int count;
                    counts.TryGetValue(kind, out count);
                    counts[kind] = count + 1;
                    if (count < 3)
                    {
                        string seen;
                        where.TryGetValue(kind, out seen);
                        where[kind] = (seen ?? string.Empty) + " t" + note.Tick + "/trk" + t;
                    }
                }
            }

            foreach (var pair in counts)
            {
                report.AppendFormat(CultureInfo.InvariantCulture, "kind {0,-14} x{1,-5} at{2}",
                    pair.Key, pair.Value, where[pair.Key]).AppendLine();
            }
        }

        private static int[] Scale(int[] rawTicks)
        {
            var virtualTicks = new int[rawTicks.Length];
            for (int i = 0; i < rawTicks.Length; i++)
            {
                virtualTicks[i] = rawTicks[i] * EventData.VirtualTickSize;
            }
            return virtualTicks;
        }

        private static void Shoot(
            StudioVerticalCanvas canvas,
            VerticalTimelineViewModel viewModel,
            int tick,
            int index,
            Options options,
            string outputDirectory,
            StringBuilder report)
        {
            viewModel.ScrollToTick(tick);
            canvas.InvalidateAll();

            canvas.Measure(new Size(options.Width, options.Height));
            canvas.Arrange(new Rect(0, 0, options.Width, options.Height));
            canvas.UpdateLayout();

            var target = new RenderTargetBitmap(
                options.Width, options.Height, 96, 96, PixelFormats.Pbgra32);
            target.Render(canvas);

            string file = Path.Combine(outputDirectory, string.Format(
                CultureInfo.InvariantCulture, "timeline-{0:00}-tick{1}.png",
                index, tick / EventData.VirtualTickSize));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using (FileStream stream = File.Create(file))
            {
                encoder.Save(stream);
            }

            VerticalTimelineFrame frame = canvas.Frame;
            report.AppendLine();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "frame {0}: rawTick={1} virtual={2} origin={3:F1} items={4} -> {5}",
                index, tick / EventData.VirtualTickSize, tick, viewModel.OriginTick,
                frame == null ? 0 : frame.Items.Count, Path.GetFileName(file)).AppendLine();

            if (frame == null)
            {
                return;
            }

            for (int i = 0; i < frame.Items.Count; i++)
            {
                DescribeItem(frame.Items[i], report);
            }
        }

        /// <summary>
        /// Drives the canvas's own press/drag/release surface API to move one note: the exact
        /// gesture a mouse performs, with no WPF input plumbing. A mismatch returns an exit
        /// code so CI can fail the probe.
        /// </summary>
        private static int RunMouseDrag(
            StudioVerticalCanvas canvas,
            VerticalTimelineViewModel viewModel,
            PlayerData model,
            Options.MouseDragSpec spec,
            Options options,
            StringBuilder report)
        {
            EventData picked = PickEvent(model, spec.Lane, spec.Tick);
            if (picked == null)
            {
                report.AppendLine("mouse drag: no note found at the requested lane/tick");
                return 6;
            }

            // Scroll the note into view before asking the frame for it: the shot's requested
            // tick is the *destination*, so without this the pressed note would be off-window.
            viewModel.ScrollToTick(spec.Tick * EventData.VirtualTickSize);
            canvas.Measure(new Size(options.Width, options.Height));
            canvas.Arrange(new Rect(0, 0, options.Width, options.Height));
            canvas.UpdateLayout();
            VerticalTimelineFrame frame = canvas.Frame;

            VerticalPlacedItem startItem = null;
            foreach (VerticalPlacedItem placed in frame.Items)
            {
                if (placed.Item.SourceEvent == picked)
                {
                    startItem = placed;
                    break;
                }
            }
            if (startItem == null)
            {
                report.AppendLine("mouse drag: pressed note is not in the visible frame");
                return 6;
            }

            // Note-capable columns in display order, mirroring StudioVerticalCanvas.
            var lanes = new List<VerticalColumn>();
            foreach (VerticalColumn column in frame.Layout.Columns)
            {
                switch (column.Kind)
                {
                    case VerticalColumnKind.SideLeft:
                    case VerticalColumnKind.ShoulderLeft:
                    case VerticalColumnKind.Button:
                    case VerticalColumnKind.ShoulderRight:
                    case VerticalColumnKind.SideRight:
                    case VerticalColumnKind.Overflow:
                        lanes.Add(column);
                        break;
                }
            }
            int startColumn = lanes.FindIndex(c => c.SourceTrackId == (int)picked.TrackId);
            int targetColumn = startColumn + spec.ColumnDelta;
            if (startColumn < 0 || targetColumn < 0 || targetColumn >= lanes.Count)
            {
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "mouse drag: lane target out of range (start={0}, delta={1}, count={2})",
                    startColumn, spec.ColumnDelta, lanes.Count).AppendLine();
                return 6;
            }

            var coords = frame.Coordinates;
            bool upward = coords.TimeDirection == VerticalTimeDirection.Upward;
            double startY = upward ? startItem.Bottom : startItem.Top;
            Point start = new Point(startItem.Left + (startItem.Width / 2.0), startY);

            VerticalColumn targetLane = lanes[targetColumn];
            int targetVirtualTick = picked.VirtualTick +
                (spec.RawTickDelta * EventData.VirtualTickSize);
            double targetX = coords.NativeXToScreen(
                targetLane.NativeLeft + (targetLane.Width / 2.0), frame.OriginNativeX);
            double targetY = coords.TickToY(targetVirtualTick, frame.OriginTick);
            var target = new Point(targetX, targetY);

            report.AppendFormat(CultureInfo.InvariantCulture,
                "mouse drag: from track {0} tick {1} at ({2:F1},{3:F1}) to track {4} tick {5} at ({6:F1},{7:F1})",
                picked.TrackId, picked.Tick, start.X, start.Y,
                targetLane.SourceTrackId, targetVirtualTick / EventData.VirtualTickSize,
                target.X, target.Y).AppendLine();

            VerticalHitResult pressHit = frame.HitTest(start.X, start.Y);
            canvas.SurfacePress(start, false);
            report.AppendFormat(CultureInfo.InvariantCulture,
                "mouse drag: press hit column={0} kind={1} item={2} armed={3} selected={4}",
                pressHit.HasColumn ? pressHit.Column.SourceTrackId.ToString() : "(none)",
                pressHit.HasColumn ? pressHit.Column.Kind.ToString() : "-",
                pressHit.HasItem, canvas.IsNoteMoveArmed,
                viewModel.Document.Selection.Count).AppendLine();
            const int steps = 6;
            for (int i = 1; i <= steps; i++)
            {
                // Well past the 3px arm threshold on the first step.
                var point = new Point(
                    start.X + ((target.X - start.X) * i / steps),
                    start.Y + ((target.Y - start.Y) * i / steps));
                canvas.SurfaceDrag(point);
                canvas.Measure(new Size(options.Width, options.Height));
                canvas.Arrange(new Rect(0, 0, options.Width, options.Height));
                canvas.UpdateLayout();
                report.AppendFormat(CultureInfo.InvariantCulture,
                    "    step {0}: track={1} tick={2}", i, picked.TrackId, picked.Tick)
                    .AppendLine();
            }
            canvas.SurfaceRelease();

            bool laneOk = picked.TrackId == (uint)targetLane.SourceTrackId;
            bool tickOk = picked.Tick == targetVirtualTick / EventData.VirtualTickSize;
            report.AppendFormat(CultureInfo.InvariantCulture,
                "mouse drag result: track={0} (want {1}, {2}) rawTick={3} (want {4}, {5})",
                picked.TrackId, targetLane.SourceTrackId, laneOk ? "ok" : "MISMATCH",
                picked.Tick, targetVirtualTick / EventData.VirtualTickSize,
                tickOk ? "ok" : "MISMATCH").AppendLine();
            return laneOk && tickOk ? 0 : 6;
        }

        /// <summary>
        /// The note nearest the requested raw tick on one track, for the headless drag test.
        /// </summary>
        private static EventData PickEvent(PlayerData model, int trackIndex, int rawTick)
        {
            if (model == null || trackIndex < 0 ||
                trackIndex >= model.Tracks.Count)
            {
                return null;
            }
            TrackData track = model.Tracks.GetTrackAtIndex((uint)trackIndex);
            EventData best = null;
            int bestDistance = int.MaxValue;
            foreach (EventData evt in track.Events)
            {
                if (evt.EventType != EventType.Note)
                {
                    continue;
                }
                int distance = Math.Abs(evt.Tick - rawTick);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = evt;
                }
            }
            return best;
        }

        /// <summary>
        /// One line per placed item, naming the three gates that decide art versus rectangle
        /// alongside the pieces the kind resolved to.
        /// </summary>
        private static void DescribeItem(VerticalPlacedItem placed, StringBuilder report)
        {
            EventData source = placed.Item.SourceEvent;
            TechnikaNoteKind kind = TechnikaNoteClassifier.Classify(source);
            TimelineNoteArt art = TimelineNoteSprites.Default.For(kind);

            report.AppendFormat(CultureInfo.InvariantCulture,
                "    {0,-14} tick={1,-6} attr={2,-3} dur={3,-5} col={4,-13} " +
                "rect=({5:F1},{6:F1} {7:F1}x{8:F1})  art={9}",
                kind, source == null ? 0 : source.Tick,
                source == null ? 0 : source.Attribute,
                source == null ? 0 : source.Duration,
                placed.Column == null ? "(none)" : placed.Column.Kind.ToString(),
                placed.Left, placed.Top, placed.Width, placed.Height,
                Describe(art)).AppendLine();
        }

        private static string Describe(TimelineNoteArt art)
        {
            if (art == null)
            {
                return "(none - rectangle)";
            }

            var text = new StringBuilder();
            text.Append("head ").Append(art.Head.FrameWidth).Append('x')
                .Append(art.Head.FrameSize);
            if (art.Cap != null)
            {
                text.Append(" + cap ").Append(art.Cap.FrameWidth).Append('x')
                    .Append(art.Cap.FrameSize);
                text.Append(" + run ").Append(art.Body == null
                    ? "(stem)"
                    : art.Body.FrameWidth + "x" + art.Body.FrameSize);
            }
            return text.ToString();
        }

        /// <summary>
        /// Ticks worth shooting: the first note of each kind that the classifier recognises. A
        /// screenful of taps proves nothing about the trail composition, and the interesting kinds
        /// are often thousands of ticks apart.
        ///
        /// <para>
        /// Returned in the timeline's own virtual ticks, not the chart's raw ones - the two differ
        /// by <see cref="EventData.VirtualTickSize"/> and scrolling to a raw tick lands the shot six
        /// times too early, which looks exactly like a chart whose notes are all plain taps.
        /// </para>
        /// </summary>
        private static int[] ChooseTicks(VerticalTimelineViewModel viewModel, PlayerData model)
        {
            var found = new System.Collections.Generic.List<int>();
            var seen = new System.Collections.Generic.HashSet<TechnikaNoteKind>();

            for (int t = 0; t < model.Tracks.Count; t++)
            {
                TrackData track = model.Tracks.GetTrackAtIndex((uint)t);
                if (track == null)
                {
                    continue;
                }

                foreach (EventData note in track.Events)
                {
                    TechnikaNoteKind kind = TechnikaNoteClassifier.Classify(note);
                    if (kind == TechnikaNoteKind.Unknown || !seen.Add(kind))
                    {
                        continue;
                    }

                    // Back off part of a screen so the note is inside the shot, not on its edge.
                    found.Add(Math.Max(0, (note.Tick * EventData.VirtualTickSize) -
                        (int)(viewModel.VisibleTickCount / 3.0)));
                }
            }

            if (found.Count == 0)
            {
                found.Add(0);
            }
            found.Sort();
            return found.ToArray();
        }

        private static void Write(string outputDirectory, StringBuilder report)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(
                    Path.Combine(outputDirectory, "timeline-report.txt"), report.ToString());
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class Options
        {
            public int Width = DefaultWidth;
            public int Height = DefaultHeight;
            public float Zoom = DefaultZoom;
            public int[] Ticks;
            public TimelineOrientation Orientation = TimelineOrientation.Horizontal;
            public VerticalTimeDirection Direction = VerticalTimeDirection.Upward;
            public bool Assets = true;
            public bool Labels;
            public DragSpec Drag;
            public MouseDragSpec MouseDrag;

            public sealed class DragSpec
            {
                public int Lane;
                public int Tick;
                public int Dx;
                public int Dy;
                public int Times = 1;
            }

            public sealed class MouseDragSpec
            {
                public int Lane;
                public int Tick;
                public int ColumnDelta;
                public int RawTickDelta;
            }

            public static Options Parse(string[] args, int first)
            {
                var options = new Options();
                for (int i = first; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (Match(arg, "width=", ref options.Width) ||
                        Match(arg, "height=", ref options.Height) ||
                        Match(arg, "zoom=", ref options.Zoom))
                    {
                        continue;
                    }

                    if (arg.StartsWith("drag=", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = arg.Substring("drag=".Length).Split(',');
                        var values = new List<int>();
                        foreach (string part in parts)
                        {
                            int value;
                            if (int.TryParse(part.Trim(), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out value))
                            {
                                values.Add(value);
                            }
                        }
                        if (values.Count >= 4)
                        {
                            options.Drag = new DragSpec
                            {
                                Lane = values[0],
                                Tick = values[1],
                                Dx = values[2],
                                Dy = values[3],
                                Times = values.Count >= 5 ? values[4] : 1
                            };
                        }
                        continue;
                    }

                    if (arg.StartsWith("mouse=", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = arg.Substring("mouse=".Length).Split(',');
                        var values = new List<int>();
                        foreach (string part in parts)
                        {
                            int value;
                            if (int.TryParse(part.Trim(), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out value))
                            {
                                values.Add(value);
                            }
                        }
                        if (values.Count >= 4)
                        {
                            options.MouseDrag = new MouseDragSpec
                            {
                                Lane = values[0],
                                Tick = values[1],
                                ColumnDelta = values[2],
                                RawTickDelta = values[3]
                            };
                        }
                        continue;
                    }

                    if (arg.StartsWith("ticks=", StringComparison.OrdinalIgnoreCase))
                    {
                        options.Ticks = ParseTicks(arg.Substring("ticks=".Length));
                    }
                    else if (arg.StartsWith("orientation=", StringComparison.OrdinalIgnoreCase))
                    {
                        options.Orientation = arg.EndsWith(
                            "vertical", StringComparison.OrdinalIgnoreCase)
                            ? TimelineOrientation.Vertical
                            : TimelineOrientation.Horizontal;
                    }
                    else if (arg.Equals("downward", StringComparison.OrdinalIgnoreCase))
                    {
                        options.Direction = VerticalTimeDirection.Downward;
                    }
                    else if (arg.Equals("labels", StringComparison.OrdinalIgnoreCase))
                    {
                        options.Labels = true;
                    }
                    else if (arg.Equals("no-assets", StringComparison.OrdinalIgnoreCase))
                    {
                        options.Assets = false;
                    }
                }
                return options;
            }

            private static bool Match(string arg, string name, ref int slot)
            {
                if (!arg.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                int parsed;
                if (int.TryParse(arg.Substring(name.Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    slot = parsed;
                }
                return true;
            }

            private static bool Match(string arg, string name, ref float slot)
            {
                if (!arg.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                float parsed;
                if (float.TryParse(arg.Substring(name.Length), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    slot = parsed;
                }
                return true;
            }

            private static int[] ParseTicks(string text)
            {
                string[] parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                var ticks = new System.Collections.Generic.List<int>();
                for (int i = 0; i < parts.Length; i++)
                {
                    int tick;
                    if (int.TryParse(parts[i].Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out tick) && tick >= 0)
                    {
                        ticks.Add(tick);
                    }
                }
                return ticks.Count == 0 ? null : ticks.ToArray();
            }
        }
    }
}
