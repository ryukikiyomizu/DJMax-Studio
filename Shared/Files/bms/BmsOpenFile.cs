using System;
using System.IO;
using System.Windows.Forms;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Files.bms
{
    internal sealed class BmsOpenFile : IOpenFile
    {
        public bool Open(string filename, out PlayerData playerData)
        {
            try
            {
                playerData = BmsChartSerializer.Parse(BmsChartSerializer.Decode(File.ReadAllBytes(filename)));
                return true;
            }
            catch (ChartLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ChartLoadException(ChartLoadError.Unexpected,
                    "The BMS file could not be read.", null, ex);
            }
        }

        public string GetName() => "bms";
        public string GetDescription() => "Be-Music Script";
        public string GetExtension() => "bms;bme;bml;pms";
        public Form GetSettingsForm() => null;
    }
}
