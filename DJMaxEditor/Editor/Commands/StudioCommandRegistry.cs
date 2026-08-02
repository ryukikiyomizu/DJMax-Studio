using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace DJMaxEditor.Editor.Commands
{
    public sealed class StudioShortcutConflict
    {
        public StudioShortcutConflict(Keys shortcut, IEnumerable<string> commandIds)
        {
            Shortcut = shortcut;
            CommandIds = commandIds.ToArray();
        }

        public Keys Shortcut { get; private set; }
        public string[] CommandIds { get; private set; }
    }

    public sealed class StudioCommandRegistry
    {
        private readonly Dictionary<string, StudioCommand> _commands =
            new Dictionary<string, StudioCommand>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Keys> _sessionShortcuts =
            new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<StudioCommand> Commands
        {
            get { return _commands.Values.OrderBy(command => command.Category).ThenBy(command => command.DisplayName); }
        }

        public void Register(StudioCommand command)
        {
            if (command == null) throw new ArgumentNullException("command");
            if (_commands.ContainsKey(command.Id))
            {
                throw new InvalidOperationException("Command ID is already registered: " + command.Id);
            }
            _commands.Add(command.Id, command);
        }

        public StudioCommand Get(string id)
        {
            StudioCommand command;
            return id != null && _commands.TryGetValue(id, out command) ? command : null;
        }

        public Keys GetCurrentShortcut(string id)
        {
            StudioCommand command = Get(id);
            if (command == null)
            {
                return Keys.None;
            }

            Keys shortcut;
            return _sessionShortcuts.TryGetValue(command.Id, out shortcut)
                ? shortcut
                : command.DefaultShortcut;
        }

        public bool ReassignShortcut(string id, Keys shortcut)
        {
            StudioCommand command = Get(id);
            if (command == null)
            {
                return false;
            }

            _sessionShortcuts[command.Id] = shortcut;
            return true;
        }

        public bool ResetShortcut(string id)
        {
            StudioCommand command = Get(id);
            if (command == null)
            {
                return false;
            }

            _sessionShortcuts.Remove(command.Id);
            return true;
        }

        public void ResetShortcutsToDefaults()
        {
            _sessionShortcuts.Clear();
        }

        public IEnumerable<StudioCommand> Search(string query)
        {
            string term = (query ?? string.Empty).Trim();
            if (term.Length == 0)
            {
                return Commands;
            }

            return Commands.Where(command =>
                command.Id.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.Category.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public IEnumerable<StudioShortcutConflict> FindShortcutConflicts()
        {
            foreach (var shortcutGroup in _commands.Values
                .Where(command => GetCurrentShortcut(command.Id) != Keys.None)
                .GroupBy(command => GetCurrentShortcut(command.Id)))
            {
                var commands = shortcutGroup.ToArray();
                var conflicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int first = 0; first < commands.Length; first++)
                {
                    for (int second = first + 1; second < commands.Length; second++)
                    {
                        if (ContextsOverlap(commands[first].ActiveContext, commands[second].ActiveContext))
                        {
                            conflicted.Add(commands[first].Id);
                            conflicted.Add(commands[second].Id);
                        }
                    }
                }

                if (conflicted.Count > 1)
                {
                    yield return new StudioShortcutConflict(shortcutGroup.Key, conflicted);
                }
            }
        }

        public bool TryExecuteShortcut(
            Keys shortcut,
            StudioCommandContext activeContext,
            out string disabledExplanation)
        {
            StudioCommand[] matches = _commands.Values
                .Where(command =>
                    GetCurrentShortcut(command.Id) == shortcut &&
                    IsActive(command.ActiveContext, activeContext))
                .ToArray();

            if (matches.Length != 1)
            {
                disabledExplanation = matches.Length > 1
                    ? "Shortcut conflict: " + string.Join(", ", matches.Select(command => command.Id))
                    : string.Empty;
                return false;
            }

            return matches[0].TryExecute(out disabledExplanation);
        }

        private static bool ContextsOverlap(StudioCommandContext first, StudioCommandContext second)
        {
            if (first == StudioCommandContext.Global || second == StudioCommandContext.Global)
            {
                return true;
            }
            return (first & second) != 0;
        }

        private static bool IsActive(StudioCommandContext commandContext, StudioCommandContext activeContext)
        {
            return commandContext == StudioCommandContext.Global ||
                (commandContext & activeContext) != 0;
        }
    }
}
