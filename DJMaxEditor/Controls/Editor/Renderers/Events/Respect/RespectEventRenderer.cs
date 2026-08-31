using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using DJMaxEditor.Controls.Editor.Renderers.Zones.Respect;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.DJMax;
using DJMaxEditor.UI;

namespace DJMaxEditor.Controls.Editor.Renderers.Events
{
    /// <summary>
    /// Default event art for Respect V charts.
    /// <para>
    /// Respect charts do not use the Technika/Trilogy attribute vocabulary — the
    /// gameplay preview deliberately classifies a Respect note by the <em>track</em> it
    /// sits on (side analog, shoulder, striped button, background plumbing), not by its
    /// attribute byte. This theme follows the same rule, so an event drawn here can
    /// never claim to be something the preview would play differently. Note art is
    /// drawn from the studio palette rather than the legacy Technika glyph set, because
    /// borrowing a chain/repeat glyph would label a Respect note with a meaning it does
    /// not have.
    /// </para>
    /// </summary>
    internal class RespectThemeRenderer : EventRenderer
    {
        /// <summary>Attribute byte reserved for the video-start marker in every format.</summary>
        internal const byte VideoStartAttribute = (byte)EventAttribute.VideoStart;

        /// <summary>Durations at or below this are taps rather than holds.</summary>
        internal const int TapDurationLimit = 6;

        private const int NoteWidth = 96;
        private const int NoteHeight = 52;
        private const int HoldHeight = 34;
        private const int MarkerSize = 90;
        private const int ShadowDistance = 3;

        private static readonly Brush ButtonBrush =
            new SolidBrush(StudioDesignSystem.Frost);
        private static readonly Brush ButtonAlternateBrush =
            new SolidBrush(StudioDesignSystem.PulseCyan);
        private static readonly Brush SideBrush =
            new SolidBrush(StudioDesignSystem.AutomationGreen);
        private static readonly Brush ShoulderBrush =
            new SolidBrush(StudioDesignSystem.SignalAmber);
        private static readonly Brush UtilityBrush =
            new SolidBrush(StudioDesignSystem.Muted);
        private static readonly Brush UnmappedBrush =
            new SolidBrush(StudioDesignSystem.Disabled);
        private static readonly Brush LabelBrush =
            new SolidBrush(StudioDesignSystem.Void);
        private static readonly Pen OutlinePen =
            new Pen(StudioDesignSystem.Void, 2f);

        private readonly Font _textFont = new Font("Tahoma", 20, FontStyle.Bold);
        private readonly StringFormat _stringFormat = new StringFormat();

        /// <summary>Cached because <see cref="Font.GetHeight()"/> is a native call.</summary>
        private readonly float _fontHeight;

        /// <summary>
        /// Layout cache keyed by button mode. Every layout is immutable, so one instance
        /// per mode is shared instead of rebuilt per painted note.
        /// </summary>
        private readonly Dictionary<int, VerticalTrackLayout> _layouts =
            new Dictionary<int, VerticalTrackLayout>();

        private int _mode = DefaultMode;

        /// <summary>
        /// 6B is the widest layout that still covers every mode-independent track
        /// (side analogs, BGA SYNC, MR, BG) plus the full 3-8 button span, so it is the
        /// safe default before a document announces its real mode.
        /// </summary>
        internal const int DefaultMode = 6;

        public RespectThemeRenderer()
        {
            _stringFormat.Alignment = StringAlignment.Center;
            _stringFormat.LineAlignment = StringAlignment.Center;
            _fontHeight = _textFont.GetHeight();
        }

        /// <summary>
        /// Button mode the theme colours against. Ignored when unsupported, so a
        /// mis-detected chart keeps the previous striping instead of losing its art.
        /// </summary>
        internal int Mode
        {
            get { return _mode; }
            set
            {
                if (!VerticalTrackLayout.IsSupportedMode(value)) return;
                _mode = value;
            }
        }

        public override string GetName()
        {
            return "Respect";
        }

        public override void DrawZones(
            GraphicsWrapper g,
            int trackIndex,
            int trackX,
            int trackY,
            int width,
            int height,
            Rectangle bounds)
        {
            // Track banding is the zone theme's job (Respect 4B/5B/6B/8B), exactly as
            // it is for Trilogy.
        }

        public override IEnumerable<KeyValuePair<string, EventData>> GetTemplates()
        {
            var list = new List<KeyValuePair<string, EventData>>();

            list.Add(CreateNoteFromEventData(
                "Basic note",
                new EventData
                {
                    EventType = EventType.Note,
                    Attribute = (byte)EventAttribute.BasicNote,
                }));

            list.Add(CreateNoteFromEventData(
                "Long note",
                new EventData
                {
                    EventType = EventType.Note,
                    Attribute = (byte)EventAttribute.BasicNote,
                    Duration = 9,
                }));

            list.Add(CreateNoteFromEventData(
                "Tempo",
                new EventData { EventType = EventType.Tempo }));

            list.Add(CreateNoteFromEventData(
                "Volume",
                new EventData { EventType = EventType.Volume }));

            list.Add(CreateNoteFromEventData(
                "Beat",
                new EventData { EventType = EventType.Beat }));

            list.Add(CreateNoteFromEventData(
                "Video start",
                new EventData
                {
                    EventType = EventType.Note,
                    Attribute = VideoStartAttribute,
                }));

            return list;
        }

