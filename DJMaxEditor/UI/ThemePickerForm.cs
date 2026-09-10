using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.Controls.Editor.Renderers.Zones;

namespace DJMaxEditor.UI
{
    /// <summary>
    /// Button-opened theme browser. Every note theme and lane theme is listed
    /// with the one-line description its renderer reports, and clicking a row
    /// applies that theme immediately so the chart behind the dialog shows the
    /// result. The toolbar dropdowns stay as the compact switcher; this dialog
    /// is the discoverable one — names alone don't say what a theme is for.
    /// </summary>
    public sealed class ThemePickerForm : Form
    {
        private sealed class ThemeRow
        {
            public string Name;
            public string Description;
            public bool IsCurrent;
            public object Theme;
        }

        private const int MarkerWidth = 22;
        private const int RowPaddingX = 10;
        private const int RowPaddingY = 7;
        private const int NameDescriptionGap = 2;

        private readonly Func<IEventRenderer> _getCurrentEventsTheme;
        private readonly Func<IZoneRenderer> _getCurrentZonesTheme;
        private readonly Action<IEventRenderer> _applyEventsTheme;
        private readonly Action<IZoneRenderer> _applyZonesTheme;
        private readonly ListBox _eventsList;
        private readonly ListBox _zonesList;
        private readonly Font _nameFont;
        private readonly Font _descriptionFont;
        private bool _applying;

        public ThemePickerForm(
            IEnumerable<IEventRenderer> eventsThemes,
            Func<IEventRenderer> getCurrentEventsTheme,
            Action<IEventRenderer> applyEventsTheme,
            IEnumerable<IZoneRenderer> zonesThemes,
            Func<IZoneRenderer> getCurrentZonesTheme,
            Action<IZoneRenderer> applyZonesTheme)
        {
            if (eventsThemes == null) throw new ArgumentNullException("eventsThemes");
            if (getCurrentEventsTheme == null) throw new ArgumentNullException("getCurrentEventsTheme");
            if (applyEventsTheme == null) throw new ArgumentNullException("applyEventsTheme");
            if (zonesThemes == null) throw new ArgumentNullException("zonesThemes");
            if (getCurrentZonesTheme == null) throw new ArgumentNullException("getCurrentZonesTheme");
            if (applyZonesTheme == null) throw new ArgumentNullException("applyZonesTheme");

            _getCurrentEventsTheme = getCurrentEventsTheme;
            _getCurrentZonesTheme = getCurrentZonesTheme;
            _applyEventsTheme = applyEventsTheme;
            _applyZonesTheme = applyZonesTheme;

            Text = "Chart themes";
            ClientSize = new Size(470, 600);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            _nameFont = StudioDesignSystem.BodyFont(9.5f, FontStyle.Bold);
            _descriptionFont = StudioDesignSystem.BodyFont(8.5f);

            Label eventsHeader = SectionHeader("NOTES — HOW NOTES LOOK");
            eventsHeader.Location = new Point(14, 12);
            Controls.Add(eventsHeader);

            _eventsList = ThemeListBox();
            _eventsList.Location = new Point(14, 34);
            _eventsList.Size = new Size(442, 208);
            _eventsList.SelectedIndexChanged += EventsList_SelectedIndexChanged;
            Controls.Add(_eventsList);

            Label zonesHeader = SectionHeader("LANES — HOW LANE BANDS LOOK");
            zonesHeader.Location = new Point(14, 252);
            Controls.Add(zonesHeader);

            _zonesList = ThemeListBox();
            _zonesList.Location = new Point(14, 274);
            _zonesList.Size = new Size(442, 240);
            _zonesList.SelectedIndexChanged += ZonesList_SelectedIndexChanged;
            Controls.Add(_zonesList);

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Location = new Point(14, 522);
            hint.Size = new Size(340, 32);
            hint.Text = "Click a theme to use it right away.\r\nYour choice is remembered next launch.";
            hint.Font = StudioDesignSystem.BodyFont(8.5f);
            Controls.Add(hint);

            Button close = StudioDesignSystem.CreateDeckButton("Close");
            close.DialogResult = DialogResult.Cancel;
            close.Location = new Point(366, 524);
            close.Size = new Size(90, 28);
            Controls.Add(close);
            CancelButton = close;

            // Pre-selecting the current rows fires SelectedIndexChanged; suppress
            // the apply so opening the dialog doesn't re-apply and re-save.
            _applying = true;
            try
            {
                FillRows(_eventsList, DescribeEvents(eventsThemes));
                FillRows(_zonesList, DescribeZones(zonesThemes));
            }
            finally
            {
                _applying = false;
            }
            RefreshCurrentMarkers();

            StudioTheme.ApplyToForm(this);
            eventsHeader.ForeColor = StudioTheme.MutedText;
            zonesHeader.ForeColor = StudioTheme.MutedText;
            hint.ForeColor = StudioTheme.MutedText;
            close.BackColor = StudioDesignSystem.Lift;
            _eventsList.BackColor = StudioTheme.ConsoleBlack;
            _zonesList.BackColor = StudioTheme.ConsoleBlack;
        }

