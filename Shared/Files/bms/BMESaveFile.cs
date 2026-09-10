using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Files.bms
{
    /// <summary>Classic BMS-family exporter retained under the historical plugin class name.</summary>
    internal sealed class BMESaveFile : ISaveFile
    {
        private string _title;
        private string _artist;
        private string _genre;

        public void SetPlayableTracks(int from, int to)
        {
            // Lane mappings loaded from BMS are retained by BmsMetadata. New documents map their first
            // eight tracks to conventional 7-key + scratch channels automatically.
        }

        public void SetSongDetails(string songname, string artist, string genre)
        {
            _title = songname;
            _artist = artist;
            _genre = genre;
        }

        public bool Save(string filename, PlayerData playerData)
        {
            if (playerData == null || string.IsNullOrWhiteSpace(filename)) return false;

            if (playerData.BmsMetadata == null)
                playerData.BmsMetadata = new BmsMetadata();
            if (_title != null) playerData.BmsMetadata.Title = _title;
            if (_artist != null) playerData.BmsMetadata.Artist = _artist;
            if (_genre != null) playerData.BmsMetadata.Genre = _genre;

            string directory = Path.GetDirectoryName(Path.GetFullPath(filename));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory,
                "." + Path.GetFileName(filename) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, BmsChartSerializer.Serialize(playerData),
                    new UTF8Encoding(false));
                if (File.Exists(filename))
                    File.Replace(temporary, filename, null);
                else
                    File.Move(temporary, filename);
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // A failed cleanup must not hide the real save result/exception.
                }
            }
        }

        public string GetName() => "bms";
        public string GetDescription() => "Be-Music Script";
        public string GetExtension() => "bms;bme;bml;pms";
        public Form GetSettingsForm() => null;
    }
}
