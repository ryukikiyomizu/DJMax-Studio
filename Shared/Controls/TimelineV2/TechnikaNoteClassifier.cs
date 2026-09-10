using DJMaxEditor.DJMax;

namespace DJMaxEditor.Controls.TimelineV2
{
    internal enum TechnikaNoteKind
    {
        Unknown,
        Basic,
        Drag,
        ChainHead,
        ChainNode,
        RepeatHead,
        RepeatHeadHold,
        Repeat,
        RepeatHold,
        Hold
    }

    internal static class TechnikaNoteClassifier
    {
        public static TechnikaNoteKind Classify(EventData source)
        {
            if (source == null || source.EventType != EventType.Note)
            {
                return TechnikaNoteKind.Unknown;
            }

            switch (source.Attribute)
            {
                case 0:
                    return source.Duration > 6
                        ? TechnikaNoteKind.Drag
                        : TechnikaNoteKind.Basic;
                case 5:
                    return TechnikaNoteKind.ChainHead;
                case 6:
                    return TechnikaNoteKind.ChainNode;
                case 10:
                    return source.Duration > 6
                        ? TechnikaNoteKind.RepeatHeadHold
                        : TechnikaNoteKind.RepeatHead;
                case 11:
                    return source.Duration > 6
                        ? TechnikaNoteKind.RepeatHold
                        : TechnikaNoteKind.Repeat;
                case 12:
                    return TechnikaNoteKind.Hold;
                default:
                    return TechnikaNoteKind.Unknown;
            }
        }
    }
}
