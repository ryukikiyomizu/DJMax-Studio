using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using DJMaxEditor.Controls.Vertical;
using DJMaxEditor.UI;

namespace DJMaxEditor.Controls.Editor.Renderers.Zones.Respect
{
    /// <summary>
    /// Gear role of a single Respect source track. This is the horizontal editor's
    /// name for the same identity the vertical timeline stores as
    /// <see cref="VerticalColumnKind"/> + <see cref="VerticalColumnStyle"/>.
    /// </summary>
    internal enum RespectTrackRole
    {
        None,
        BgaSync,
        SideAnalog,
        ButtonWhite,
        ButtonBlue,
        Shoulder,
        Mr,
        Background
    }

    /// <summary>
    /// Resolves a Respect source track to its gear role from the shared
    /// <see cref="VerticalTrackLayout"/>, so the horizontal zone themes, the vertical
    /// ptSequencer columns, and the gameplay preview cannot disagree about which track
    /// is a button, a side analog, a shoulder, or background plumbing.
    /// </summary>
    internal static class RespectTrackRoles
    {
        public static RespectTrackRole ForTrack(VerticalTrackLayout layout, int sourceTrackId)
        {
            if (layout == null) return RespectTrackRole.None;

            return ForColumn(layout.ColumnForSourceTrack(sourceTrackId));
        }

        public static RespectTrackRole ForColumn(VerticalColumn column)
        {
            if (column == null) return RespectTrackRole.None;

            switch (column.Kind)
            {
                case VerticalColumnKind.SideLeft:
                case VerticalColumnKind.SideRight:
                    return RespectTrackRole.SideAnalog;
                case VerticalColumnKind.ShoulderLeft:
                case VerticalColumnKind.ShoulderRight:
                    return RespectTrackRole.Shoulder;
                case VerticalColumnKind.Button:
                    // The ptSequencer preset's alternate shading is the same striping the
                    // gameplay preview calls a "blue" button.
                    return column.Style == VerticalColumnStyle.RegularAlternate
                        ? RespectTrackRole.ButtonBlue
                        : RespectTrackRole.ButtonWhite;
                case VerticalColumnKind.BgaSync:
                    return RespectTrackRole.BgaSync;
                case VerticalColumnKind.Mr:
                    return RespectTrackRole.Mr;
                case VerticalColumnKind.Background:
                    return RespectTrackRole.Background;
                default:
                    return RespectTrackRole.None;
            }
        }
    }

    /// <summary>Shared brushes for the Respect gear roles, taken from the studio palette.</summary>
    internal static class RespectZoneBrushes
    {
        public static readonly Brush Button = new SolidBrush(StudioDesignSystem.Frost);
        public static readonly Brush ButtonAlternate = new SolidBrush(StudioDesignSystem.PulseCyan);
        public static readonly Brush Side = new SolidBrush(StudioDesignSystem.AutomationGreen);
        public static readonly Brush Shoulder = new SolidBrush(StudioDesignSystem.SignalAmber);
        public static readonly Brush Utility = new SolidBrush(StudioDesignSystem.Muted);

        public static Brush ForRole(RespectTrackRole role)
        {
            switch (role)
            {
                case RespectTrackRole.ButtonWhite: return Button;
                case RespectTrackRole.ButtonBlue: return ButtonAlternate;
                case RespectTrackRole.SideAnalog: return Side;
                case RespectTrackRole.Shoulder: return Shoulder;
                case RespectTrackRole.BgaSync:
                case RespectTrackRole.Mr:
                case RespectTrackRole.Background: return Utility;
                default: return null;
            }
        }
    }

    /// <summary>One labelled band of consecutive tracks.</summary>
    internal sealed class RespectZoneBand
    {
        public RespectZoneBand(int from, int to, string text, Brush brush)
        {
            From = from;
            To = to;
            Text = text;
            Brush = brush;
        }

        /// <summary>First source track in the band.</summary>
        public int From { get; private set; }

        /// <summary>Last source track in the band (equal to <see cref="From"/> for a single track).</summary>
        public int To { get; private set; }

        public string Text { get; private set; }

        public Brush Brush { get; private set; }
    }

    /// <summary>
    /// Zone theme for a Respect V chart of one button mode. The band ranges are read
    /// straight out of <see cref="VerticalTrackLayout"/> rather than hard-coded, so a
    /// change to the shared layout moves the horizontal bands with it. On top of the
    /// familiar top/bottom band lines each track also gets a left-edge accent bar in
    /// its gear colour, which is what makes an otherwise anonymous 41-row Respect
    /// chart readable at a glance.
    /// </summary>
    internal abstract class RespectZoneRenderer : ZoneRenderer
    {
        private const int AccentWidth = 6;
        private const int BandLineHeight = 4;

        private readonly VerticalTrackLayout _layout;
        private readonly ReadOnlyCollection<RespectZoneBand> _bands;

        protected RespectZoneRenderer(int mode)
        {
            // Throws for an unsupported mode rather than silently drawing 4B bands.
            _layout = VerticalTrackLayout.ForMode(mode);
            _bands = new ReadOnlyCollection<RespectZoneBand>(BuildBands(_layout));
        }

        internal int Mode
        {
            get { return _layout.Mode; }
        }

