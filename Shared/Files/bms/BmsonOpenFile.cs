using System;
using System.IO;
using System.Windows.Forms;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Files.bms
{
    /// <summary>
    /// Reader for bmson (Be-Music JSON). Until now bmson existed only on the write side - the
    /// overflow format for charts with more keysounds than classic BMS can address - and anything
    /// written by it came back as "unrecognised". The bytes go to <see cref="BmsonChartSerializer"/>
    /// directly: bmson is UTF-8 JSON by definition, so the classic reader's Shift-JIS decode rules
    /// do not apply.
    /// </summary>
    internal sealed class BmsonOpenFile : IOpenFile
    {
        public bool Open(string filename, out PlayerData playerData)
        {
            try
            {
                playerData = BmsonChartSerializer.Parse(File.ReadAllBytes(filename));
                return true;
            }
            catch (ChartLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ChartLoadException(ChartLoadError.Unexpected,
                    "The bmson file could not be read.", null, ex);
            }
        }

        public string GetName() => "bmson";
        public string GetDescription() => "Be-Music JSON";
        public string GetExtension() => "bmson";
        public Form GetSettingsForm() => null;
    }
}
