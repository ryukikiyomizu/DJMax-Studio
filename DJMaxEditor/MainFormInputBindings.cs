using System.Windows.Forms;

namespace DJMaxEditor
{
    internal static class MainFormInputBindings
    {
        internal static bool IsPlayPauseKey(Keys keyData)
        {
            return keyData == Keys.Space;
        }
    }
}
