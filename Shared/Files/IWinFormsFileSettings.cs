using System.Windows.Forms;

namespace DJMaxEditor.Files
{
    /// <summary>
    /// Optional Windows-only settings UI provided by legacy file handlers.
    /// The chart-file interfaces themselves stay platform neutral.
    /// </summary>
    internal interface IWinFormsFileSettings
    {
        Form GetSettingsForm();
    }
}
