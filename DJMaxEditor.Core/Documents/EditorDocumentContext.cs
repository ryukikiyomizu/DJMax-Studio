using System;
using DJMaxEditor.DJMax;

namespace DJMaxEditor.Editor
{
    /// <summary>
    /// Binds view state and capabilities to the authoritative PlayerData instance without copying it.
    /// </summary>
    public sealed class EditorDocumentContext
    {
        public EditorDocumentContext(PlayerData model, string sourcePath)
            : this(model, sourcePath, UndoManager.GetInstance())
        {
        }

        public EditorDocumentContext(PlayerData model, string sourcePath, UndoManager undoManager)
        {
            if (model == null)
            {
                throw new ArgumentNullException("model");
            }
            if (undoManager == null)
            {
                throw new ArgumentNullException("undoManager");
            }

            Model = model;
            SourcePath = sourcePath;
            UndoManager = undoManager;
            Capabilities = DocumentCapabilities.Resolve(model);
            Selection = new ChartSelectionService();
            Edits = new ChartEditController(this, undoManager);
            Clipboard = new ChartClipboardService(this);
            Interaction = new TimelineInteractionState();
        }

        public PlayerData Model { get; private set; }

        public string SourcePath { get; private set; }

        public DocumentCapabilities Capabilities { get; private set; }

        public UndoManager UndoManager { get; private set; }

        public ChartSelectionService Selection { get; private set; }

        public ChartEditController Edits { get; private set; }

        public ChartClipboardService Clipboard { get; private set; }

        public TimelineInteractionState Interaction { get; private set; }

        public void RefreshCapabilities()
        {
            Capabilities = DocumentCapabilities.Resolve(Model);
        }
    }
}