        private static Label SectionHeader(string text)
        {
            Label header = new Label();
            header.AutoSize = true;
            header.Text = text;
            header.Font = StudioDesignSystem.BodyFont(8.5f, FontStyle.Bold);
            header.ForeColor = StudioDesignSystem.Muted;
            return header;
        }

        private ListBox ThemeListBox()
        {
            ListBox list = new ListBox();
            list.DrawMode = DrawMode.OwnerDrawVariable;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.IntegralHeight = false;
            list.HorizontalScrollbar = false;
            list.MeasureItem += ThemeList_MeasureItem;
            list.DrawItem += ThemeList_DrawItem;
            return list;
        }

        private List<ThemeRow> DescribeEvents(IEnumerable<IEventRenderer> themes)
        {
            List<ThemeRow> rows = new List<ThemeRow>();
            IEventRenderer current = _getCurrentEventsTheme();
            foreach (IEventRenderer theme in themes)
            {
                if (theme == null) continue;
                rows.Add(new ThemeRow
                {
                    Name = theme.GetName(),
                    Description = theme.GetDescription(),
                    IsCurrent = theme == current,
                    Theme = theme
                });
            }
            return rows;
        }

        private List<ThemeRow> DescribeZones(IEnumerable<IZoneRenderer> themes)
        {
            List<ThemeRow> rows = new List<ThemeRow>();
            IZoneRenderer current = _getCurrentZonesTheme();
            foreach (IZoneRenderer theme in themes)
            {
                if (theme == null) continue;
                rows.Add(new ThemeRow
                {
                    Name = theme.GetName(),
                    Description = theme.GetDescription(),
                    IsCurrent = theme == current,
                    Theme = theme
                });
            }
            return rows;
        }

        private void FillRows(ListBox list, List<ThemeRow> rows)
        {
            if (rows == null) return;
            list.BeginUpdate();
            try
            {
                foreach (ThemeRow row in rows)
                {
                    list.Items.Add(row);
                    if (row.IsCurrent)
                    {
                        list.SelectedItem = row;
                    }
                }
            }
            finally
            {
                list.EndUpdate();
            }
        }

