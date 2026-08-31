using System;
using DJMaxEditor.Controls.Editor.Renderers.Events;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.Editor.Renderers
{
    /// <summary>
    /// Precomputed note labels for the inspector display modes.
    /// <para>
    /// Every label an event theme can print is a fixed string derived from one small
    /// number: a pan byte, a velocity byte, a duration, an instrument number. The themes
    /// used to <c>string.Format</c> them once per visible note per frame, which on a dense
    /// chart meant thousands of throwaway string allocations every frame — the reported
    /// "really lags" symptom. The whole reachable label space is a few hundred short
    /// strings, so it is built once and handed out by reference.
    /// </para>
    /// <para>
    /// The formats are the exact ones the themes used before, so the text on screen is
    /// unchanged. Values past the tables fall back to the same <c>string.Format</c> call,
    /// so nothing can be dropped or truncated.
    /// </para>
    /// <para>
    /// There is no attribute label any more: <c>Attr</c> mode was the default and the
    /// slowest thing on screen, and <see cref="EventDisplayMode.None"/> replaced it as the
    /// default. Attributes are read and edited in the Properties inspector instead.
    /// </para>
    /// </summary>
    internal static class EventLabelCache
    {
        /// <summary>
        /// Covers every byte-valued field outright, and the range of durations and
        /// instrument numbers a chart realistically uses.
        /// </summary>
        private const int TableSize = 256;

        private const string InstrumentFormat = "Ins {0,3:000}";
        private const string DurationFormat = "Dur {0,3:000}";
        private const string PanFormat = "Pan {0,3:000}";
        private const string VelocityFormat = "Vel {0,3:000}";

        private static readonly string[] Instruments = Build(InstrumentFormat);
        private static readonly string[] Durations = Build(DurationFormat);
        private static readonly string[] Pans = Build(PanFormat);
        private static readonly string[] Velocities = Build(VelocityFormat);

        /// <summary>Label for <see cref="EventDisplayMode.Instrument"/>.</summary>
        public static string Instrument(int value)
        {
            return Lookup(Instruments, InstrumentFormat, value);
        }

        /// <summary>Label for <see cref="EventDisplayMode.Duration"/>.</summary>
        public static string Duration(int value)
        {
            return Lookup(Durations, DurationFormat, value);
        }

        /// <summary>Label for <see cref="EventDisplayMode.Pan"/>.</summary>
        public static string Pan(byte value)
        {
            return Pans[value];
        }

        /// <summary>Label for <see cref="EventDisplayMode.Velocity"/>.</summary>
        public static string Velocity(byte value)
        {
            return Velocities[value];
        }

        /// <summary>
        /// Label for the given display mode, or <see cref="string.Empty"/> when the mode
        /// prints nothing. Themes call this so they can skip their text drawing entirely
        /// rather than paying two GDI+ text calls to render an empty string.
        /// </summary>
        public static string For(EventDisplayMode mode, EventData eventData)
        {
            if (eventData == null) return string.Empty;

            switch (mode)
            {
                case EventDisplayMode.Instrument:
                    InstrumentData instrument = eventData.Instrument;
                    return Instrument(instrument != null ? instrument.InsNum : 0);
                case EventDisplayMode.Duration:
                    return Duration(eventData.Duration);
                case EventDisplayMode.Pan:
                    return Pan(eventData.Pan);
                case EventDisplayMode.Velocity:
                    return Velocity(eventData.Vel);
                default:
                    // EventDisplayMode.None lands here, and so does any unmapped value.
                    return string.Empty;
            }
        }

        private static string Lookup(string[] table, string format, int value)
        {
            if (value >= 0 && value < table.Length) return table[value];
            return string.Format(format, value);
        }

        private static string[] Build(string format)
        {
            var table = new string[TableSize];
            for (int value = 0; value < TableSize; value++)
            {
                table[value] = string.Format(format, value);
            }
            return table;
        }
    }
}
