using System;
using System.Windows.Forms;

namespace DJMaxEditor.Editor
{
    public sealed class LegacyEditorSurfaceAdapter : IEditorSurface
    {
        private readonly EditorControl _editor;
        private EditorDocumentContext _document;

        public LegacyEditorSurfaceAdapter(EditorControl editor)
        {
            if (editor == null) throw new ArgumentNullException("editor");
            _editor = editor;
        }

        public Control View
        {
            get { return _editor; }
        }

        public bool SupportsEditing
        {
            get { return true; }
        }

        public int PlayheadVirtualTick
        {
            get
            {
                return _document == null ? 0 : _document.Model.VirtualCurrentTick;
            }
            set
            {
                if (_document != null)
                {
                    // Change-gated inside the editor: this setter is driven by a 16ms shell
                    // timer that runs whenever a chart is loaded, so an unconditional
                    // Redraw() here repainted the whole chart ~62 times a second while idle.
                    _editor.SetPlayheadVirtualTick(value);
                }
            }
        }

        public void Bind(EditorDocumentContext document)
        {
            if (document == null) throw new ArgumentNullException("document");
            bool alreadyBound = _document != null &&
                object.ReferenceEquals(_document.Model, document.Model);
            _document = document;
            if (!alreadyBound)
            {
                _editor.Bind(document);
            }
            else
            {
                _editor.Redraw();
            }
        }

        public void InvalidateView()
        {
            _editor.Redraw();
        }

        public bool TrySetTimeZoom(float zoom)
        {
            if (zoom < EditorControl.MinZoom || zoom > EditorControl.MaxZoom)
            {
                return false;
            }
            _editor.SetZoom(zoom);
            return true;
        }

        public EditorViewState CaptureViewState()
        {
            return new EditorViewState
            {
                PixelsPerTick = _editor.GetZoom(),
                PlayheadVirtualTick = PlayheadVirtualTick
            };
        }

        public void RestoreViewState(EditorViewState state)
        {
            if (state == null) return;
            TrySetTimeZoom((float)state.PixelsPerTick);
            PlayheadVirtualTick = state.PlayheadVirtualTick;
        }
    }
}
