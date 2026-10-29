using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DJMaxEditor.DJMax;
using DJMaxEditor.Files.FormatDetection;

namespace DJMaxEditor.Linux
{
    public sealed class MainWindow : Window
    {
        private const double TimelineWidth = 620;
        private const int NoteSnapTicks = 12;

        private static readonly IBrush WindowBrush = Brush("#0B1117");
        private static readonly IBrush PanelBrush = Brush("#121C26");
        private static readonly IBrush PanelAltBrush = Brush("#0E1720");
        private static readonly IBrush LineBrush = Brush("#263544");
        private static readonly IBrush TextBrush = Brush("#EAF2F7");
        private static readonly IBrush MutedBrush = Brush("#8DA0AE");
        private static readonly IBrush AccentBrush = Brush("#4DD6C5");
        private static readonly IBrush NoteBrush = Brush("#64B5FF");
        private static readonly IBrush TempoBrush = Brush("#FFC778");

        private readonly LinuxChartFileService _fileService = new LinuxChartFileService();
        private readonly TextBlock _formatValue;
        private readonly TextBlock _tempoValue;
        private readonly TextBlock _tracksValue;
        private readonly TextBlock _notesValue;
        private readonly TextBlock _lengthValue;
        private readonly TextBlock _chartName;
        private readonly TextBlock _chartSubtitle;
        private readonly TextBlock _statusText;
        private readonly StackPanel _timelineRows;
        private readonly Button _saveButton;

        private PlayerData _model;
        private string _sourcePath;
        private bool _dirty;
        private bool _busy;

        public MainWindow()
        {
            Title = "DJMAX Studio Linux";
            Width = 1180;
            Height = 780;
            MinWidth = 820;
            MinHeight = 600;
            Background = WindowBrush;

            _formatValue = ValueText();
            _tempoValue = ValueText();
            _tracksValue = ValueText();
            _notesValue = ValueText();
            _lengthValue = ValueText();
            _chartName = new TextBlock
            {
                Text = "No chart open",
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Foreground = TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            _chartSubtitle = MutedText("Open a chart or start a new one");
            _statusText = MutedText("Ready · Linux desktop preview");
            _timelineRows = new StackPanel { Spacing = 5 };
            _saveButton = MakeButton("Save", AccentBrush, Brush("#071815"));
            _saveButton.Click += async (_, _) => await SaveAsync();

            var root = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Background = WindowBrush
            };
            root.Children.Add(BuildToolbar());

            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("300,*"),
                ColumnSpacing = 14,
                Margin = new Thickness(18, 14, 18, 14)
            };
            Grid.SetRow(content, 1);
            content.Children.Add(BuildSidebar());
            var timeline = BuildTimelinePanel();
            Grid.SetColumn(timeline, 1);
            content.Children.Add(timeline);
            root.Children.Add(content);

            var status = new Border
            {
                Background = PanelAltBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(18, 9),
                Child = _statusText
            };
            Grid.SetRow(status, 2);
            root.Children.Add(status);
            Content = root;

            KeyDown += HandleKeyDown;
            UpdateDocumentView();
        }

        public void LoadInitialFile(string path)
        {
            _ = LoadFileAsync(path);
        }

        private Control BuildToolbar()
        {
            var bar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Background = PanelBrush,
                Margin = new Thickness(0, 0, 0, 1),
                MinHeight = 76
            };

