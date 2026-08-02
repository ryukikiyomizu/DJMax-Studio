using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DJMaxEditor.Controls.TimelineV2;
using DJMaxEditor.Controls.TimelineV2.Renderers;
using DJMaxEditor.Editor;
using DJMaxEditor.UI;

namespace DJMaxEditor.Preview
{
    public sealed class GameplayPreviewControl : Control
    {
        private EditorDocumentContext _document;
        private GameplayPreviewProjection _projection;
        private GameplayPreviewFrame _frame;
        private GameplayPreviewProfile _profile = GameplayPreviewProfile.Generic;
        private float _noteZoom = 1.35f;

        public GameplayPreviewControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            BackColor = StudioDesignSystem.Void;
            Dock = DockStyle.Fill;
            MinimumSize = new Size(320, 220);
            TabStop = true;
        }

        public EditorDocumentContext Document
        {
            get { return _document; }
        }

        public GameplayPreviewProfile Profile
        {
            get { return _profile; }
        }

        public float NoteZoom
        {
            get { return _noteZoom; }
            set
            {
                _noteZoom = Math.Max(0.75f, Math.Min(2.5f, value));
                Invalidate();
            }
        }

        public string ProjectionStatus
        {
            get
            {
                return _projection == null
                    ? "NO DOCUMENT"
                    : _projection.StatusLabel;
            }
        }

        public int DiagnosticCount
        {
            get { return _projection == null ? 0 : _projection.Diagnostics.Count; }
        }

        public void Bind(EditorDocumentContext document)
        {
            if (_document != null)
            {
                _document.Model.Tracks.EventAdded -= ChartTopologyChanged;
                _document.Model.Tracks.EventRemoved -= ChartTopologyChanged;
                _document.UndoManager.OnUndoRedo -= DocumentUndoRedo;
            }

            _document = document;
            if (_document != null)
            {
                _document.Model.Tracks.EventAdded += ChartTopologyChanged;
                _document.Model.Tracks.EventRemoved += ChartTopologyChanged;
                _document.UndoManager.OnUndoRedo += DocumentUndoRedo;
            }
            RebuildProjection();
        }

        public void SetProfile(GameplayPreviewProfile profile)
        {
            if (_profile == profile) return;
            _profile = profile;
            RebuildProjection();
        }

        public void RefreshTopology()
        {
            RebuildProjection();
        }

        public void RefreshPlayback()
        {
            if (_projection == null || _document == null)
            {
                _frame = null;
            }
            else
            {
                _frame = _projection.CreateRenderableFrame(_document.Model.CurrentTick);
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _document != null)
            {
                _document.Model.Tracks.EventAdded -= ChartTopologyChanged;
                _document.Model.Tracks.EventRemoved -= ChartTopologyChanged;
                _document.UndoManager.OnUndoRedo -= DocumentUndoRedo;
            }
            base.Dispose(disposing);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            NoteZoom += e.Delta > 0 ? 0.1f : -0.1f;
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(StudioDesignSystem.Void);

            Rectangle viewport = ClientRectangle;
            viewport.Inflate(-12, -12);
            if (viewport.Width <= 0 || viewport.Height <= 0) return;

            if (_projection == null || _frame == null)
            {
                DrawEmptyState(graphics, viewport);
                return;
            }

            if (_projection.Profile == GameplayPreviewProfile.Technika)
            {
                DrawTechnikaFrame(graphics, viewport);
            }
            else
            {
                DrawGenericFrame(graphics, viewport);
            }
            DrawOverlay(graphics, viewport);
        }

        private void RebuildProjection()
        {
            _projection = _document == null
                ? null
                : GameplayPreviewProjector.Project(_document.Model, _profile);
            RefreshPlayback();
        }

        private void ChartTopologyChanged(object sender, EventArgs e)
        {
            RebuildProjection();
        }

        private void DocumentUndoRedo(object sender, UndoManager.Action action)
        {
            RebuildProjection();
        }

        private void DrawEmptyState(Graphics graphics, Rectangle viewport)
        {
            using (var title = StudioDesignSystem.DisplayFont(14f))
            using (var body = StudioDesignSystem.BodyFont(9f))
            using (var primary = new SolidBrush(StudioDesignSystem.Frost))
            using (var muted = new SolidBrush(StudioDesignSystem.Muted))
            {
                graphics.DrawString("GAMEPLAY PREVIEW", title, primary,
                    viewport.Left + 18, viewport.Top + 18);
                graphics.DrawString(
                    "Open a chart to visualize the shared playback position.",
                    body,
                    muted,
                    viewport.Left + 18,
                    viewport.Top + 54);
            }
        }

