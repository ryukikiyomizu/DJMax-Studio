using System.Collections.Generic;

namespace DJMaxEditor.Files.bms
{
    /// <summary>
    /// BMS-only information which the generic DJMAX event model cannot otherwise retain.
    /// Keeping this beside PlayerData makes BMS -> edit -> BMS preserve lane and header meaning.
    /// </summary>
    public sealed class BmsMetadata
    {
        public string Title { get; set; } = "Untitled";
        public string Artist { get; set; } = "";
        public string Genre { get; set; } = "";
        public int Player { get; set; } = 1;
        public int PlayLevel { get; set; } = 1;
        public int Rank { get; set; } = 2;
        public double Total { get; set; } = 100;
        public double VolWav { get; set; } = 100;

        /// <summary>Editor track id -> classic BMS channel (01, 11, 21, etc.).</summary>
        public Dictionary<uint, string> TrackChannels { get; } =
            new Dictionary<uint, string>();

        /// <summary>Non-1.0 #mmm02 measure-length multipliers.</summary>
        public Dictionary<int, double> MeasureLengthRatios { get; } =
            new Dictionary<int, double>();

        /// <summary>Valid headers not represented by PlayerData (subtitle, BGA definitions, etc.).</summary>
        public List<string> AdditionalHeaderLines { get; } = new List<string>();

        /// <summary>Valid channels the generic editor cannot model, retained verbatim on BMS export.</summary>
        public List<string> PreservedDataLines { get; } = new List<string>();
    }
}