            var identity = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(18, 12)
            };
            var mark = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(11),
                Background = AccentBrush,
                Child = new TextBlock
                {
                    Text = "DM",
                    FontSize = 15,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brush("#071815"),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };
            identity.Children.Add(mark);
            var branding = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            branding.Children.Add(new TextBlock
            {
                Text = "DJMAX STUDIO",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = TextBrush
            });
            branding.Children.Add(new TextBlock
            {
                Text = "LINUX EDITION  ·  PORTABLE CHART WORKSPACE",
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = MutedBrush
            });
            identity.Children.Add(branding);
            bar.Children.Add(identity);

            var actions = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(8, 12, 18, 12)
            };
            var newButton = MakeButton("New", PanelAltBrush, TextBrush);
            newButton.Click += (_, _) => CreateNewChart();
            var openButton = MakeButton("Open chart", PanelAltBrush, TextBrush);
            openButton.Click += async (_, _) => await OpenFromPickerAsync();
            _saveButton.IsEnabled = false;
            actions.Children.Add(newButton);
            actions.Children.Add(openButton);
            actions.Children.Add(_saveButton);
            var saveAsButton = MakeButton("Save as", PanelAltBrush, TextBrush);
            saveAsButton.Click += async (_, _) => await SaveAsAsync();
            actions.Children.Add(saveAsButton);
            Grid.SetColumn(actions, 1);
            bar.Children.Add(actions);
            return bar;
        }

        private Control BuildSidebar()
        {
            var sidebar = new StackPanel { Spacing = 12 };
            sidebar.Children.Add(BuildCard("CHART", BuildChartInfoContent()));

            var help = new StackPanel { Spacing = 9 };
            help.Children.Add(SectionLabel("SUPPORTED FORMATS"));
            help.Children.Add(MutedText("PT  ·  BMS / BME / BML / PMS"));
            help.Children.Add(MutedText("BMSON  ·  TECHMANIA .tech"));
            help.Children.Add(new Border
            {
                Background = PanelAltBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11),
                Margin = new Thickness(0, 4, 0, 0),
                Child = new TextBlock
                {
                    Text = "Click a lane to add a note. Right-click near a note to remove it. Save as another format to convert.",
                    FontSize = 11,
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap
                }
            });
            sidebar.Children.Add(BuildCard("PORTABLE WORKSPACE", help));
            return sidebar;
        }

        private Control BuildChartInfoContent()
        {
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(_chartName);
            panel.Children.Add(_chartSubtitle);
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = LineBrush,
                Margin = new Thickness(0, 1, 0, 1)
            });
            panel.Children.Add(InfoRow("FORMAT", _formatValue));
            panel.Children.Add(InfoRow("TEMPO", _tempoValue));
            panel.Children.Add(InfoRow("TRACKS", _tracksValue));
            panel.Children.Add(InfoRow("NOTES", _notesValue));
            panel.Children.Add(InfoRow("END TICK", _lengthValue));
            return panel;
        }

        private Control BuildTimelinePanel()
        {
            var heading = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            var titleStack = new StackPanel { Spacing = 3 };
            titleStack.Children.Add(new TextBlock
            {
                Text = "NOTE TIMELINE",
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = TextBrush
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "Chart lanes · click to edit",
                FontSize = 11,
                Foreground = MutedBrush
            });
            heading.Children.Add(titleStack);
            var legend = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            legend.Children.Add(LegendItem("NOTES", NoteBrush));
            legend.Children.Add(LegendItem("TEMPO", TempoBrush));
            Grid.SetColumn(legend, 1);
            heading.Children.Add(legend);

            var body = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*"),
                RowSpacing = 10
            };
            body.Children.Add(heading);
            var scale = BuildTimelineScale();
            Grid.SetRow(scale, 1);
            body.Children.Add(scale);
            var trackScroll = new ScrollViewer
            {
                Content = _timelineRows,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Grid.SetRow(trackScroll, 2);
            body.Children.Add(trackScroll);
            return new Border
            {
                Background = PanelBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(15),
                Child = body
            };
        }

        private Control BuildTimelineScale()
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("130,*"),
                Margin = new Thickness(0, 0, 10, 0)
            };
            grid.Children.Add(MutedText("TRACK / LANE"));
            var scale = new Grid
            {
                Width = TimelineWidth,
                ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*")
            };
            string[] labels = { "START", "25%", "50%", "75%", "END" };
            for (int i = 0; i < labels.Length; i++)
            {
                var label = new TextBlock
                {
                    Text = labels[i],
                    FontSize = 9,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = MutedBrush,
                    HorizontalAlignment = i == 0
                        ? Avalonia.Layout.HorizontalAlignment.Left
                        : i == labels.Length - 1
                            ? Avalonia.Layout.HorizontalAlignment.Right
                            : Avalonia.Layout.HorizontalAlignment.Center
                };
                Grid.SetColumn(label, i);
                scale.Children.Add(label);
            }
            Grid.SetColumn(scale, 1);
            grid.Children.Add(scale);
            return grid;
        }

        private void CreateNewChart()
        {
            var model = new PlayerData
            {
                Version = 2,
                Tempo = 140,
                TickPerMinute = 192,
                TrackDuration = 120,
                SourceFormat = ChartFormat.PtffDecrypted
            };
            model.Instruments.Add(new InstrumentData { InsNum = 0, Name = "none" });
            model.Instruments.Add(new InstrumentData { InsNum = 1, Name = "Default" });
            for (uint i = 0; i < 8; i++)
            {
                var track = new TrackData(i) { TrackName = "Lane " + (i + 1) };
                model.Tracks.AddTrack(track);
            }

            _model = model;
            _sourcePath = null;
            _dirty = true;
            UpdateDocumentView();
            SetStatus("New chart · click a lane to place notes", false);
        }

        private async Task OpenFromPickerAsync()
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open chart",
                    AllowMultiple = false,
                    FileTypeFilter = ChartFileTypes()
                });
                if (files.Count == 0)
                {
                    return;
                }
                await LoadFileAsync(files[0].Path.LocalPath);
            }
            catch (Exception exception)
            {
                SetStatus("Open failed · " + exception.Message, true);
            }
        }

        private async Task LoadFileAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            SetBusy(true, "Loading " + Path.GetFileName(path) + "…");
            try
            {
                PlayerData loaded = await Task.Run(() => _fileService.Open(path));
                _model = loaded;
                _sourcePath = path;
                _dirty = false;
                UpdateDocumentView();
                SetStatus("Loaded " + Path.GetFileName(path), false);
            }
            catch (Exception exception)
            {
                SetStatus("Could not open chart · " + exception.Message, true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task SaveAsync()
        {
            if (_model == null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(_sourcePath))
            {
                await SaveAsAsync();
                return;
            }
            await SaveToPathAsync(_sourcePath);
        }

        private async Task SaveAsAsync()
        {
            if (_model == null)
            {
                SetStatus("Create or open a chart before saving.", true);
                return;
            }

            string suggestedName = string.IsNullOrWhiteSpace(_sourcePath)
                ? "untitled.pt"
                : Path.GetFileName(_sourcePath);
            string extension = Path.GetExtension(suggestedName).TrimStart('.');
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save chart as",
                    SuggestedFileName = suggestedName,
                    DefaultExtension = string.IsNullOrWhiteSpace(extension) ? "pt" : extension,
                    FileTypeChoices = ChartFileTypes()
                });
                if (file == null)
                {
                    return;
                }
                await SaveToPathAsync(file.Path.LocalPath);
            }
            catch (Exception exception)
            {
                SetStatus("Save dialog failed · " + exception.Message, true);
            }
        }

        private async Task SaveToPathAsync(string path)
        {
            SetBusy(true, "Saving " + Path.GetFileName(path) + "…");
            try
            {
                await Task.Run(() => _fileService.Save(path, _model));
                _sourcePath = path;
                _dirty = false;
                UpdateDocumentView();
                SetStatus("Saved " + Path.GetFileName(path), false);
            }
            catch (Exception exception)
            {
                SetStatus("Save failed · " + exception.Message, true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void RefreshTimeline()
        {
            _timelineRows.Children.Clear();
            if (_model == null || _model.Tracks.Count == 0)
            {
                _timelineRows.Children.Add(new Border
                {
                    Background = PanelAltBrush,
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(18),
                    Child = new TextBlock
                    {
                        Text = "No tracks to display. Create or open a chart to begin.",
                        Foreground = MutedBrush,
                        FontSize = 12
                    }
                });
                return;
            }

            int maxTick = Math.Max(_model.MaxTick, 384);
            foreach (TrackData track in _model.Tracks)
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("130,*"),
                    MinHeight = 32,
                    Margin = new Thickness(0, 1)
                };
                var laneName = string.IsNullOrWhiteSpace(track.TrackName)
                    ? "Track " + track.Idx
                    : track.TrackName;
                var label = new TextBlock
                {
                    Text = laneName,
                    FontSize = 10,
                    Foreground = MutedBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                row.Children.Add(label);

                var canvas = new Canvas
                {
                    Width = TimelineWidth,
                    Height = 28,
                    Background = PanelAltBrush,
                    ClipToBounds = true
                };
                for (int gridLine = 0; gridLine <= 4; gridLine++)
                {
                    var line = new Border
                    {
                        Width = 1,
                        Height = 28,
                        Background = LineBrush,
                        Opacity = gridLine == 0 || gridLine == 4 ? 0.65 : 0.35
                    };
                    Canvas.SetLeft(line, (TimelineWidth - 1) * gridLine / 4.0);
                    canvas.Children.Add(line);
                }

                var notes = track.Events.Where(item =>
                    item.EventType == EventType.Note || item.EventType == EventType.Tempo).ToArray();
                int stride = Math.Max(1, (int)Math.Ceiling(notes.Length / 220.0));
                for (int index = 0; index < notes.Length; index += stride)
                {
                    EventData item = notes[index];
                    double x = Math.Clamp((double)item.Tick / maxTick, 0, 1) * (TimelineWidth - 5);
                    bool isTempo = item.EventType == EventType.Tempo;
                    var marker = new Border
                    {
                        Width = isTempo ? 3 : 4,
                        Height = isTempo ? 18 : 12,
                        CornerRadius = new CornerRadius(2),
                        Background = isTempo ? TempoBrush : NoteBrush
                    };
                    Canvas.SetLeft(marker, x);
                    Canvas.SetTop(marker, isTempo ? 4 : 8);
                    canvas.Children.Add(marker);
                }

                canvas.PointerPressed += (_, args) => HandleTimelinePointer(track, canvas, args, maxTick);
                Grid.SetColumn(canvas, 1);
                row.Children.Add(canvas);
                _timelineRows.Children.Add(row);
            }
        }

        private void HandleTimelinePointer(TrackData track, Canvas canvas, PointerPressedEventArgs args, int maxTick)
        {
            if (_model == null || _busy || _model.IsReadOnly)
            {
                return;
            }

            var properties = args.GetCurrentPoint(canvas).Properties;
            bool remove = properties.IsRightButtonPressed;
            if (!remove && !properties.IsLeftButtonPressed)
            {
                return;
            }

            double fraction = Math.Clamp(args.GetPosition(canvas).X / TimelineWidth, 0, 1);
            int rawTick = (int)Math.Round(fraction * maxTick);
            int tick = Math.Max(0, (int)Math.Round(rawTick / (double)NoteSnapTicks) * NoteSnapTicks);
            EventData nearest = track.Events
                .Where(item => item.EventType == EventType.Note)
                .OrderBy(item => Math.Abs(item.Tick - tick))
                .FirstOrDefault();

            if (remove)
            {
                if (nearest != null && Math.Abs(nearest.Tick - tick) <= NoteSnapTicks)
                {
                    track.RemoveEvent(nearest);
                    _dirty = true;
                }
            }
            else if (nearest != null && Math.Abs(nearest.Tick - tick) <= NoteSnapTicks)
            {
                track.RemoveEvent(nearest);
                _dirty = true;
            }
            else
            {
                InstrumentData instrument = _model.Instruments.FirstOrDefault(item => item.InsNum != 0);
                if (instrument == null)
                {
                    instrument = new InstrumentData
                    {
                        InsNum = (ushort)Math.Max(1, _model.Instruments.Count),
                        Name = "Default"
                    };
                    _model.Instruments.Add(instrument);
                }
                track.AddEvent(new EventData
                {
                    EventType = EventType.Note,
                    Tick = tick,
                    Duration = 12,
                    Instrument = instrument
                });
                _dirty = true;
            }

            UpdateDocumentView();
            SetStatus(_dirty ? "Unsaved changes · click a lane to add/remove notes" : "Ready", false);
            args.Handled = true;
        }

        private void UpdateDocumentView()
        {
            if (_model == null)
            {
                _chartName.Text = "No chart open";
                _chartSubtitle.Text = "Open a chart or start a new one";
                _formatValue.Text = "—";
                _tempoValue.Text = "—";
                _tracksValue.Text = "—";
                _notesValue.Text = "—";
                _lengthValue.Text = "—";
                _saveButton.IsEnabled = false;
                Title = "DJMAX Studio Linux";
                RefreshTimeline();
                return;
            }

            string name = string.IsNullOrWhiteSpace(_sourcePath) ? "Untitled chart" : Path.GetFileName(_sourcePath);
            _chartName.Text = name;
            _chartSubtitle.Text = _sourcePath ?? "New chart · not saved";
            _chartSubtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _formatValue.Text = FormatLabel(_model.SourceFormat);
            _tempoValue.Text = _model.Tempo > 0
                ? _model.Tempo.ToString("0.##", CultureInfo.InvariantCulture) + " BPM"
                : "Not set";
            _tracksValue.Text = _model.Tracks.Count.ToString(CultureInfo.InvariantCulture);
            _notesValue.Text = _model.Tracks.SelectMany(track => track.Events)
                .Count(item => item.EventType == EventType.Note).ToString("N0", CultureInfo.InvariantCulture);
            _lengthValue.Text = _model.MaxTick.ToString("N0", CultureInfo.InvariantCulture);
            _saveButton.IsEnabled = !_busy;
            Title = (_dirty ? "● " : "") + name + " — DJMAX Studio Linux";
            RefreshTimeline();
        }

        private void SetBusy(bool busy, string message = null)
        {
            _busy = busy;
            _saveButton.IsEnabled = _model != null && !busy;
            if (!string.IsNullOrWhiteSpace(message))
            {
                SetStatus(message, false);
            }
        }

        private void SetStatus(string message, bool isError)
        {
            _statusText.Text = message;
            _statusText.Foreground = isError ? Brush("#FF8D91") : MutedBrush;
        }

        private void HandleKeyDown(object sender, KeyEventArgs args)
        {
            if ((args.KeyModifiers & KeyModifiers.Control) == 0)
            {
                return;
            }
            if (args.Key == Key.O)
            {
                _ = OpenFromPickerAsync();
                args.Handled = true;
            }
            else if (args.Key == Key.S && (args.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                _ = SaveAsAsync();
                args.Handled = true;
            }
            else if (args.Key == Key.S)
            {
                _ = SaveAsync();
                args.Handled = true;
            }
        }

        private static FilePickerFileType[] ChartFileTypes()
        {
            return new[]
            {
                new FilePickerFileType("Supported chart files")
                {
                    Patterns = new[] { "*.pt", "*.bms", "*.bme", "*.bml", "*.pms", "*.bmson", "*.tech" }
                },
                new FilePickerFileType("PT chart") { Patterns = new[] { "*.pt" } },
                new FilePickerFileType("BMS family") { Patterns = new[] { "*.bms", "*.bme", "*.bml", "*.pms" } },
                new FilePickerFileType("BMSON chart") { Patterns = new[] { "*.bmson" } },
                new FilePickerFileType("TECHMANIA chart") { Patterns = new[] { "*.tech" } }
            };
        }

        private static string FormatLabel(ChartFormat? format)
        {
            if (!format.HasValue)
            {
                return "New chart";
            }
            switch (format.Value)
            {
                case ChartFormat.PtffDecrypted: return "DJMAX PT";
                case ChartFormat.PtffEncryptedTechnika: return "Encrypted PT";
                case ChartFormat.BmsClassic: return "BMS classic";
                case ChartFormat.Bmson: return "BMSON";
                case ChartFormat.TechmaniaTrack: return "TECHMANIA";
                case ChartFormat.TrailerRespectV: return "Respect V trailer";
                case ChartFormat.CyclonXml: return "CYCLON XML";
                default: return format.Value.ToString();
            }
        }

        private static Control BuildCard(string title, Control content)
        {
            var panel = new StackPanel { Spacing = 13 };
            if (!string.IsNullOrWhiteSpace(title))
            {
                panel.Children.Add(SectionLabel(title));
            }
            panel.Children.Add(content);
            return new Border
            {
                Background = PanelBrush,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(15),
                Child = panel
            };
        }

        private static Control InfoRow(string caption, TextBlock value)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = MutedBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            value.FontSize = 12;
            value.FontWeight = FontWeight.SemiBold;
            value.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            value.Foreground = TextBrush;
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            return row;
        }

        private static Control LegendItem(string label, IBrush color)
        {
            var item = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            item.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = color,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            item.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 9,
                FontWeight = FontWeight.SemiBold,
                Foreground = MutedBrush
            });
            return item;
        }

        private static TextBlock SectionLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = AccentBrush
            };
        }

        private static TextBlock ValueText()
        {
            return new TextBlock { Foreground = TextBrush };
        }

        private static TextBlock MutedText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = MutedBrush
            };
        }

        private static Button MakeButton(string text, IBrush background, IBrush foreground)
        {
            return new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = foreground
                },
                Background = background,
                Foreground = foreground,
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 9),
                CornerRadius = new CornerRadius(8),
                MinWidth = 66,
                Cursor = new Avalonia.Input.Cursor(StandardCursorType.Hand)
            };
        }

        private static IBrush Brush(string color)
        {
            return new SolidColorBrush(Color.Parse(color));
        }
    }
}