        private void DrawTechnikaFrame(Graphics graphics, Rectangle viewport)
        {
            int middle = viewport.Top + viewport.Height / 2;
            Rectangle top = new Rectangle(
                viewport.Left, viewport.Top, viewport.Width, viewport.Height / 2);
            Rectangle bottom = new Rectangle(
                viewport.Left, middle, viewport.Width, viewport.Bottom - middle);

            using (var topBrush = new SolidBrush(StudioDesignSystem.Deck))
            using (var bottomBrush = new SolidBrush(Color.FromArgb(18, 28, 42)))
            using (var border = new Pen(StudioDesignSystem.Border))
            using (var lanePen = new Pen(Color.FromArgb(100, StudioDesignSystem.Border)))
            {
                graphics.FillRectangle(topBrush, top);
                graphics.FillRectangle(bottomBrush, bottom);
                graphics.DrawRectangle(border, viewport);
                graphics.DrawLine(border, viewport.Left, middle, viewport.Right, middle);
                DrawLaneGrid(graphics, top, _projection.LaneCount, lanePen);
                DrawLaneGrid(graphics, bottom, _projection.LaneCount, lanePen);
            }

            foreach (ProjectedGameplayNote note in _frame.Notes)
            {
                if (note.State == GameplayPreviewNoteState.Resolved ||
                    note.State == GameplayPreviewNoteState.Inactive)
                {
                    continue;
                }
                DrawTechnikaNote(graphics, viewport, note);
            }

            bool topScan = (_frame.CurrentIntScan & 1) == 1;
            double scanBase = 0.15 + 0.75 * _frame.CurrentPhase;
            double scanX = topScan ? scanBase : 1.0 - scanBase;
            int x = viewport.Left + (int)Math.Round(scanX * viewport.Width);
            Rectangle half = topScan ? top : bottom;
            using (var glow = new Pen(Color.FromArgb(62, StudioDesignSystem.PulseCyan), 7f))
            using (var scan = new Pen(StudioDesignSystem.PulseCyan, 2f))
            {
                graphics.DrawLine(glow, x, half.Top + 1, x, half.Bottom - 1);
                graphics.DrawLine(scan, x, half.Top + 1, x, half.Bottom - 1);
            }
        }

        private void DrawGenericFrame(Graphics graphics, Rectangle viewport)
        {
            using (var deck = new SolidBrush(StudioDesignSystem.Deck))
            using (var border = new Pen(StudioDesignSystem.Border))
            using (var lanePen = new Pen(Color.FromArgb(100, StudioDesignSystem.Border)))
            {
                graphics.FillRectangle(deck, viewport);
                graphics.DrawRectangle(border, viewport);
                DrawLaneGrid(graphics, viewport, Math.Min(32, _projection.LaneCount), lanePen);
            }

            int playhead = viewport.Left + viewport.Width / 2;
            using (var glow = new Pen(Color.FromArgb(58, StudioDesignSystem.BeatViolet), 8f))
            using (var line = new Pen(StudioDesignSystem.BeatViolet, 2f))
            {
                graphics.DrawLine(glow, playhead, viewport.Top, playhead, viewport.Bottom);
                graphics.DrawLine(line, playhead, viewport.Top, playhead, viewport.Bottom);
            }

            foreach (ProjectedGameplayNote note in _frame.Notes)
            {
                if (note.X <= 0.04 || note.X >= 0.96) continue;
                int x = viewport.Left + (int)Math.Round(note.X * viewport.Width);
                int y = viewport.Top + (int)Math.Round(note.Y * viewport.Height);
                int size = Math.Max(6, (int)Math.Round(12 * _noteZoom));
                Color color = note.State == GameplayPreviewNoteState.Active
                    ? StudioDesignSystem.PulseCyan
                    : StudioDesignSystem.Muted;
                using (var brush = new SolidBrush(color))
                {
                    graphics.FillEllipse(brush, x - size / 2, y - size / 2, size, size);
                }
            }
        }

        private void DrawLaneGrid(
            Graphics graphics,
            Rectangle rectangle,
            int lanes,
            Pen pen)
        {
            lanes = Math.Max(1, lanes);
            for (int lane = 1; lane < lanes; lane++)
            {
                int y = rectangle.Top + (rectangle.Height * lane / lanes);
                graphics.DrawLine(pen, rectangle.Left, y, rectangle.Right, y);
            }
        }

