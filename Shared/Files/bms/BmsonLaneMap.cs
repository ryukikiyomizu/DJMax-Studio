using System;
using System.Collections.Generic;

namespace DJMaxEditor.Files.bms
{
    /// <summary>
    /// The one table that says which bmson x lane is which classic BMS channel.
    ///
    /// <para>
    /// Both halves of <c>BmsonChartSerializer</c> go through here so the reader and the writer
    /// cannot drift apart: the reader folds lanes onto channels when it builds the model, and the
    /// writer reads those same channels back out to lanes. Before this the two halves each kept
    /// their own <c>DefaultChannels</c> order, and that order disagreed with the spec twice over -
    /// lane 6 read as the turntable instead of key 6, lane 8 read as key 7 instead of the
    /// turntable - so a 7K chart's scratch drew as a key in the middle of the keyboard, and a 5K
    /// chart (keys plus scratch on lane 8, nothing on 6/7) had no scratch column at all.
    /// </para>
    ///
    /// <para>
    /// The spec's own tables (bmson-spec, Appendices/Canonical List of Mode Hints): on every
    /// beat-* layout x = 1..7 are the keys and x = 8 is the turntable, with player 2 mirroring
    /// that on x = 9..16; on popn-9k x = 1..9 are all keys. Classic BMS numbers the same idea
    /// differently - 11-15 are keys 1-5, 16 the turntable, 18/19 keys 6/7 - which is why lane 6
    /// is channel 18 and not 16. Lane 0 is accompaniment on every layout.
    /// </para>
    /// </summary>
    internal static class BmsonLaneMap
    {
        /// <summary>beat-* player 1: keys 1-7 on x = 1..7, turntable on x = 8.</summary>
        private static readonly string[] BeatPlayer1 =
            { "11", "12", "13", "14", "15", "18", "19", "16" };

        /// <summary>beat-* player 2: the same shape on x = 9..16.</summary>
        private static readonly string[] BeatPlayer2 =
            { "21", "22", "23", "24", "25", "28", "29", "26" };

        /// <summary>
        /// popn-9k: nine keys on x = 1..9, no turntable. The right hand rides the second channel
        /// family (22-25), which is also the shape the BMS layout's own PMS rule recognises, so a
        /// popn chart opened here draws nine keys rather than a second player.
        /// </summary>
        private static readonly string[] Popn =
            { "11", "12", "13", "14", "15", "22", "23", "24", "25" };

        /// <summary>
        /// Fallback channel order for tracks that carry no channel at all (a DJMax chart exported
        /// to bmson). Lane order, so the 8th track is the scratch rather than the 6th.
        /// </summary>
        private static readonly string[] FallbackChannels =
            { "11", "12", "13", "14", "15", "18", "19", "16" };

