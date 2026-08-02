using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Files.bms
{
    internal sealed class BmsonSaveFile : ISaveFile
    {
        public bool Save(string filename, PlayerData playerData)
        {
            if (playerData == null || string.IsNullOrWhiteSpace(filename)) return false;

            string directory = Path.GetDirectoryName(Path.GetFullPath(filename));
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory,
                "." + Path.GetFileName(filename) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, BmsonChartSerializer.Serialize(playerData),
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

        public string GetName() => "bmson";
        public string GetDescription() => "Be-Music JSON (unlimited keysounds)";
        public string GetExtension() => "bmson";
        public Form GetSettingsForm() => null;
    }
}
