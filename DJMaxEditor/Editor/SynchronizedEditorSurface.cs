using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace DJMaxEditor.Editor
{
    /// <summary>
    /// Fans one editor surface command out to a primary horizontal surface plus any
    /// number of synchronized secondary surfaces (today: the ptSequencer-style vertical
    /// timeline). Every existing shell call site keeps talking to a single
    /// <see cref="IEditorSurface"/>, so binding, invalidation, zoom, playhead, and view
    /// state stay in lockstep for both the V1 and V2 editor paths without the shell
    /// having to know how many surfaces are on screen.
    /// </summary>
    public sealed class SynchronizedEditorSurface : IEditorSurface
    {
        private readonly IEditorSurface _primary;
        private readonly IEditorSurface[] _secondaries;

        public SynchronizedEditorSurface(IEditorSurface primary, params IEditorSurface[] secondaries)
        {
            if (primary == null) throw new ArgumentNullException("primary");

            _primary = primary;
            var followers = new List<IEditorSurface>();
            if (secondaries != null)
            {
                foreach (IEditorSurface surface in secondaries)
                {
                    if (surface != null && !object.ReferenceEquals(surface, primary))
                    {
                        followers.Add(surface);
                    }
                }
            }
            _secondaries = followers.ToArray();
        }

        /// <summary>The surface that owns editing gestures and the shell's focus.</summary>
        public IEditorSurface Primary
        {
            get { return _primary; }
        }

        /// <summary>The primary control; secondary views are hosted by the shell itself.</summary>
        public Control View
        {
            get { return _primary.View; }
        }

        public bool SupportsEditing
        {
            get { return _primary.SupportsEditing; }
        }

        public int PlayheadVirtualTick
        {
            get { return _primary.PlayheadVirtualTick; }
            set
            {
                _primary.PlayheadVirtualTick = value;
                for (int i = 0; i < _secondaries.Length; i++)
                {
                    _secondaries[i].PlayheadVirtualTick = value;
                }
            }
        }

        public void Bind(EditorDocumentContext document)
        {
            if (document == null) throw new ArgumentNullException("document");

            _primary.Bind(document);
            for (int i = 0; i < _secondaries.Length; i++)
            {
                _secondaries[i].Bind(document);
            }
        }

        public void InvalidateView()
        {
            _primary.InvalidateView();
            for (int i = 0; i < _secondaries.Length; i++)
            {
                _secondaries[i].InvalidateView();
            }
        }

        /// <summary>
        /// Zoom is only propagated when the primary accepts it, so a rejected zoom can
        /// never leave the surfaces at different scales.
        /// </summary>
        public bool TrySetTimeZoom(float zoom)
        {
            if (!_primary.TrySetTimeZoom(zoom))
            {
                return false;
            }

            for (int i = 0; i < _secondaries.Length; i++)
            {
                _secondaries[i].TrySetTimeZoom(zoom);
            }
            return true;
        }

        public EditorViewState CaptureViewState()
        {
            return _primary.CaptureViewState();
        }

        public void RestoreViewState(EditorViewState state)
        {
            if (state == null) return;

            _primary.RestoreViewState(state);
            for (int i = 0; i < _secondaries.Length; i++)
            {
                _secondaries[i].RestoreViewState(state);
            }
        }
    }
}