        /// <summary>True when <paramref name="modeHint"/> names a popn layout (popn-5k/9k).</summary>
        public static bool IsPopnHint(string modeHint)
        {
            return !string.IsNullOrWhiteSpace(modeHint) &&
                modeHint.Trim().StartsWith("popn", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The classic channel a lane plays on, or null when the lane is not playable (lane 0,
        /// anything past the layout's last lane). The reader keeps playable lanes on lane tracks
        /// and routes everything else to accompaniment.
        /// </summary>
        public static string ChannelForLane(string modeHint, int lane)
        {
            if (IsPopnHint(modeHint))
            {
                return lane >= 1 && lane <= Popn.Length ? Popn[lane - 1] : null;
            }

            if (lane >= 1 && lane <= BeatPlayer1.Length)
            {
                return BeatPlayer1[lane - 1];
            }
            if (lane > BeatPlayer1.Length && lane <= BeatPlayer1.Length + BeatPlayer2.Length)
            {
                return BeatPlayer2[lane - BeatPlayer1.Length - 1];
            }
            return null;
        }

        /// <summary>
        /// The lane a channel plays on, or 0 for accompaniment/timing channels. The inverse of
        /// <see cref="ChannelForLane"/>, with the long-note families (5x/6x) folded onto the lanes
        /// they extend. <paramref name="pms"/> must be the shape of the whole channel set (see
        /// <see cref="IsPmsShaped"/>), because channel 22 is popn's key 6 on a PMS chart and
        /// player 2's key 2 on a beat chart.
        /// </summary>
        public static int LaneForChannel(string channel, bool pms)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                return 0;
            }
            string normalized = channel.Trim().ToUpperInvariant();
            if (normalized.Length != 2)
            {
                return 0;
            }

            // Both long-note families address their plain lane; the model already stores the
            // plain channel, so this only steadies a hand-built map.
            if (normalized[0] == '5')
            {
                normalized = "1" + normalized.Substring(1);
            }
            else if (normalized[0] == '6')
            {
                normalized = "2" + normalized.Substring(1);
            }

            if (pms)
            {
                for (int i = 0; i < Popn.Length; i++)
                {
                    if (Popn[i] == normalized)
                    {
                        return i + 1;
                    }
                }
                return 0;
            }

            for (int i = 0; i < BeatPlayer1.Length; i++)
            {
                if (BeatPlayer1[i] == normalized)
                {
                    return i + 1;
                }
            }
            for (int i = 0; i < BeatPlayer2.Length; i++)
            {
                if (BeatPlayer2[i] == normalized)
                {
                    return BeatPlayer1.Length + i + 1;
                }
            }
            return 0;
        }

        /// <summary>
        /// Whether a channel set is a nine-button PMS chart rather than a chart with a second
        /// player's side: the second channel family carries only lanes 2-5, and the first has
        /// neither a turntable nor the 18/19 keys a seven-key chart would use. Deliberately the
        /// same rule as the BMS layout's own shape test, so the writer's lanes and the layout's
        /// columns agree on what a chart is.
        /// </summary>
        public static bool IsPmsShaped(IEnumerable<string> channels)
        {
            if (channels == null)
            {
                return false;
            }

            bool secondFamilyKey = false;
            foreach (string raw in channels)
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Length != 2)
                {
                    continue;
                }
                string channel = raw.Trim().ToUpperInvariant();
                // Long-note families address their plain lane; folded for the same reason the
                // layout folds them before asking this same question.
                if (channel[0] == '5')
                {
                    channel = "1" + channel.Substring(1);
                }
                else if (channel[0] == '6')
                {
                    channel = "2" + channel.Substring(1);
                }
                if (channel[0] == '2')
                {
                    int lane = Base36Value(channel[1]);
                    if (lane < 2 || lane > 5)
                    {
                        return false;
                    }
                    secondFamilyKey = true;
                }
                else if (channel == "16" || channel == "17" || channel == "18" || channel == "19")
                {
                    return false;
                }
            }
            return secondFamilyKey;
        }

        /// <summary>
        /// The mode_hint a channel set describes: beat-5k/7k for one player's side, beat-10k/14k
        /// when the second side is present, popn-9k for a PMS shape. A first-family-only popn-5k
        /// reads as beat-5k, honestly enough - its lanes 1-5 are the same five keys either way.
        /// </summary>
        public static string ModeHintForChannels(IEnumerable<string> channels)
        {
            if (channels == null)
            {
                return "beat-7k";
            }

            var list = new List<string>(channels);
            bool pms = IsPmsShaped(list);
            if (pms)
            {
                return "popn-9k";
            }

            var lanes = new HashSet<int>();
            foreach (string channel in list)
            {
                int lane = LaneForChannel(channel, false);
                if (lane > 0)
                {
                    lanes.Add(lane);
                }
            }

            if (lanes.Count == 0)
            {
                return "beat-7k";
            }

            bool secondSide = false;
            foreach (int lane in lanes)
            {
                if (lane > BeatPlayer1.Length)
                {
                    secondSide = true;
                    break;
                }
            }

            // A scratch alone does not make a 7K chart: keys plus turntable on lane 8 and
            // nothing on 6/7 is beat-5k, which the old max-lane rule misreported as beat-7k.
            bool sevenKeys = lanes.Contains(6) || lanes.Contains(7) ||
                lanes.Contains(BeatPlayer1.Length + 6) || lanes.Contains(BeatPlayer1.Length + 7);
            if (secondSide)
            {
                return sevenKeys ? "beat-14k" : "beat-10k";
            }
            return sevenKeys ? "beat-7k" : "beat-5k";
        }

        /// <summary>
        /// The fallback channel for the <paramref name="index"/>th track that carries no channel
        /// of its own, or "01" past the eighth: accompaniment, which is where an overflow track's
        /// notes belong rather than on a ninth lane no layout has.
        /// </summary>
        public static string FallbackChannel(int index)
        {
            return index >= 0 && index < FallbackChannels.Length
                ? FallbackChannels[index]
                : "01";
        }

        private static int Base36Value(char value)
        {
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }
            char upper = char.ToUpperInvariant(value);
            return upper >= 'A' && upper <= 'Z' ? 10 + (upper - 'A') : -1;
        }
    }
}