        public override void RenderEventData(
            GraphicsWrapper g,
            EventData eventData,
            Rectangle eventRectangle,
            int centerX,
            int centerY)
        {
            switch (eventData.EventType)
            {
                case EventType.Note:
                    RenderNote(g, eventData, eventRectangle, centerX, centerY);
                    break;
                case EventType.Tempo:
                    DrawImage(g, DJMRessources.metronome,
                        centerX - (MarkerSize / 2), centerY - (MarkerSize / 2),
                        MarkerSize, MarkerSize);
                    break;
                case EventType.Volume:
                    DrawImage(g, DJMRessources.volume,
                        centerX - (64 / 2), centerY - (64 / 2), 64, 64);
                    break;
                case EventType.Beat:
                    DrawImage(g, DJMRessources.beat,
                        centerX - (MarkerSize / 2), centerY - (MarkerSize / 2),
                        MarkerSize, MarkerSize);
                    break;
                default:
                    DrawImage(g, DJMRessources.unknowNote,
                        eventRectangle.X, eventRectangle.Y, MarkerSize, MarkerSize);
                    break;
            }
        }

        public override void RenderNote(
            GraphicsWrapper g,
            EventData eventData,
            Rectangle eventRectangle,
            int centerX,
            int centerY)
        {
            if (eventData.Attribute == VideoStartAttribute)
            {
                DrawImage(g, DJMRessources.videoStart,
                    centerX - (MarkerSize / 2), centerY - (MarkerSize / 2),
                    MarkerSize, MarkerSize);
                return;
            }

            RespectTrackRole role = RoleForTrack((int)eventData.TrackId);
            Brush brush = BrushForRole(role);
            bool hold = eventData.Duration > TapDurationLimit;

            if (hold)
            {
                // The hold body runs from the head to the tail so the sustain is visible
                // at the same length the chart stores, then the head is drawn over it.
                var body = new Rectangle(
                    centerX,
                    centerY - (HoldHeight / 2),
                    Math.Max(1, (int)eventData.VirtualDuration),
                    HoldHeight);
                g.FillRectangle(brush, body.X, body.Y, body.Width, body.Height);
            }

            int headX = centerX - (NoteWidth / 2);
            int headY = centerY - (NoteHeight / 2);
            g.FillRectangle(brush, headX, headY, NoteWidth, NoteHeight);
            g.DrawRectangle(OutlinePen, headX, headY, NoteWidth, NoteHeight);

            string text = DescribeEvent(eventData);
            if (text.Length == 0) return;

            // A 20pt label under the V2 timeline's 0.2x theme-art transform lands below 6px
            // tall. Skipping it is both the readable choice and the cheap one.
            if (!TextImageCache.IsLegible(_fontHeight, g.LabelScale)) return;

            // Every Respect gear colour is light or saturated, so the label reads as dark
            // ink over a pale shadow rather than the Null theme's white-on-dark. It is
            // rasterised once per distinct label and blitted after that: GDI+ has no cached
            // glyph path under a world transform, and both surfaces draw notes through one.
            TextImage label = TextImageCache.Get(
                _textFont, text, StudioDesignSystem.Void, Color.White, ShadowDistance);
            if (label != null)
            {
                g.DrawLabel(label.Image, label.CenteredOn(centerX, centerY));
                return;
            }

            g.DrawString(text, _textFont, Brushes.White,
                centerX + ShadowDistance, centerY + ShadowDistance, _stringFormat);
            g.DrawString(text, _textFont, LabelBrush, centerX, centerY, _stringFormat);
        }

        internal RespectTrackRole RoleForTrack(int sourceTrackId)
        {
            return RespectTrackRoles.ForTrack(LayoutForMode(_mode), sourceTrackId);
        }

        internal static Brush BrushForRole(RespectTrackRole role)
        {
            switch (role)
            {
                case RespectTrackRole.ButtonWhite: return ButtonBrush;
                case RespectTrackRole.ButtonBlue: return ButtonAlternateBrush;
                case RespectTrackRole.SideAnalog: return SideBrush;
                case RespectTrackRole.Shoulder: return ShoulderBrush;
                case RespectTrackRole.BgaSync:
                case RespectTrackRole.Mr:
                case RespectTrackRole.Background: return UtilityBrush;
                default:
                    // A note on a track the Respect layout does not describe is drawn
                    // in the disabled colour instead of being coloured like gameplay.
                    return UnmappedBrush;
            }
        }

        /// <summary>
        /// Label drawn inside the note head. The inspector display modes win when one is
        /// active; otherwise a Respect note carries no attribute vocabulary worth
        /// printing, so nothing is drawn — and an empty label skips the text calls
        /// entirely rather than drawing nothing twice.
        /// </summary>
        private string DescribeEvent(EventData eventData)
        {
            return EventLabelCache.For(EventDisplayMode, eventData);
        }

        private VerticalTrackLayout LayoutForMode(int mode)
        {
            VerticalTrackLayout layout;
            if (_layouts.TryGetValue(mode, out layout)) return layout;

            layout = VerticalTrackLayout.ForMode(mode);
            _layouts[mode] = layout;
            return layout;
        }
    }
}
