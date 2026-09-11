using System;
using System.IO;
using System.Windows.Forms;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Files.Tech
{
    /// <summary>
    /// Opens a TECHMANIA <c>track.tech</c> file. TECHMANIA is the TECHNIKA-style fan
    /// game, and its native note vocabulary is the one the TECHNIKA preview already
    /// speaks, so the import lands on the same four-lane layout with end-of-scan marker
    /// tracks and the same attribute grammar a real .pt uses.
    /// </summary>
    internal sealed class TechOpenFile : IOpenFile
    {
        public bool Open(string filename, out PlayerData playerData)
        {
            try
            {
                playerData = TechmaniaChartSerializer.Parse(File.ReadAllBytes(filename));
                return true;
            }
            catch (ChartLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ChartLoadException(ChartLoadError.Unexpected,
                    "The TECHMANIA track file could not be read.", null, ex);
            }
        }

        public string GetName() => "tech";
        public string GetDescription() => "TECHMANIA track";
        public string GetExtension() => "tech";
        public Form GetSettingsForm() => null;
    }
}
