using System;

namespace DJMaxEditor.UI
{
    public enum StudioWorkspacePreset
    {
        Editing,
        Preview,
        Audio,
        Compact
    }

    public sealed class StudioWorkspaceRequestedEventArgs : EventArgs
    {
        public StudioWorkspaceRequestedEventArgs(StudioWorkspacePreset preset)
        {
            Preset = preset;
        }

        public StudioWorkspacePreset Preset { get; private set; }
    }

    /// <summary>
    /// A request to switch something on or off. Carries the state the user asked for rather than
    /// "flip it", so the shell stays authoritative: it can decline (nothing to show) and push the
    /// real state back without the two ever disagreeing.
    /// </summary>
    public sealed class StudioToggleRequestedEventArgs : EventArgs
    {
        public StudioToggleRequestedEventArgs(bool requestedState)
        {
            RequestedState = requestedState;
        }

        public bool RequestedState { get; private set; }
    }
}
