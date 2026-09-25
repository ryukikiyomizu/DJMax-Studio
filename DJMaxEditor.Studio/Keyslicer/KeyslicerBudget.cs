using System;
using System.Windows.Media;

namespace DJMaxEditor.Studio.Keyslicer
{
    /// <summary>
    /// Budget accounting: BMS 1295, RESPECT 2047, Technika unbounded.
    /// The slicer's slices are keysounds; the budget counts them.
    /// Over-budget is a warning in BMS/RESPECT, not a hard block — the wizard
    /// will suggest trimming or splitting on Finalize — but the meter turns red
    /// the moment you cross it so you don't discover it at export.
    /// </summary>
    public sealed class KeyslicerBudget
    {
        public static int MaxFor(SlicerMode mode) => KeyslicerProject.MaxSlicesForMode(mode);

        public static BudgetState Evaluate(int count, SlicerMode mode)
        {
            int max = MaxFor(mode);
            if (max == int.MaxValue)
                return new BudgetState(count, max, double.NaN, false, string.Format("{0} slices (Technika — no limit)", count));
            double fill = max > 0 ? (double)count / max : 0;
            bool over = count > max;
            string label;
            if (over) label = string.Format("{0} / {1} — over by {2}", count, max, count - max);
            else label = string.Format("{0} / {1}  ({2} left)", count, max, max - count);
            return new BudgetState(count, max, fill, over, label);
        }
    }

    public sealed class BudgetState
    {
        public BudgetState(int count, int max, double fill, bool over, string label)
        {
            Count = count;
            Max = max;
            Fill = fill;
            IsOver = over;
            Label = label;
            IsUnbounded = max == int.MaxValue;
        }
        public int Count { get; }
        public int Max { get; }
        public double Fill { get; }
        public bool IsOver { get; }
        public bool IsUnbounded { get; }
        public string Label { get; }
        public Brush FillBrush
        {
            get
            {
                if (IsUnbounded) return new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A)) { Opacity = 0.9 };
                if (IsOver) return new SolidColorBrush(Color.FromRgb(0xE5, 0x4B, 0x4B));
                if (Fill > 0.85) return new SolidColorBrush(Color.FromRgb(0xE8, 0xA8, 0x2A));
                return new SolidColorBrush(Color.FromRgb(0x6A, 0xB8, 0xFF));
            }
        }
        public Brush BackgroundBrush => new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));
    }
}