        internal VerticalTrackLayout Layout
        {
            get { return _layout; }
        }

        internal ReadOnlyCollection<RespectZoneBand> Bands
        {
            get { return _bands; }
        }

        internal RespectTrackRole RoleForTrack(int sourceTrackId)
        {
            return RespectTrackRoles.ForTrack(_layout, sourceTrackId);
        }

        public override string GetName()
        {
            return "Respect " + _layout.Mode + "B";
        }

        public override string GetDescription()
        {
            return "Lane bands for " + _layout.Mode + "-button Respect V charts.";
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
            DrawAccent(g, trackIndex, trackY, height, bounds);

            for (int i = 0; i < _bands.Count; i++)
            {
                RespectZoneBand band = _bands[i];
                DrawZone(
                    g, trackIndex, trackX, trackY, width, height, bounds,
                    band.Brush, band.Text, band.From, band.To);
            }
        }

        /// <summary>
        /// Left-edge gear-colour bar for this track. It is anchored to
        /// <paramref name="bounds"/> so it stays visible when the chart is scrolled
        /// horizontally, and is narrower than the track-name inset so it never covers
        /// the name the tracks renderer already drew.
        /// </summary>
        private void DrawAccent(
            GraphicsWrapper g,
            int trackIndex,
            int trackY,
            int height,
            Rectangle bounds)
        {
            Brush brush = RespectZoneBrushes.ForRole(RoleForTrack(trackIndex));
            if (brush == null) return;

            var accent = new Rectangle(
                bounds.X,
                trackY + BandLineHeight,
                AccentWidth,
                Math.Max(1, height - (BandLineHeight * 2)));
            accent.Intersect(bounds);
            if (accent.IsEmpty) return;

            FillRectangle(g, brush, accent);
        }

        private static IList<RespectZoneBand> BuildBands(VerticalTrackLayout layout)
        {
            var bands = new List<RespectZoneBand>();

            AddSingle(bands, layout, VerticalColumnKind.BgaSync, "BGA SYNC",
                RespectZoneBrushes.Utility);
            AddSingle(bands, layout, VerticalColumnKind.SideLeft, "SIDE L",
                RespectZoneBrushes.Side);

            int firstButton;
            int lastButton;
            if (TryFindButtonSpan(layout, out firstButton, out lastButton))
            {
                bands.Add(new RespectZoneBand(
                    firstButton,
                    lastButton,
                    layout.Mode + "B KEYS",
                    RespectZoneBrushes.Button));
            }

            AddSingle(bands, layout, VerticalColumnKind.SideRight, "SIDE R",
                RespectZoneBrushes.Side);

            int firstShoulder;
            int lastShoulder;
            if (TryFindShoulderSpan(layout, out firstShoulder, out lastShoulder))
            {
                bands.Add(new RespectZoneBand(
                    firstShoulder, lastShoulder, "L1 / R1", RespectZoneBrushes.Shoulder));
            }

            AddSingle(bands, layout, VerticalColumnKind.Mr, "MR",
                RespectZoneBrushes.Utility);

            int firstBackground;
            int lastBackground;
            if (TrySpan(layout, VerticalColumnKind.Background,
                out firstBackground, out lastBackground))
            {
                bands.Add(new RespectZoneBand(
                    firstBackground, lastBackground, "BG LAYERS",
                    RespectZoneBrushes.Utility));
            }

            return bands;
        }

        private static void AddSingle(
            IList<RespectZoneBand> bands,
            VerticalTrackLayout layout,
            VerticalColumnKind kind,
            string text,
            Brush brush)
        {
            int from;
            int to;
            if (!TrySpan(layout, kind, out from, out to)) return;

            bands.Add(new RespectZoneBand(from, to, text, brush));
        }

        private static bool TryFindButtonSpan(
            VerticalTrackLayout layout, out int from, out int to)
        {
            return TrySpan(layout, VerticalColumnKind.Button, out from, out to);
        }

        private static bool TryFindShoulderSpan(
            VerticalTrackLayout layout, out int from, out int to)
        {
            int leftFrom;
            int leftTo;
            int rightFrom;
            int rightTo;
            bool left = TrySpan(layout, VerticalColumnKind.ShoulderLeft,
                out leftFrom, out leftTo);
            bool right = TrySpan(layout, VerticalColumnKind.ShoulderRight,
                out rightFrom, out rightTo);

            if (!left && !right)
            {
                from = 0;
                to = 0;
                return false;
            }

            from = left ? leftFrom : rightFrom;
            to = right ? rightTo : leftTo;
            return true;
        }

        /// <summary>
        /// Source-track span of every column of one kind. Respect track ids are
        /// contiguous inside a kind, so first/last describes the band exactly.
        /// </summary>
        private static bool TrySpan(
            VerticalTrackLayout layout,
            VerticalColumnKind kind,
            out int from,
            out int to)
        {
            from = int.MaxValue;
            to = int.MinValue;

            foreach (VerticalColumn column in layout.Columns)
            {
                if (column.Kind != kind) continue;
                if (column.SourceTrackId < from) from = column.SourceTrackId;
                if (column.SourceTrackId > to) to = column.SourceTrackId;
            }

            if (from == int.MaxValue)
            {
                from = 0;
                to = 0;
                return false;
            }
            return true;
        }
    }
}
