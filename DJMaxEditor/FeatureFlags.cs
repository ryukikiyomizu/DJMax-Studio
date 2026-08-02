using System;

namespace DJMaxEditor
{
    /// <summary>
    /// Central access to reversible editor features. Timeline V2 remains opt-in until its rollout
    /// gates pass; missing or malformed values always select the legacy editor.
    /// </summary>
    public static class FeatureFlags
    {
        public static bool UseTimelineV2
        {
            get { return Properties.Settings.Default.UseTimelineV2; }
        }

        public static bool ParseUseTimelineV2(string value)
        {
            bool enabled;
            return Boolean.TryParse(value, out enabled) && enabled;
        }

        public static void SetUseTimelineV2(bool enabled)
        {
            Properties.Settings.Default.UseTimelineV2 = enabled;
            Properties.Settings.Default.Save();
        }
    }
}
