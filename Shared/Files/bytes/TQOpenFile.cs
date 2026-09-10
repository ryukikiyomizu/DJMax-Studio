using DJMaxEditor.DJMax;
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DJMaxEditor.Files.bytes
{
    internal class TQOpenFile : IOpenFile
    {
        public string GetDescription()
        {
            return "Technika Q pattern";
        }

        public string GetExtension()
        {
            return "bytes";
        }

        public string GetName()
        {
            return "Technika Q pattern";
        }

        private OpenSettingsDialog m_settingsDialog = new OpenSettingsDialog();

        public Form GetSettingsForm()
        {
            return m_settingsDialog;
        }

        /// <summary>Statistics from the most recent successful open (for diagnostics / UX reporting).</summary>
        public TrailerReadResult LastReadResult { get; private set; }

        public bool Open(string filename, out PlayerData playerData)
        {
            playerData = null;

            if (String.IsNullOrEmpty(filename))
            {
                return false;
            }

            // Bounds-checked, UI-free parse. Typed ChartLoadExceptions propagate to the caller, which
            // turns them into specific diagnostics. Reading never mutates the source file.
            byte[] data = File.ReadAllBytes(filename);
            TrailerReadResult stats;
            playerData = TrailerChartReader.Read(data, m_settingsDialog.RenameInst, out stats);
            LastReadResult = stats;

            DJMaxEditor.Diagnostics.DiagnosticLog.Write("open.trailer",
                $"{System.IO.Path.GetFileName(filename)}: instruments={stats.InstrumentsRead}, tracks={stats.TracksRead}, " +
                $"events={stats.EventsRead}, wav->ogg remapped={stats.AudioRemappedWavToOgg}, " +
                $"renameOption={(m_settingsDialog.RenameInst ? "on" : "off")}, readOnly={playerData.IsReadOnly}");

            return true;
        }
    }
}
