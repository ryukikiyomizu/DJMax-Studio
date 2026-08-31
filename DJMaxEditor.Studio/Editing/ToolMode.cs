using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DJMaxEditor.Studio.Editing
{
    /// <summary>
    /// The editing tool. These are ptSequencer's four modes and its four shortcuts, kept exactly
    /// as its manual documents them so muscle memory transfers:
    /// Select F5, Addition F6, Edit F7, Delete F8.
    /// </summary>
    public enum ToolMode
    {
        /// <summary>F5. Pick and drag notes. Delete key also deletes here.</summary>
        Select = 0,

        /// <summary>F6. Click an empty cell to place a note. Cannot select while in this mode.</summary>
        Addition = 1,

        /// <summary>F7. Drag a note's trailing edge to turn it into a long note, or to resize one.</summary>
        Edit = 2,

        /// <summary>F8. Click a note to remove it.</summary>
        Delete = 3,
    }

    /// <summary>
    /// Grid resolution, as ptSequencer expresses it: a division of the bar. "1/1 means 1 note per
    /// bar, and if set 1/16, you can add up to 16 notes per bar. Free means you can add Notes
    /// without limitation."
    /// </summary>
    public sealed class GridDivision
    {
        private static readonly ReadOnlyCollection<GridDivision> AllDivisions = BuildAll();

        private GridDivision(int denominator, string label)
        {
            Denominator = denominator;
            Label = label;
        }

        /// <summary>Bar divisions. 0 means Free (no quantisation).</summary>
        public int Denominator { get; private set; }

        public string Label { get; private set; }

        public bool IsFree { get { return Denominator <= 0; } }

        /// <summary>The list ptSequencer offers, in its order, plus Free at the end.</summary>
        public static ReadOnlyCollection<GridDivision> All { get { return AllDivisions; } }

        public static GridDivision Free { get { return AllDivisions[AllDivisions.Count - 1]; } }

        /// <summary>ptSequencer's shipped default is 1/16.</summary>
        public static GridDivision Default
        {
            get
            {
                GridDivision sixteenth = FromDenominator(16);
                return sixteenth ?? AllDivisions[0];
            }
        }

        public static GridDivision FromDenominator(int denominator)
        {
            foreach (GridDivision division in AllDivisions)
            {
                if (division.Denominator == denominator)
                {
                    return division;
                }
            }
            return null;
        }

        /// <summary>
        /// Quantises a tick to this grid. <paramref name="ticksPerMeasure"/> is the chart's own
        /// measure resolution in virtual ticks, so the grid follows a chart that does not use the
        /// default 192 - guessing a constant here is how off-grid notes get silently moved.
        /// </summary>
        public int Snap(int virtualTick, int ticksPerMeasure)
        {
            if (IsFree || ticksPerMeasure <= 0)
            {
                return virtualTick;
            }

            double step = (double)ticksPerMeasure / Denominator;
            if (step < 1.0)
            {
                // A division finer than one tick cannot quantise anything; behave as Free rather
                // than rounding everything to zero.
                return virtualTick;
            }

            return (int)Math.Round(virtualTick / step, MidpointRounding.AwayFromZero) * (int)Math.Round(step);
        }

        /// <summary>Grid step in virtual ticks, or 0 when Free.</summary>
        public double StepTicks(int ticksPerMeasure)
        {
            if (IsFree || ticksPerMeasure <= 0)
            {
                return 0;
            }
            return (double)ticksPerMeasure / Denominator;
        }

        public override string ToString()
        {
            return Label;
        }

        private static ReadOnlyCollection<GridDivision> BuildAll()
        {
            List<GridDivision> list = new List<GridDivision>();
            int[] denominators = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64 };
            foreach (int denominator in denominators)
            {
                list.Add(new GridDivision(denominator, "1/" + denominator));
            }
            list.Add(new GridDivision(0, "Free"));
            return new ReadOnlyCollection<GridDivision>(list);
        }
    }

    /// <summary>
    /// ptSequencer's second grid combo: "Detailed Grid : Showing beat. Set it to be basic - 1/4."
    /// It does not quantise anything - it only decides which subdivision gets a drawn line, so
    /// the canvas can show a 1/4 beat rule while notes snap to 1/16.
    /// </summary>
    public sealed class BeatDisplay
    {
        private static readonly ReadOnlyCollection<BeatDisplay> AllDisplays = BuildAll();

        private BeatDisplay(int denominator, string label)
        {
            Denominator = denominator;
            Label = label;
        }

        public int Denominator { get; private set; }

        public string Label { get; private set; }

        public bool IsOff { get { return Denominator <= 0; } }

        public static ReadOnlyCollection<BeatDisplay> All { get { return AllDisplays; } }

        /// <summary>ptSequencer's documented basic value.</summary>
        public static BeatDisplay Default
        {
            get
            {
                BeatDisplay quarter = FromDenominator(4);
                return quarter ?? AllDisplays[0];
            }
        }

        public static BeatDisplay FromDenominator(int denominator)
        {
            foreach (BeatDisplay display in AllDisplays)
            {
                if (display.Denominator == denominator)
                {
                    return display;
                }
            }
            return null;
        }

        public override string ToString()
        {
            return Label;
        }

        private static ReadOnlyCollection<BeatDisplay> BuildAll()
        {
            List<BeatDisplay> list = new List<BeatDisplay>();
            int[] denominators = { 1, 2, 4, 8, 16 };
            foreach (int denominator in denominators)
            {
                list.Add(new BeatDisplay(denominator, "1/" + denominator));
            }
            list.Add(new BeatDisplay(0, "Off"));
            return new ReadOnlyCollection<BeatDisplay>(list);
        }
    }
}
