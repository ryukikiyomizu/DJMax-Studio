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
}
