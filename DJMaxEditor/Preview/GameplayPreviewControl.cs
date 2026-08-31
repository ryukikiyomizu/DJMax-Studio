using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
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
        // Inputs the cached _frame was built from, so an unchanged refresh can be skipped.
        private GameplayPreviewProjection _frameProjection;
        private int _frameTick = -1;
        private float _frameNoteSpeed = -1f;
        private GameplayPreviewProfile _profile = GameplayPreviewProfile.Generic;
        private float _noteZoom = 1f;
        private float _noteSpeed = RespectGameplayLayout.DefaultNoteSpeed;
        private readonly IList<RespectVerticalSkin> _skins;
        private readonly IList<RespectVerticalSkin> _gearSkins;
        private readonly IList<RespectVerticalSkin> _noteSkins;
        private RespectVerticalSkin _gearSkin;
        private RespectVerticalSkin _noteSkin;
        private RespectGearVideoPlayer _gearVideo;
        private int _gearFrameInvalidatePending;

        // GDI handles, kept for the life of the control instead of per paint. Every draw helper
        // below used to open a using-block, so a 6B chart at 60fps was allocating and finalizing
        // two GDI objects per visible note per frame - which is the stutter the playtest hit as
        // soon as the preview was open. The caches are keyed by the exact colour/size asked for,
        // so the drawing code reads the same as before and no call site has to hold a field.
        private readonly Dictionary<int, SolidBrush> _brushCache =
            new Dictionary<int, SolidBrush>();
        private readonly Dictionary<long, Pen> _penCache = new Dictionary<long, Pen>();
        private readonly Dictionary<int, Font> _fontCache = new Dictionary<int, Font>();

        // The HUD strings are compile-time constants, so their extents only depend on the font.
        private SizeF _hudComboLabelSize;
        private SizeF _hudComboValueSize;
        private SizeF _hudMaxSize;
        private bool _hudMetricsMeasured;

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
            _skins = RespectVerticalSkin.LoadAvailable();
            _gearSkins = _skins.Where(skin => skin.CanSelectGear).ToList();
            _noteSkins = _skins.Where(skin => skin.CanSelectNotes).ToList();
            if (_gearSkins.Count > 0)
            {
                _gearSkin = _gearSkins[0];
                RebuildGearVideo();
            }
            if (_noteSkins.Count > 0) _noteSkin = _noteSkins[0];
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

        public float NoteSpeed
        {
            get { return _noteSpeed; }
            set
            {
                _noteSpeed = Math.Max(1f, Math.Min(20f, value));
                RefreshPlayback();
            }
        }

        public string ProjectionStatus
        {
            get
            {
                return _projection == null
                    ? "No document"
                    : _projection.StatusLabel;
            }
        }

        public int DiagnosticCount
        {
            get { return _projection == null ? 0 : _projection.Diagnostics.Count; }
        }

        public string RespectAssetStatus
        {
            get
            {
                return _gearSkin == null || _noteSkin == null
                    ? "Vector fallback"
                    : "GEAR " + _gearSkin.DisplayName +
                        " / NOTES " + _noteSkin.DisplayName;
            }
        }

        public IList<string> GearSkinNames
        {
            get { return _gearSkins.Select(skin => skin.DisplayName).ToList(); }
        }

        public IList<string> NoteSkinNames
        {
            get { return _noteSkins.Select(skin => skin.DisplayName).ToList(); }
        }

        public void SelectGearSkin(int index)
        {
            if (index < 0 || index >= _gearSkins.Count ||
                object.ReferenceEquals(_gearSkin, _gearSkins[index])) return;
            _gearSkin = _gearSkins[index];
            RebuildGearVideo();
            Invalidate();
        }

        public void SelectNoteSkin(int index)
        {
            if (index < 0 || index >= _noteSkins.Count ||
                object.ReferenceEquals(_noteSkin, _noteSkins[index])) return;
            _noteSkin = _noteSkins[index];
            Invalidate();
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

        /// <summary>
        /// Rebuilds the renderable frame, doing nothing when nothing that feeds it changed.
        /// </summary>
        /// <remarks>
        /// The shell calls this from a 16ms timer that runs whenever a chart is loaded, so an
        /// unconditional rebuild meant ~62 frame projections plus repaints a second even with
        /// playback stopped - which is why opening the preview made every other surface lag.
        /// The gate is keyed on the tick, the note speed and the projection identity, because
        /// those are the only three inputs <see cref="GameplayPreviewProjection"/> uses.
        /// </remarks>
        public void RefreshPlayback()
        {
            if (_projection == null || _document == null)
            {
                if (_frame == null && _frameProjection == null) return;
                _frame = null;
                _frameProjection = null;
                _frameTick = -1;
                PlaybackFrameRebuildCount++;
                Invalidate();
                return;
            }

            int tick = _document.Model.CurrentTick;
            if (_frame != null &&
                _frameTick == tick &&
                _frameNoteSpeed == _noteSpeed &&
                ReferenceEquals(_frameProjection, _projection))
            {
                return;
            }

            _frame = _projection.CreateRenderableFrame(tick, _noteSpeed);
            _frameTick = tick;
            _frameNoteSpeed = _noteSpeed;
            _frameProjection = _projection;
            PlaybackFrameRebuildCount++;
            Invalidate();
        }

        /// <summary>Times the renderable frame has actually been rebuilt; a test seam.</summary>
        public int PlaybackFrameRebuildCount { get; private set; }

        /// <summary>
        /// Live GDI objects held by the paint caches. A test seam, and the number that used to be
        /// created and thrown away on every single frame.
        /// </summary>
        internal int CachedPaintObjectCount
        {
            get { return _brushCache.Count + _penCache.Count + _fontCache.Count; }
        }

        private SolidBrush Fill(Color color)
        {
            int key = color.ToArgb();
            SolidBrush brush;
            if (!_brushCache.TryGetValue(key, out brush))
            {
                brush = new SolidBrush(color);
                _brushCache.Add(key, brush);
            }
            return brush;
        }

        private Pen Stroke(Color color, float width)
        {
            // Widths come from a handful of literals, so hundredths of a pixel is an exact key.
            long key = ((long)color.ToArgb() << 32) |
                (uint)(int)Math.Round(width * 100f);
            Pen pen;
            if (!_penCache.TryGetValue(key, out pen))
            {
                pen = new Pen(color, width);
                _penCache.Add(key, pen);
            }
            return pen;
        }

        private Font DisplayFont(float size)
        {
            return CachedFont(0, size);
        }

        private Font BodyFont(float size)
        {
            return CachedFont(1, size);
        }

        private Font UtilityFont(float size)
        {
            return CachedFont(2, size);
        }

        private Font CachedFont(int role, float size)
        {
            int key = (role * 100000) + (int)Math.Round(size * 10f);
            Font font;
            if (!_fontCache.TryGetValue(key, out font))
            {
                font = role == 0
                    ? StudioDesignSystem.DisplayFont(size)
                    : role == 1
                        ? StudioDesignSystem.BodyFont(size)
                        : StudioDesignSystem.UtilityFont(size);
                _fontCache.Add(key, font);
            }
            return font;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (SolidBrush brush in _brushCache.Values) brush.Dispose();
                foreach (Pen pen in _penCache.Values) pen.Dispose();
                foreach (Font font in _fontCache.Values) font.Dispose();
                _brushCache.Clear();
                _penCache.Clear();
                _fontCache.Clear();
                if (_document != null)
                {
                    _document.Model.Tracks.EventAdded -= ChartTopologyChanged;
                    _document.Model.Tracks.EventRemoved -= ChartTopologyChanged;
                    _document.UndoManager.OnUndoRedo -= DocumentUndoRedo;
                }
                if (_gearVideo != null)
                {
                    _gearVideo.FrameReady -= GearVideoFrameReady;
                    _gearVideo.Dispose();
                    _gearVideo = null;
                }
                foreach (RespectVerticalSkin skin in _skins)
                {
                    skin.Dispose();
                }
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
            // No frame-wide antialiasing. Every helper that actually needs smooth edges - the note
            // ellipses, the hit ring - turns it on for its own loop and restores it, so paying for
            // it on the rectangles and lane lines that make up most of the frame was pure cost.
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
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
            if (_projection.Profile == GameplayPreviewProfile.Technika ||
                RespectVerticalSkin.NormalizeLaneMode(_projection.LaneCount) == 0)
            {
                DrawOverlay(graphics, viewport);
            }
        }

        private void RebuildProjection()
        {
            _projection = _document == null
                ? null
                : GameplayPreviewProjector.Project(_document.Model, _profile);
            RefreshPlayback();
        }

        private void RebuildGearVideo()
        {
            if (_gearVideo != null)
            {
                _gearVideo.FrameReady -= GearVideoFrameReady;
                _gearVideo.Dispose();
                _gearVideo = null;
            }
            if (_gearSkin == null ||
                string.IsNullOrWhiteSpace(_gearSkin.GearVideoPath)) return;

            var player = new RespectGearVideoPlayer(_gearSkin.GearVideoPath);
            if (!player.IsAvailable)
            {
                player.Dispose();
                return;
            }
            _gearVideo = player;
            _gearVideo.FrameReady += GearVideoFrameReady;
        }

        private void GearVideoFrameReady(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (Interlocked.Exchange(ref _gearFrameInvalidatePending, 1) != 0)
                return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    Interlocked.Exchange(ref _gearFrameInvalidatePending, 0);
                    Invalidate();
                });
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _gearFrameInvalidatePending, 0);
                // The dock may close between the handle check and BeginInvoke.
            }
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
            graphics.DrawString("Gameplay preview", DisplayFont(14f),
                Fill(StudioDesignSystem.Frost),
                viewport.Left + 18, viewport.Top + 18);
            graphics.DrawString(
                "Open a chart to visualize the shared playback position.",
                BodyFont(9f),
                Fill(StudioDesignSystem.Muted),
                viewport.Left + 18,
                viewport.Top + 54);
        }

        private void DrawTechnikaFrame(Graphics graphics, Rectangle viewport)
        {
            int middle = viewport.Top + viewport.Height / 2;
            Rectangle top = new Rectangle(
                viewport.Left, viewport.Top, viewport.Width, viewport.Height / 2);
            Rectangle bottom = new Rectangle(
                viewport.Left, middle, viewport.Width, viewport.Bottom - middle);

            Pen border = Stroke(StudioDesignSystem.Border, 1f);
            graphics.FillRectangle(Fill(StudioDesignSystem.Deck), top);
            graphics.FillRectangle(Fill(Color.FromArgb(18, 28, 42)), bottom);
            graphics.DrawRectangle(border, viewport);
            graphics.DrawLine(border, viewport.Left, middle, viewport.Right, middle);
            Pen lanePen = Stroke(Color.FromArgb(100, StudioDesignSystem.Border), 1f);
            DrawLaneGrid(graphics, top, _projection.LaneCount, lanePen);
            DrawLaneGrid(graphics, bottom, _projection.LaneCount, lanePen);

            // Round glyphs are the only part of this frame that benefits from antialiasing, and
            // the only part that used to pay for it on the flat fills above.
            SmoothingMode previousSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (ProjectedGameplayNote note in _frame.Notes)
            {
                if (note.State == GameplayPreviewNoteState.Resolved ||
                    note.State == GameplayPreviewNoteState.Inactive)
                {
                    continue;
                }
                DrawTechnikaNote(graphics, viewport, note);
            }
            graphics.SmoothingMode = previousSmoothing;

            bool topScan = (_frame.CurrentIntScan & 1) == 1;
            double scanBase = 0.15 + 0.75 * _frame.CurrentPhase;
            double scanX = topScan ? scanBase : 1.0 - scanBase;
            int x = viewport.Left + (int)Math.Round(scanX * viewport.Width);
            Rectangle half = topScan ? top : bottom;
            graphics.DrawLine(
                Stroke(Color.FromArgb(62, StudioDesignSystem.PulseCyan), 7f),
                x, half.Top + 1, x, half.Bottom - 1);
            graphics.DrawLine(
                Stroke(StudioDesignSystem.PulseCyan, 2f),
                x, half.Top + 1, x, half.Bottom - 1);
        }

        private void DrawGenericFrame(Graphics graphics, Rectangle viewport)
        {
            int laneMode = RespectVerticalSkin.NormalizeLaneMode(_projection.LaneCount);
            if (laneMode != 0)
            {
                DrawRespectVerticalFrame(graphics, viewport, laneMode);
                return;
            }

            DrawHorizontalFallback(graphics, viewport);
        }

        private void DrawRespectVerticalFrame(
            Graphics graphics,
            Rectangle viewport,
            int lanes)
        {
            RespectGameplayLayout layout = RespectGameplayLayout.ForMode(lanes);
            RectangleF scene = layout.FitScene(viewport);
            RectangleF gear = layout.GetPlayfield(scene);
            Rectangle cabinet = RoundRectangle(scene);
            Rectangle playfield = RoundRectangle(gear);
            int regularLanes = lanes == 8 ? 6 : lanes;
            bool hasPackageSkin = _gearSkin != null && _gearSkin.IsLoaded;
            InterpolationMode previousInterpolation = graphics.InterpolationMode;
            PixelOffsetMode previousPixelOffset = graphics.PixelOffsetMode;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;

            graphics.FillRectangle(Fill(Color.FromArgb(120, 0, 0, 0)),
                cabinet.Left - 7, cabinet.Top + 7,
                cabinet.Width + 14, cabinet.Height);
            SolidBrush deck = Fill(Color.FromArgb(3, 5, 8));
            graphics.FillRectangle(deck, cabinet);
            graphics.FillRectangle(deck, playfield);
            if (!hasPackageSkin)
            {
                graphics.DrawRectangle(Stroke(Color.FromArgb(65, 92, 112), 1f), cabinet);
                Pen lanePen = Stroke(Color.FromArgb(34, 60, 78), 1f);
                for (int lane = 1; lane < regularLanes; lane++)
                {
                    float first = layout.GetTrackX(3);
                    float second = layout.GetTrackX(4);
                    float nativeX = first + ((second - first) * (lane - 0.5f));
                    float x = layout.ToScreenX(nativeX, gear);
                    graphics.DrawLine(lanePen, x, gear.Top, x, gear.Bottom);
                }
            }

            DrawSkinImage(graphics, "gear_bg", playfield);
            float packageScale = gear.Width / layout.PlayfieldWidth;
            int bottomHeight = Math.Max(1,
                (int)Math.Round(244f * packageScale));
            int capHeight = Math.Max(1,
                (int)Math.Round(56f * packageScale));
            Rectangle capRect = new Rectangle(
                playfield.Left, playfield.Bottom - bottomHeight - capHeight,
                playfield.Width, capHeight);
            Rectangle bottomRect = new Rectangle(
                playfield.Left, playfield.Bottom - bottomHeight,
                playfield.Width, bottomHeight);
            int hitY = (int)Math.Round(layout.ToScreenY(layout.JudgmentLineY, gear));
            Rectangle judgeRect = new Rectangle(
                playfield.Left,
                hitY - Math.Max(1, (int)Math.Round(51f * packageScale)),
                playfield.Width,
                Math.Max(1, (int)Math.Round(102f * packageScale)));

            int frameLeft = Math.Max(1,
                (int)Math.Round(layout.LeftFrameWidth * packageScale));
            int frameRight = Math.Max(1,
                (int)Math.Round(layout.RightFrameWidth * packageScale));
            DrawSkinImage(graphics, "gear_frame_left",
                new Rectangle(cabinet.Left, cabinet.Top,
                    frameLeft, cabinet.Height));
            DrawSkinImage(graphics, "gear_frame_right",
                new Rectangle(cabinet.Right - frameRight, cabinet.Top,
                    frameRight, cabinet.Height));

            Region previousClip = graphics.Clip;
            graphics.SetClip(playfield);
            // Package shoulders/analog bars are the back layer. Regular key
            // notes remain readable when both types occupy the same X range.
            foreach (ProjectedGameplayNote note in _frame.Notes)
            {
                if (note.LaneRole == GameplayPreviewLaneRole.Regular ||
                    note.RespectType == RespectGameplayNoteType.None ||
                    !IsRespectNoteVisible(layout, note))
                {
                    continue;
                }
                DrawRespectVerticalNote(graphics, layout, gear, lanes, note);
            }
            foreach (ProjectedGameplayNote note in _frame.Notes)
            {
                if (note.LaneRole != GameplayPreviewLaneRole.Regular ||
                    note.RespectType == RespectGameplayNoteType.None ||
                    !IsRespectNoteVisible(layout, note))
                {
                    continue;
                }
                DrawRespectVerticalNote(graphics, layout, gear, lanes, note);
            }
            graphics.Clip = previousClip;
            previousClip.Dispose();

            // Runtime notes sit behind the judgment/deck assembly. Drawing the
            // opaque cover after the notes naturally preserves partially visible
            // heads and removes them only once their complete rectangle is hidden.
            DrawSkinImage(graphics, "gear_default_up_01", capRect);
            if (_gearVideo != null) _gearVideo.Start();
            bool drewVideo = _gearVideo != null &&
                _gearVideo.Draw(graphics, bottomRect);
            if (!drewVideo)
                DrawSkinImage(graphics, "gear_default_bottom", bottomRect);
            if (!DrawSkinImage(graphics, "judge_line_blue", judgeRect))
            {
                graphics.DrawLine(
                    Stroke(Color.FromArgb(70, StudioDesignSystem.PulseCyan), 8f),
                    playfield.Left, hitY, playfield.Right, hitY);
                graphics.DrawLine(
                    Stroke(StudioDesignSystem.PulseCyan, 2f),
                    playfield.Left, hitY, playfield.Right, hitY);
            }

            DrawRespectButtons(graphics, layout, gear, bottomHeight, lanes);
            DrawRespectHud(graphics, layout, gear);
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousPixelOffset;
        }

        private static bool IsRespectNoteVisible(
            RespectGameplayLayout layout,
            ProjectedGameplayNote note)
        {
            if (note.NativeY >= layout.JudgmentLimitY) return true;
            return note.Source.Duration > 0 &&
                note.NativeY + note.NativeHeight >= layout.JudgmentLimitY;
        }

        private void DrawRespectVerticalNote(
            Graphics graphics,
            RespectGameplayLayout layout,
            RectangleF gear,
            int lanes,
            ProjectedGameplayNote note)
        {
            float scale = gear.Width / layout.PlayfieldWidth;
            float x = layout.ToScreenX((float)note.NativeX, gear);
            float y = layout.ToScreenY((float)note.NativeY, gear);
            float nativeWidth = NativeNoteWidth(lanes, note.RespectType);
            float defaultHeight = layout.GetDefaultNoteHeight(note.RespectType);
            int noteWidth = Math.Max(4,
                (int)Math.Round(nativeWidth * scale * _noteZoom));
            int headHeight = Math.Max(3,
                (int)Math.Round(defaultHeight * scale * _noteZoom));
            int noteHeight = Math.Max(headHeight,
                (int)Math.Round(note.NativeHeight * scale));
            var rect = new Rectangle(
                (int)Math.Round(x - (noteWidth / 2f)),
                (int)Math.Round(y + (headHeight / 2f) - noteHeight),
                noteWidth,
                noteHeight);

            string artName = RespectNoteAssetName(note.RespectType, lanes, note.Lane);
            Image art = _noteSkin == null ? null : _noteSkin.Get(artName);
            if (art != null)
            {
                DrawVerticalSlicedImage(graphics, art, rect, headHeight);
            }
            else
            {
                bool shoulder = note.RespectType == RespectGameplayNoteType.L1 ||
                    note.RespectType == RespectGameplayNoteType.L2 ||
                    note.RespectType == RespectGameplayNoteType.R1 ||
                    note.RespectType == RespectGameplayNoteType.R2;
                Color color = note.RespectType == RespectGameplayNoteType.Analog
                    ? Color.Cyan
                    : shoulder ? Color.DeepPink
                    : note.RespectType == RespectGameplayNoteType.Blue
                        ? StudioDesignSystem.PulseCyan
                        : StudioDesignSystem.Frost;
                graphics.FillRectangle(Fill(Color.FromArgb(235, color)), rect);
                graphics.DrawRectangle(
                    Stroke(Color.FromArgb(160, StudioDesignSystem.PulseCyan), 1f), rect);
            }

            if (note.RespectType == RespectGameplayNoteType.Analog)
            {
                double seconds = 0.0;
                if (_document != null && _document.Model.Tempo > 0 &&
                    _document.Model.TickPerMinute > 0)
                {
                    seconds = _frame.CurrentTick * 240.0 /
                        (_document.Model.Tempo * _document.Model.TickPerMinute);
                }
                float alpha = layout.GetAnalogPulseAlpha(seconds);
                graphics.FillRectangle(
                    Fill(Color.FromArgb((int)Math.Round(80 * alpha), 40, 255, 255)),
                    rect);
                graphics.DrawRectangle(
                    Stroke(Color.FromArgb(
                        (int)Math.Round(180 * alpha), 80, 255, 255), 2f),
                    rect);
            }

            if (Math.Abs(note.NativeY - layout.JudgmentLineY) <= 8.0)
            {
                DrawRespectHitEffect(graphics, x, y,
                    Math.Max(headHeight * 2, Math.Min(noteWidth, 72)));
            }
        }

        private void DrawRespectButtons(
            Graphics graphics,
            RespectGameplayLayout layout,
            RectangleF gear,
            int bottomHeight,
            int lanes)
        {
            if (_gearSkin == null || !_gearSkin.IsLoaded) return;

            int regularLanes = lanes == 8 ? 6 : lanes;
            float scale = gear.Width / layout.PlayfieldWidth;
            int buttonWidth = Math.Max(8, (int)Math.Round(80f * scale));
            int buttonHeight = Math.Max(10, (int)Math.Round(106f * scale));
            int y = (int)Math.Round(gear.Bottom - (80f * scale));
            for (int lane = 0; lane < regularLanes; lane++)
            {
                string name = ButtonAssetName(lanes, lane);
                Image image = _gearSkin.Get(name);
                if (image == null) continue;
                int x = (int)Math.Round(layout.ToScreenX(
                    layout.GetTrackX(lane + 3), gear));
                graphics.DrawImage(image,
                    new Rectangle(x - buttonWidth / 2, y,
                        buttonWidth, buttonHeight));
            }

            if (lanes == 8)
            {
                int shoulderWidth = Math.Max(18, (int)Math.Round(234f * scale));
                int shoulderHeight = Math.Max(5, (int)Math.Round(33f * scale));
                int shoulderY = (int)Math.Round(
                    layout.ToScreenY(-272f, gear));
                int left = (int)Math.Round(layout.ToScreenX(-120f, gear));
                int right = (int)Math.Round(layout.ToScreenX(120f, gear));
                DrawSkinImage(graphics, "btn_L1_on", new Rectangle(
                    left - shoulderWidth / 2, shoulderY - shoulderHeight / 2,
                    shoulderWidth, shoulderHeight));
                DrawSkinImage(graphics, "btn_R1_on", new Rectangle(
                    right - shoulderWidth / 2, shoulderY - shoulderHeight / 2,
                    shoulderWidth, shoulderHeight));
            }
        }

        private void DrawRespectHud(
            Graphics graphics,
            RespectGameplayLayout layout,
            RectangleF gear)
        {
            float center = layout.ToScreenX(0f, gear);
            float judgment = layout.ToScreenY(-120f, gear);
            Font label = UtilityFont(7f);
            Font value = DisplayFont(20f);
            if (!_hudMetricsMeasured)
            {
                // Three MeasureString calls on string literals, once, instead of three per frame.
                _hudComboLabelSize = graphics.MeasureString(HudComboLabel, label);
                _hudComboValueSize = graphics.MeasureString(HudComboValue, value);
                _hudMaxSize = graphics.MeasureString(HudMaxLabel, value);
                _hudMetricsMeasured = true;
            }

            graphics.DrawString(HudComboLabel, label,
                Fill(Color.FromArgb(130, 220, 225, 230)),
                center - _hudComboLabelSize.Width / 2f, gear.Top + 18f);
            graphics.DrawString(HudComboValue, value,
                Fill(Color.FromArgb(96, 238, 240, 242)),
                center - _hudComboValueSize.Width / 2f, gear.Top + 30f);
            graphics.DrawString(HudMaxLabel, value,
                Fill(Color.FromArgb(215, 242, 190, 38)),
                center - _hudMaxSize.Width / 2f, judgment - _hudMaxSize.Height / 2f);
        }

        private const string HudComboLabel = "COMBO";
        private const string HudComboValue = "0";
        private const string HudMaxLabel = "MAX 100%";

        private void DrawRespectHitEffect(
            Graphics graphics,
            float x,
            float y,
            int size)
        {
            int radius = Math.Max(8, size / 2);
            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawEllipse(Stroke(Color.FromArgb(80, 20, 220, 255), 5f),
                x - radius, y - radius, radius * 2, radius * 2);
            Pen inner = Stroke(Color.FromArgb(220, 245, 250, 255), 2f);
            graphics.DrawLine(inner, x - radius, y, x + radius, y);
            graphics.DrawLine(inner, x, y - radius, x, y + radius);
            graphics.FillEllipse(Fill(Color.FromArgb(150, 255, 255, 255)),
                x - 4, y - 4, 8, 8);
            graphics.SmoothingMode = previous;
        }

        private static Rectangle RoundRectangle(RectangleF rectangle)
        {
            return Rectangle.FromLTRB(
                (int)Math.Round(rectangle.Left),
                (int)Math.Round(rectangle.Top),
                (int)Math.Round(rectangle.Right),
                (int)Math.Round(rectangle.Bottom));
        }

        private static float NativeNoteWidth(
            int lanes,
            RespectGameplayNoteType type)
        {
            if (type == RespectGameplayNoteType.Analog) return 240f;
            if (type == RespectGameplayNoteType.L1 ||
                type == RespectGameplayNoteType.L2 ||
                type == RespectGameplayNoteType.R1 ||
                type == RespectGameplayNoteType.R2)
            {
                return 244f;
            }
            if (lanes == 4) return 120f;
            if (lanes == 5) return 96f;
            return 80f;
        }

        private static string RespectNoteAssetName(
            RespectGameplayNoteType type,
            int lanes,
            int lane)
        {
            switch (type)
            {
                case RespectGameplayNoteType.Analog: return "note_analog_shine_01";
                case RespectGameplayNoteType.L1: return "note_L1";
                case RespectGameplayNoteType.L2: return "note_L2";
                case RespectGameplayNoteType.R1: return "note_R1";
                case RespectGameplayNoteType.R2: return "note_R2";
            }
            int mode = lanes == 8 ? 6 : lanes;
            string color = type == RespectGameplayNoteType.Blue ? "blue" : "white";
            return "note_" + color + "_shine_" + mode + "_00000";
        }

        private static void DrawVerticalSlicedImage(
            Graphics graphics,
            Image image,
            Rectangle target,
            int headHeight)
        {
            if (target.Height <= headHeight)
            {
                graphics.DrawImage(image, target);
                return;
            }

            int sourceBorder = Math.Max(1, Math.Min(image.Height / 3, 6));
            int targetBorder = Math.Max(1,
                Math.Min(target.Height / 3, headHeight / 3));
            graphics.DrawImage(image,
                new Rectangle(target.Left, target.Top, target.Width, targetBorder),
                new Rectangle(0, 0, image.Width, sourceBorder),
                GraphicsUnit.Pixel);
            graphics.DrawImage(image,
                new Rectangle(target.Left, target.Top + targetBorder,
                    target.Width, target.Height - (targetBorder * 2)),
                new Rectangle(0, sourceBorder, image.Width,
                    image.Height - (sourceBorder * 2)),
                GraphicsUnit.Pixel);
            graphics.DrawImage(image,
                new Rectangle(target.Left, target.Bottom - targetBorder,
                    target.Width, targetBorder),
                new Rectangle(0, image.Height - sourceBorder,
                    image.Width, sourceBorder),
                GraphicsUnit.Pixel);
        }

        private static string ButtonAssetName(int lanes, int lane)
        {
            if (lanes == 8)
            {
                lanes = 6;
            }

            if (lanes == 4)
            {
                string[] names =
                {
                    "btn_4k_left_on", "btn_4k_triangle_on",
                    "btn_4k_circle_on", "btn_4k_up_on_x"
                };
                return names[Math.Max(0, Math.Min(names.Length - 1, lane))];
            }
            if (lanes == 5)
            {
                string[] names =
                {
                    "btn_5k_left_on", "btn_5k_triangle_on",
                    "btn_5k_circle_on", "btn_5k_right_square_on",
                    "btn_5k_up_on"
                };
                return names[Math.Max(0, Math.Min(names.Length - 1, lane))];
            }

            string[] six =
            {
                "btn_6k_left_on", "btn_6k_triangle_on",
                "btn_6k_circle_on", "btn_6k_square_on",
                "btn_6k_up_on", "btn_6k_right_on"
            };
            return six[Math.Max(0, Math.Min(six.Length - 1, lane))];
        }

        private bool DrawSkinImage(Graphics graphics, string name, Rectangle target)
        {
            Image image = _gearSkin == null ? null : _gearSkin.Get(name);
            if (image == null || target.Width <= 0 || target.Height <= 0)
            {
                return false;
            }
            graphics.DrawImage(image, target);
            return true;
        }

        private void DrawHorizontalFallback(Graphics graphics, Rectangle viewport)
        {
            graphics.FillRectangle(Fill(StudioDesignSystem.Deck), viewport);
            graphics.DrawRectangle(Stroke(StudioDesignSystem.Border, 1f), viewport);
            DrawLaneGrid(
                graphics,
                viewport,
                Math.Min(32, _projection.LaneCount),
                Stroke(Color.FromArgb(100, StudioDesignSystem.Border), 1f));

            int playhead = viewport.Left + viewport.Width / 2;
            graphics.DrawLine(
                Stroke(Color.FromArgb(58, StudioDesignSystem.BeatViolet), 8f),
                playhead, viewport.Top, playhead, viewport.Bottom);
            graphics.DrawLine(
                Stroke(StudioDesignSystem.BeatViolet, 2f),
                playhead, viewport.Top, playhead, viewport.Bottom);

            SmoothingMode previousSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (ProjectedGameplayNote note in _frame.Notes)
            {
                if (note.X <= 0.04 || note.X >= 0.96) continue;
                int x = viewport.Left + (int)Math.Round(note.X * viewport.Width);
                int y = viewport.Top + (int)Math.Round(note.Y * viewport.Height);
                int size = Math.Max(6, (int)Math.Round(12 * _noteZoom));
                Color color = note.State == GameplayPreviewNoteState.Active
                    ? StudioDesignSystem.PulseCyan
                    : StudioDesignSystem.Muted;
                graphics.FillEllipse(Fill(color), x - size / 2, y - size / 2, size, size);
            }
            graphics.SmoothingMode = previousSmoothing;
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
                graphics.DrawEllipse(
                    Stroke(Color.FromArgb(140, color), 2f),
                    x - ringSize / 2,
                    y - ringSize / 2,
                    ringSize,
                    ringSize);
            }

            if (note.DurationPulse > 30 &&
                note.Kind != GameplayPreviewNoteKind.Basic &&
                note.Kind != GameplayPreviewNoteKind.ChainHead &&
                note.Kind != GameplayPreviewNoteKind.ChainNode)
            {
                int trail = Math.Max(size, Math.Min(viewport.Width / 3,
                    note.DurationPulse * viewport.Width / (960 * 2)));
                int direction = note.IsTopHalf ? 1 : -1;
                graphics.FillRectangle(
                    Fill(Color.FromArgb(alpha / 2, color)),
                    direction > 0 ? x : x - trail,
                    y - Math.Max(2, size / 6),
                    trail,
                    Math.Max(4, size / 3));
            }

            graphics.FillEllipse(Fill(Color.FromArgb(48, color)),
                x - size, y - size, size * 2, size * 2);

            bool drewAuthenticArt = TechnikaNoteArt.TryDraw(
                graphics,
                ToTechnikaKind(note.Kind),
                x,
                y,
                size + 4,
                alpha / 255f);
            if (!drewAuthenticArt)
            {
                graphics.FillEllipse(Fill(Color.FromArgb(alpha, color)),
                    x - size / 2, y - size / 2, size, size);
                graphics.DrawEllipse(Stroke(StudioDesignSystem.Frost, 1.25f),
                    x - size / 2, y - size / 2, size, size);
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
                ? "No projection warnings"
                : DiagnosticCount + " PROJECTION WARNING" +
                    (DiagnosticCount == 1 ? string.Empty : "S");
            Font status = UtilityFont(7.5f);
            var box = new Rectangle(
                viewport.Left + 10,
                viewport.Top + 10,
                Math.Min(viewport.Width - 20, 455),
                44);
            graphics.FillRectangle(
                Fill(Color.FromArgb(225, StudioDesignSystem.Void)), box);
            graphics.DrawString(
                _projection.StatusLabel,
                status,
                Fill(StudioDesignSystem.Frost),
                box.Left + 10,
                box.Top + 7);
            graphics.DrawString(
                "TICK " + _frame.CurrentTick + "  |  " + diagnostics,
                status,
                Fill(DiagnosticCount == 0
                    ? StudioDesignSystem.Muted
                    : StudioDesignSystem.SignalAmber),
                box.Left + 10,
                box.Top + 24);
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