        private void DrawTechnikaNote(
            Graphics graphics,
            Rectangle viewport,
            ProjectedGameplayNote note)
        {
            int x = viewport.Left + (int)Math.Round(note.X * viewport.Width);
            int y = viewport.Top + (int)Math.Round(note.Y * viewport.Height);
            int lanePixels = Math.Max(12,
                viewport.Height / (2 * Math.Max(1, _projection.LaneCount)));
            int size = Math.Max(10,
                (int)Math.Round(lanePixels * 0.62f * _noteZoom));
            int alpha = note.State == GameplayPreviewNoteState.Prepare ? 150 : 240;
            Color color = NoteColor(note.Kind);

            if (note.ApproachVisible)
            {
                int ringSize = size + (int)Math.Round((1.0 - note.ApproachProgress) * size * 1.6);
                using (var ring = new Pen(Color.FromArgb(140, color), 2f))
                {
                    graphics.DrawEllipse(
                        ring,
                        x - ringSize / 2,
                        y - ringSize / 2,
                        ringSize,
                        ringSize);
                }
            }

            if (note.DurationPulse > 30 &&
                note.Kind != GameplayPreviewNoteKind.Basic &&
                note.Kind != GameplayPreviewNoteKind.ChainHead &&
                note.Kind != GameplayPreviewNoteKind.ChainNode)
            {
                int trail = Math.Max(size, Math.Min(viewport.Width / 3,
                    note.DurationPulse * viewport.Width / (960 * 2)));
                int direction = note.IsTopHalf ? 1 : -1;
                using (var trailBrush = new SolidBrush(Color.FromArgb(alpha / 2, color)))
                {
                    graphics.FillRectangle(
                        trailBrush,
                        direction > 0 ? x : x - trail,
                        y - Math.Max(2, size / 6),
                        trail,
                        Math.Max(4, size / 3));
                }
            }

            using (var glow = new SolidBrush(Color.FromArgb(48, color)))
            {
                graphics.FillEllipse(glow,
                    x - size, y - size, size * 2, size * 2);
            }

            bool drewAuthenticArt = TechnikaNoteArt.TryDraw(
                graphics,
                ToTechnikaKind(note.Kind),
                x,
                y,
                size + 4,
                alpha / 255f);
            if (!drewAuthenticArt)
            {
                using (var body = new SolidBrush(Color.FromArgb(alpha, color)))
                using (var edge = new Pen(StudioDesignSystem.Frost, 1.25f))
                {
                    graphics.FillEllipse(body,
                        x - size / 2, y - size / 2, size, size);
                    graphics.DrawEllipse(edge,
                        x - size / 2, y - size / 2, size, size);
                }
            }
        }

        private static TechnikaNoteKind ToTechnikaKind(GameplayPreviewNoteKind kind)
        {
            switch (kind)
            {
                case GameplayPreviewNoteKind.Basic:
                    return TechnikaNoteKind.Basic;
                case GameplayPreviewNoteKind.Drag:
                    return TechnikaNoteKind.Drag;
                case GameplayPreviewNoteKind.ChainHead:
                    return TechnikaNoteKind.ChainHead;
                case GameplayPreviewNoteKind.ChainNode:
                    return TechnikaNoteKind.ChainNode;
                case GameplayPreviewNoteKind.RepeatHead:
                    return TechnikaNoteKind.RepeatHead;
                case GameplayPreviewNoteKind.RepeatHeadHold:
                    return TechnikaNoteKind.RepeatHeadHold;
                case GameplayPreviewNoteKind.Repeat:
                    return TechnikaNoteKind.Repeat;
                case GameplayPreviewNoteKind.RepeatHold:
                    return TechnikaNoteKind.RepeatHold;
                case GameplayPreviewNoteKind.Hold:
                    return TechnikaNoteKind.Hold;
                default:
                    return TechnikaNoteKind.Unknown;
            }
        }

        private void DrawOverlay(Graphics graphics, Rectangle viewport)
        {
            string diagnostics = DiagnosticCount == 0
                ? "NO PROJECTION WARNINGS"
                : DiagnosticCount + " PROJECTION WARNING" +
                    (DiagnosticCount == 1 ? string.Empty : "S");
            using (var panel = new SolidBrush(Color.FromArgb(225, StudioDesignSystem.Void)))
            using (var status = StudioDesignSystem.UtilityFont(7.5f))
            using (var statusBrush = new SolidBrush(StudioDesignSystem.Frost))
            using (var detailBrush = new SolidBrush(
                DiagnosticCount == 0
                    ? StudioDesignSystem.Muted
                    : StudioDesignSystem.SignalAmber))
            {
                var box = new Rectangle(
                    viewport.Left + 10,
                    viewport.Top + 10,
                    Math.Min(viewport.Width - 20, 455),
                    44);
                graphics.FillRectangle(panel, box);
                graphics.DrawString(
                    _projection.StatusLabel,
                    status,
                    statusBrush,
                    box.Left + 10,
                    box.Top + 7);
                graphics.DrawString(
                    "TICK " + _frame.CurrentTick + "  |  " + diagnostics,
                    status,
                    detailBrush,
                    box.Left + 10,
                    box.Top + 24);
            }
        }

        private static Color NoteColor(GameplayPreviewNoteKind kind)
        {
            switch (kind)
            {
                case GameplayPreviewNoteKind.ChainHead:
                case GameplayPreviewNoteKind.ChainNode:
                    return StudioDesignSystem.AutomationGreen;
                case GameplayPreviewNoteKind.RepeatHead:
                case GameplayPreviewNoteKind.RepeatHeadHold:
                case GameplayPreviewNoteKind.Repeat:
                case GameplayPreviewNoteKind.RepeatHold:
                    return StudioDesignSystem.BeatViolet;
                case GameplayPreviewNoteKind.Hold:
                case GameplayPreviewNoteKind.Drag:
                    return StudioDesignSystem.SignalAmber;
                default:
                    return StudioDesignSystem.PulseCyan;
            }
        }
    }
}
