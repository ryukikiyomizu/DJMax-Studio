using System;
using System.Drawing;

namespace DJMaxEditor.Preview
{
    /// <summary>
    /// Native coordinate rules used by the RESPECT maingame scene. Values are
    /// kept in the package's 502-unit lane space. The left and right cabinet
    /// frames are separate package sprites, so the complete scene is wider
    /// than the playable lane core.
    /// </summary>
    public sealed class RespectGameplayLayout
    {
        public const float DefaultNoteSpeed = 4.5f;

        private readonly int _mode;

        /// <summary>
        /// The lane count as a RESPECT gear mode, or 0 when there is no gear for it.
        /// <para>
        /// This lives with the layout rather than with the artwork because it answers a geometry
        /// question - "is there a gear for this many lanes" - that the projector has to ask before
        /// any skin is loaded, and the projector is shared with the WPF shell, which has no
        /// System.Drawing image loader at all. <see cref="RespectVerticalSkin"/> forwards here so
        /// there is still one answer.
        /// </para>
        /// </summary>
        public static int NormalizeLaneMode(int laneCount)
        {
            return laneCount == 4 || laneCount == 5 || laneCount == 6 || laneCount == 8
                ? laneCount
                : 0;
        }

        private RespectGameplayLayout(int mode)
        {
            _mode = mode;
        }

        public float PlayfieldWidth { get { return 502f; } }

        public float LeftFrameWidth { get { return 91f; } }

        public float RightFrameWidth { get { return 54f; } }

        public float SceneWidth
        {
            // The lane camera stays centered. The narrower right artwork sits
            // against the outside edge of a gutter matching the left frame.
            get { return PlayfieldWidth + (LeftFrameWidth * 2f); }
        }

        // The default gear frame is authored as a 502 x 1080 package sprite.
        // Keep that full coordinate space so frame, deck and buttons share one
        // uniform scale instead of being independently stretched.
        public float PlayfieldHeight { get { return 1080f; } }

        public float JudgmentLineY { get { return -217f; } }

        public float JudgmentLimitY { get { return -285f; } }

        public RectangleF FitScene(RectangleF viewport)
        {
            if (viewport.Width <= 0f || viewport.Height <= 0f)
            {
                return RectangleF.Empty;
            }

            float scale = Math.Min(
                viewport.Width / SceneWidth,
                viewport.Height / PlayfieldHeight);
            float width = SceneWidth * scale;
            float height = PlayfieldHeight * scale;
            return new RectangleF(
                viewport.Left + ((viewport.Width - width) / 2f),
                viewport.Top + ((viewport.Height - height) / 2f),
                width,
                height);
        }

        public RectangleF GetPlayfield(RectangleF scene)
        {
            if (scene.Width <= 0f || scene.Height <= 0f)
            {
                return RectangleF.Empty;
            }

            float scale = scene.Width / SceneWidth;
            return new RectangleF(
                scene.Left + (LeftFrameWidth * scale),
                scene.Top,
                PlayfieldWidth * scale,
                scene.Height);
        }

        public RectangleF FitPlayfield(RectangleF viewport)
        {
            return GetPlayfield(FitScene(viewport));
        }

        public float ToScreenX(float nativeX, RectangleF playfield)
        {
            return playfield.Left + ((nativeX + (PlayfieldWidth / 2f)) *
                playfield.Width / PlayfieldWidth);
        }

        public float ToScreenY(float nativeY, RectangleF playfield)
        {
            // The runtime camera is vertically offset inside the 1080-high
            // frame. At -217 the judge line meets the 244-high bottom deck.
            const float nativeTop = 619f;
            return playfield.Top + ((nativeTop - nativeY) *
                playfield.Height / PlayfieldHeight);
        }

        public static RespectGameplayLayout ForMode(int mode)
        {
            if (mode != 4 && mode != 5 && mode != 6 && mode != 8)
            {
                throw new ArgumentOutOfRangeException("mode");
            }
            return new RespectGameplayLayout(mode);
        }

        /// <summary>
        /// Native X of a trailer track: the button lanes on the mode's pitch, the side tracks
        /// (2/9) and the shoulder inputs (10/11 L1/R1, 12/13 L2/R2) at the centre of their half.
        /// <para>
        /// The half-centre is a centre, not a lane: shoulders and sides are wide bars spanning
        /// from their half's outer lane edge to the middle (see
        /// <c>docs/respectv-playfield-research.md</c>), so a renderer must draw them at a bar
        /// width around this X, never at a lane width. Drawing a lane-width note here is what
        /// used to park a cyan bar on top of the mains.
        /// </para>
        /// </summary>
        public float GetTrackX(int trackIndex)
        {
            switch (trackIndex)
            {
                case 2:
                case 10:
                case 12:
                    return -120f;
                case 9:
                case 11:
                case 13:
                    return 120f;
            }

            if (trackIndex < 3 || trackIndex > 8)
            {
                throw new ArgumentOutOfRangeException("trackIndex");
            }

            if (_mode == 4) return (120f * trackIndex) - 540f;
            if (_mode == 5) return (96f * trackIndex) - 480f;
            return (80f * trackIndex) - 440f;
        }

        public float GetNoteY(int eventTick, int currentTick, float noteSpeed)
        {
            if (noteSpeed <= 0f) throw new ArgumentOutOfRangeException("noteSpeed");
            return ((eventTick - currentTick) * noteSpeed) + JudgmentLineY;
        }

        public float GetLongNoteHeight(
            int duration,
            float noteSpeed,
            float defaultHeight)
        {
            if (duration < 0) throw new ArgumentOutOfRangeException("duration");
            if (noteSpeed <= 0f) throw new ArgumentOutOfRangeException("noteSpeed");
            if (defaultHeight < 0f) throw new ArgumentOutOfRangeException("defaultHeight");
            return Math.Max(duration * noteSpeed, defaultHeight);
        }

        public float GetDefaultNoteHeight(RespectGameplayNoteType type)
        {
            return 24f;
        }

        public float GetAnalogPulseAlpha(double playbackSeconds)
        {
            double phase = playbackSeconds % 0.5;
            if (phase < 0) phase += 0.5;
            double normalized = phase <= 0.25
                ? phase / 0.25
                : (0.5 - phase) / 0.25;
            double smooth = normalized * normalized * (3.0 - (2.0 * normalized));
            return (float)(0.2 + (0.8 * smooth));
        }
    }
}
