using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace DJMaxEditor.UI
{
    /// <summary>
    /// Shared visual language for the editor shell.  The timeline keeps its own
    /// renderer; this class makes the surrounding WinForms chrome feel like the
    /// same chart-authoring workstation.
    /// </summary>
    public static class StudioTheme
    {
        private static readonly ConditionalWeakTable<Control, object> NativeDarkHooks =
            new ConditionalWeakTable<Control, object>();

        public static readonly Color ConsoleBlack = StudioDesignSystem.Void;
        public static readonly Color PanelGraphite = StudioDesignSystem.Deck;
        public static readonly Color RaisedSlate = StudioDesignSystem.Lift;
        public static readonly Color HoverSlate = StudioDesignSystem.Hover;
        public static readonly Color Border = StudioDesignSystem.Border;
        public static readonly Color TimingCyan = StudioDesignSystem.PulseCyan;
        public static readonly Color SelectionViolet = StudioDesignSystem.BeatViolet;
        public static readonly Color SignalAmber = StudioDesignSystem.SignalAmber;
        public static readonly Color FaultRed = StudioDesignSystem.FaultRed;
        public static readonly Color PrimaryText = StudioDesignSystem.Frost;
        public static readonly Color MutedText = StudioDesignSystem.Muted;
        public static readonly Color DeepSelection = StudioDesignSystem.Selected;

        public static Font BodyFont(float size = 9f)
        {
            return StudioDesignSystem.BodyFont(size);
        }

        public static Font StrongFont(float size = 9f)
        {
            return StudioDesignSystem.BodyFont(size, FontStyle.Bold);
        }

        public static Font MonoFont(float size = 9f)
        {
            return StudioDesignSystem.UtilityFont(size);
        }

        public static void ApplyMainShell(
            Form form,
            MenuStrip menu,
            ToolStrip commandBar,
            DockPanel dockPanel)
        {
            form.BackColor = ConsoleBlack;
            form.ForeColor = PrimaryText;
            form.Font = BodyFont();
            form.MinimumSize = new Size(1040, 680);

            menu.AutoSize = false;
            menu.Height = 32;
            menu.Padding = new Padding(8, 4, 8, 3);
            ApplyToolStrip(menu, false);

            commandBar.AutoSize = false;
            commandBar.Height = 46;
            commandBar.Padding = new Padding(9, 6, 9, 6);
            commandBar.GripStyle = ToolStripGripStyle.Hidden;
            commandBar.ImageScalingSize = new Size(18, 18);
            ApplyToolStrip(commandBar, true);

            dockPanel.DockBackColor = ConsoleBlack;
            dockPanel.BackColor = ConsoleBlack;
            dockPanel.Skin = CreateDockSkin();

            TryApplyDarkTitleBar(form);
            ApplyNativeDarkMode(form);
        }

        public static void ApplyToolStrip(ToolStrip strip, bool accentBottom)
        {
            strip.BackColor = PanelGraphite;
            strip.ForeColor = PrimaryText;
            strip.Font = BodyFont();
            strip.RenderMode = ToolStripRenderMode.Professional;
            strip.Renderer = new StudioToolStripRenderer(accentBottom);

            foreach (ToolStripItem item in strip.Items)
            {
                StyleToolStripItem(item);
            }
        }

        public static void ApplyToForm(Form form)
        {
            form.BackColor = PanelGraphite;
            form.ForeColor = PrimaryText;
            form.Font = BodyFont();
            TryApplyDarkTitleBar(form);
            ApplyToControlTree(form);
            ApplyNativeDarkMode(form);
        }

        public static void ApplyToControlTree(Control root)
        {
            foreach (Control control in root.Controls)
            {
                if (control is DataGridView)
                {
                    ApplyDataGrid((DataGridView)control);
                }
                else if (control is PropertyGrid)
                {
                    ApplyPropertyGrid((PropertyGrid)control);
                }
                else if (control is RichTextBox)
                {
                    var richText = (RichTextBox)control;
                    richText.BackColor = ConsoleBlack;
                    richText.ForeColor = PrimaryText;
                    richText.Font = MonoFont(8.5f);
                    richText.BorderStyle = BorderStyle.None;
                }
                else if (control is StatusStrip)
                {
                    ApplyToolStrip((StatusStrip)control, false);
                }
                else if (control is ToolStrip)
                {
                    ApplyToolStrip((ToolStrip)control, false);
                }
                else
                {
                    control.BackColor = PanelGraphite;
                    control.ForeColor = PrimaryText;
                }

                ApplyToControlTree(control);
            }
        }

        public static void ApplyDataGrid(DataGridView grid)
        {
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = ConsoleBlack;
            grid.GridColor = Border;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.Font = BodyFont(8.5f);
            grid.RowTemplate.Height = Math.Max(grid.RowTemplate.Height, 30);

            grid.DefaultCellStyle.BackColor = PanelGraphite;
            grid.DefaultCellStyle.ForeColor = PrimaryText;
            grid.DefaultCellStyle.SelectionBackColor = DeepSelection;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Padding = new Padding(6, 2, 6, 2);

            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(20, 26, 35);
            grid.AlternatingRowsDefaultCellStyle.ForeColor = PrimaryText;
            grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = DeepSelection;
            grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.White;

            grid.ColumnHeadersDefaultCellStyle.BackColor = RaisedSlate;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = MutedText;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = RaisedSlate;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = PrimaryText;
            grid.ColumnHeadersDefaultCellStyle.Font = StrongFont(8f);

            grid.RowHeadersDefaultCellStyle.BackColor = PanelGraphite;
            grid.RowHeadersDefaultCellStyle.ForeColor = MutedText;

            if (grid.ContextMenuStrip != null)
            {
                ApplyToolStrip(grid.ContextMenuStrip, false);
            }
        }

        public static void ApplyPropertyGrid(PropertyGrid grid)
        {
            grid.BackColor = PanelGraphite;
            grid.ViewBackColor = PanelGraphite;
            grid.ViewForeColor = PrimaryText;
            grid.ViewBorderColor = Border;
            grid.LineColor = Border;
            grid.CategoryForeColor = TimingCyan;
            grid.CategorySplitterColor = Border;
            grid.CommandsBackColor = ConsoleBlack;
            grid.CommandsForeColor = PrimaryText;
            grid.CommandsBorderColor = Border;
            grid.HelpBackColor = ConsoleBlack;
            grid.HelpForeColor = MutedText;
            grid.HelpBorderColor = Border;
            grid.SelectedItemWithFocusBackColor = DeepSelection;
            grid.SelectedItemWithFocusForeColor = Color.White;
            grid.Font = BodyFont(8.5f);
        }

        public static DockPanelSkin CreateDockSkin()
        {
            var skin = new DockPanelSkin();

            var autoHide = new AutoHideStripSkin();
            autoHide.DockStripGradient = SolidDockGradient(ConsoleBlack);
            autoHide.TabGradient = SolidTabGradient(PanelGraphite, MutedText);
            autoHide.TextFont = StrongFont(8f);
            skin.AutoHideStripSkin = autoHide;

            var pane = new DockPaneStripSkin();
            pane.TextFont = StrongFont(8.25f);

            var documents = new DockPaneStripGradient();
            documents.DockStripGradient = SolidDockGradient(ConsoleBlack);
            documents.ActiveTabGradient = SolidTabGradient(RaisedSlate, PrimaryText);
            documents.InactiveTabGradient = SolidTabGradient(PanelGraphite, MutedText);
            pane.DocumentGradient = documents;

            var tools = new DockPaneStripToolWindowGradient();
            tools.DockStripGradient = SolidDockGradient(ConsoleBlack);
            tools.ActiveCaptionGradient = SolidTabGradient(RaisedSlate, TimingCyan);
            tools.InactiveCaptionGradient = SolidTabGradient(PanelGraphite, MutedText);
            tools.ActiveTabGradient = SolidTabGradient(RaisedSlate, PrimaryText);
            tools.InactiveTabGradient = SolidTabGradient(PanelGraphite, MutedText);
            pane.ToolWindowGradient = tools;

            skin.DockPaneStripSkin = pane;
            return skin;
        }

        public static Bitmap CreatePlayIcon(Color color)
        {
            var bitmap = NewIconBitmap();
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Brush brush = new SolidBrush(color))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillPolygon(brush, new[]
                {
                    new PointF(6f, 3.5f),
                    new PointF(15f, 9f),
                    new PointF(6f, 14.5f)
                });
            }
            return bitmap;
        }

        public static Bitmap CreateStopIcon(Color color)
        {
            var bitmap = NewIconBitmap();
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Brush brush = new SolidBrush(color))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillRectangle(brush, 5, 5, 9, 9);
            }
            return bitmap;
        }

        public static Bitmap CreatePauseIcon(Color color)
        {
            var bitmap = NewIconBitmap();
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Brush brush = new SolidBrush(color))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillRectangle(brush, 5, 4, 3, 10);
                graphics.FillRectangle(brush, 11, 4, 3, 10);
            }
            return bitmap;
        }

        public static void TryApplyDarkTitleBar(Form form)
        {
            if (form == null || !form.IsHandleCreated || Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }

            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        public static void ApplyNativeDarkMode(Control root)
        {
            if (root == null || Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }

            ApplyNativeDarkModeToControl(root);
            foreach (Control child in root.Controls)
            {
                ApplyNativeDarkMode(child);
            }
        }

        private static void ApplyNativeDarkModeToControl(Control control)
        {
            object marker;
            if (NativeDarkHooks.TryGetValue(control, out marker))
            {
                return;
            }
            NativeDarkHooks.Add(control, new object());

            EventHandler apply = delegate(object sender, EventArgs args)
            {
                var target = sender as Control;
                if (target == null)
                {
                    return;
                }

                try
                {
                    SetWindowTheme(target.Handle, "DarkMode_Explorer", null);
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }
            };

            control.HandleCreated += apply;
            if (control.IsHandleCreated)
            {
                apply(control, EventArgs.Empty);
            }
        }

        private static void StyleToolStripItem(ToolStripItem item)
        {
            item.ForeColor = PrimaryText;
            item.Font = BodyFont();

            var menuItem = item as ToolStripMenuItem;
            if (menuItem != null)
            {
                menuItem.DropDown.BackColor = PanelGraphite;
                menuItem.DropDown.ForeColor = PrimaryText;
                menuItem.DropDown.Renderer = new StudioToolStripRenderer(false);
                foreach (ToolStripItem child in menuItem.DropDownItems)
                {
                    StyleToolStripItem(child);
                }
            }

            var dropDownButton = item as ToolStripDropDownButton;
            if (dropDownButton != null)
            {
                dropDownButton.DropDown.BackColor = PanelGraphite;
                dropDownButton.DropDown.ForeColor = PrimaryText;
                dropDownButton.DropDown.Renderer = new StudioToolStripRenderer(false);
                foreach (ToolStripItem child in dropDownButton.DropDownItems)
                {
                    StyleToolStripItem(child);
                }
            }
        }

        private static DockPanelGradient SolidDockGradient(Color color)
        {
            return new DockPanelGradient
            {
                StartColor = color,
                EndColor = color
            };
        }

        private static TabGradient SolidTabGradient(Color background, Color foreground)
        {
            return new TabGradient
            {
                StartColor = background,
                EndColor = background,
                TextColor = foreground,
                LinearGradientMode = LinearGradientMode.Horizontal
            };
        }

        private static Bitmap NewIconBitmap()
        {
            var bitmap = new Bitmap(18, 18);
            bitmap.MakeTransparent();
            return bitmap;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int attributeValue,
            int attributeSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(
            IntPtr hwnd,
            string subAppName,
            string subIdList);
    }

    public sealed class StudioColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return StudioTheme.PanelGraphite; } }
        public override Color ImageMarginGradientBegin { get { return StudioTheme.ConsoleBlack; } }
        public override Color ImageMarginGradientMiddle { get { return StudioTheme.ConsoleBlack; } }
        public override Color ImageMarginGradientEnd { get { return StudioTheme.ConsoleBlack; } }
        public override Color MenuBorder { get { return StudioTheme.Border; } }
        public override Color MenuItemBorder { get { return StudioTheme.TimingCyan; } }
        public override Color MenuItemSelected { get { return StudioTheme.HoverSlate; } }
        public override Color MenuItemSelectedGradientBegin { get { return StudioTheme.HoverSlate; } }
        public override Color MenuItemSelectedGradientEnd { get { return StudioTheme.HoverSlate; } }
        public override Color MenuItemPressedGradientBegin { get { return StudioTheme.RaisedSlate; } }
        public override Color MenuItemPressedGradientMiddle { get { return StudioTheme.RaisedSlate; } }
        public override Color MenuItemPressedGradientEnd { get { return StudioTheme.RaisedSlate; } }
        public override Color ToolStripBorder { get { return StudioTheme.Border; } }
        public override Color ToolStripGradientBegin { get { return StudioTheme.PanelGraphite; } }
        public override Color ToolStripGradientMiddle { get { return StudioTheme.PanelGraphite; } }
        public override Color ToolStripGradientEnd { get { return StudioTheme.PanelGraphite; } }
        public override Color ButtonSelectedBorder { get { return StudioTheme.TimingCyan; } }
        public override Color ButtonSelectedGradientBegin { get { return StudioTheme.HoverSlate; } }
        public override Color ButtonSelectedGradientMiddle { get { return StudioTheme.HoverSlate; } }
        public override Color ButtonSelectedGradientEnd { get { return StudioTheme.HoverSlate; } }
        public override Color ButtonPressedBorder { get { return StudioTheme.SelectionViolet; } }
        public override Color ButtonPressedGradientBegin { get { return StudioTheme.RaisedSlate; } }
        public override Color ButtonPressedGradientMiddle { get { return StudioTheme.RaisedSlate; } }
        public override Color ButtonPressedGradientEnd { get { return StudioTheme.RaisedSlate; } }
        public override Color SeparatorDark { get { return StudioTheme.Border; } }
        public override Color SeparatorLight { get { return StudioTheme.PanelGraphite; } }
        public override Color CheckBackground { get { return StudioTheme.DeepSelection; } }
        public override Color CheckSelectedBackground { get { return StudioTheme.DeepSelection; } }
        public override Color CheckPressedBackground { get { return StudioTheme.DeepSelection; } }
    }

    public sealed class StudioToolStripRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _accentBottom;

        public StudioToolStripRenderer(bool accentBottom)
            : base(new StudioColorTable())
        {
            _accentBottom = accentBottom;
            RoundedEdges = false;
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? StudioTheme.MutedText : StudioTheme.Border;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
            {
                using (var pen = new Pen(StudioTheme.Border))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                }
                return;
            }

            Color color = _accentBottom ? StudioTheme.TimingCyan : StudioTheme.Border;
            using (var pen = new Pen(color))
            {
                e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int middle = e.Item.Width / 2;
            using (var pen = new Pen(StudioTheme.Border))
            {
                e.Graphics.DrawLine(
                    pen,
                    middle,
                    e.Item.ContentRectangle.Top + 3,
                    middle,
                    e.Item.ContentRectangle.Bottom - 3);
            }
        }
    }
}