        private void EventsList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_applying) return;
            ThemeRow row = _eventsList.SelectedItem as ThemeRow;
            if (row == null || row.Theme == null) return;
            ApplyEventsChoice((IEventRenderer)row.Theme);
        }

        private void ZonesList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_applying) return;
            ThemeRow row = _zonesList.SelectedItem as ThemeRow;
            if (row == null || row.Theme == null) return;
            ApplyZonesChoice((IZoneRenderer)row.Theme);
        }

        private void ApplyEventsChoice(IEventRenderer theme)
        {
            _applying = true;
            try
            {
                _applyEventsTheme(theme);
            }
            finally
            {
                _applying = false;
            }
            RefreshCurrentMarkers();
        }

        private void ApplyZonesChoice(IZoneRenderer theme)
        {
            _applying = true;
            try
            {
                _applyZonesTheme(theme);
            }
            finally
            {
                _applying = false;
            }
            RefreshCurrentMarkers();
        }

        /// <summary>
        /// Re-reads the live selection after an apply rather than assuming the
        /// click took: the apply callbacks own the editor state, this dialog
        /// only mirrors it.
        /// </summary>
        private void RefreshCurrentMarkers()
        {
            IEventRenderer currentEvents = _getCurrentEventsTheme();
            IZoneRenderer currentZones = _getCurrentZonesTheme();
            foreach (ThemeRow row in _eventsList.Items)
            {
                row.IsCurrent = row.Theme == (object)currentEvents;
            }
            foreach (ThemeRow row in _zonesList.Items)
            {
                row.IsCurrent = row.Theme == (object)currentZones;
            }
            _eventsList.Invalidate();
            _zonesList.Invalidate();
        }

        private void ThemeList_MeasureItem(object sender, MeasureItemEventArgs e)
        {
            ListBox list = sender as ListBox;
            ThemeRow row = list != null && e.Index >= 0 && e.Index < list.Items.Count
                ? list.Items[e.Index] as ThemeRow
                : null;
            if (list == null || row == null)
            {
                e.ItemHeight = _nameFont.Height + (RowPaddingY * 2);
                return;
            }

            int textWidth = Math.Max(50, list.ClientSize.Width - MarkerWidth - (RowPaddingX * 2));
            Size proposed = new Size(textWidth, 0);
            int descriptionHeight = TextRenderer.MeasureText(
                row.Description ?? string.Empty,
                _descriptionFont,
                proposed,
                TextFormatFlags.WordBreak).Height;
            e.ItemHeight = RowPaddingY + _nameFont.Height + NameDescriptionGap
                + descriptionHeight + RowPaddingY;
        }

        private void ThemeList_DrawItem(object sender, DrawItemEventArgs e)
        {
            ListBox list = sender as ListBox;
            if (list == null || e.Index < 0 || e.Index >= list.Items.Count) return;
            ThemeRow row = list.Items[e.Index] as ThemeRow;
            if (row == null) return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color backColor = selected ? StudioDesignSystem.Selected : list.BackColor;
            using (SolidBrush back = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(back, e.Bounds);
            }

            // Current-theme dot. Drawn for every row so names always start at
            // the same x whether or not the dot is lit.
            string marker = row.IsCurrent ? "●" : "○";
            Color markerColor = row.IsCurrent ? StudioDesignSystem.PulseCyan : StudioDesignSystem.Border;
            TextRenderer.DrawText(
                e.Graphics,
                marker,
                _descriptionFont,
                new Rectangle(e.Bounds.X + RowPaddingX, e.Bounds.Y + RowPaddingY, MarkerWidth, _nameFont.Height),
                markerColor,
                TextFormatFlags.NoPrefix);

            int textX = e.Bounds.X + RowPaddingX + MarkerWidth;
            int textWidth = e.Bounds.Width - RowPaddingX - MarkerWidth - RowPaddingX;
            TextRenderer.DrawText(
                e.Graphics,
                row.Name ?? string.Empty,
                _nameFont,
                new Rectangle(textX, e.Bounds.Y + RowPaddingY, textWidth, _nameFont.Height),
                StudioDesignSystem.Frost,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            Color descriptionColor = selected ? StudioDesignSystem.Frost : StudioDesignSystem.Muted;
            TextRenderer.DrawText(
                e.Graphics,
                row.Description ?? string.Empty,
                _descriptionFont,
                new Rectangle(
                    textX,
                    e.Bounds.Y + RowPaddingY + _nameFont.Height + NameDescriptionGap,
                    textWidth,
                    e.Bounds.Height),
                descriptionColor,
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);

            e.DrawFocusRectangle();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_nameFont != null) _nameFont.Dispose();
                if (_descriptionFont != null) _descriptionFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
