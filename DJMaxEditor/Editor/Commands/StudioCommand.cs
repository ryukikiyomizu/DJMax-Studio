using System;
using System.Windows.Forms;

namespace DJMaxEditor.Editor.Commands
{
    [Flags]
    public enum StudioCommandContext
    {
        None = 0,
        Global = 1,
        Document = 2,
        Timeline = 4,
        Selection = 8,
        Playback = 16
    }

    public sealed class StudioCommand
    {
        private readonly Func<bool> _availability;
        private readonly Func<string> _disabledExplanation;
        private readonly Action _execute;

        public StudioCommand(
            string id,
            string displayName,
            string category,
            Keys defaultShortcut,
            StudioCommandContext activeContext,
            Func<bool> availability,
            Func<string> disabledExplanation,
            Action execute)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A stable command ID is required.", "id");
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", "displayName");
            if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("A category is required.", "category");
            if (availability == null) throw new ArgumentNullException("availability");
            if (disabledExplanation == null) throw new ArgumentNullException("disabledExplanation");
            if (execute == null) throw new ArgumentNullException("execute");

            Id = id;
            DisplayName = displayName;
            Category = category;
            DefaultShortcut = defaultShortcut;
            ActiveContext = activeContext;
            _availability = availability;
            _disabledExplanation = disabledExplanation;
            _execute = execute;
        }

        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public string Category { get; private set; }
        public Keys DefaultShortcut { get; private set; }
        public StudioCommandContext ActiveContext { get; private set; }
        public bool IsAvailable { get { return _availability(); } }
        public string DisabledExplanation
        {
            get { return IsAvailable ? string.Empty : (_disabledExplanation() ?? string.Empty); }
        }

        public bool TryExecute(out string disabledExplanation)
        {
            if (!IsAvailable)
            {
                disabledExplanation = DisabledExplanation;
                return false;
            }

            _execute();
            disabledExplanation = string.Empty;
            return true;
        }
    }
}
