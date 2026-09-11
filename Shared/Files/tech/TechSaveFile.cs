using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Files.Tech
{
    /// <summary>
    /// Writes a TECHMANIA <c>track.tech</c> (format version "3"): the save-back half of
    /// the round trip opened by <see cref="TechOpenFile"/>. Uses the same atomic
    /// temp-file-then-replace write the other text chart handlers use.
    /// </summary>
    internal sealed class TechSaveFile : ISaveFile
    {
        public bool Save(string filename, PlayerData playerData)
        {
            if (playerData == null || string.IsNullOrWhiteSpace(filename))
            {
                return false;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(filename));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory,
                "." + Path.GetFileName(filename) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, TechmaniaChartSerializer.Serialize(playerData),
                    new UTF8Encoding(false));
                if (File.Exists(filename))
                {
                    File.Replace(temporary, filename, null);
                }
                else
                {
                    File.Move(temporary, filename);
                }
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                    // A failed cleanup must not hide the real save result/exception.
                }
            }
        }

        public string GetName() => "tech";
        public string GetDescription() => "TECHMANIA track";
        public string GetExtension() => "tech";
        public Form GetSettingsForm() => null;
    }
}
