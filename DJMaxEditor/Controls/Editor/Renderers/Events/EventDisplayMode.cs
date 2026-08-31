using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DJMaxEditor.Controls.Editor.Renderers.Events
{
    /// <summary>
    /// Which per-note value the horizontal surfaces stamp on each event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="None"/> is deliberately value 0, i.e. the default: a freshly opened chart
    /// draws no note labels at all. Every label mode costs one text blit per visible event,
    /// which on a dense chart is thousands of blits per frame, so having a label mode as the
    /// default made "lowest FPS" the out-of-the-box experience.
    /// </para>
    /// <para>
    /// There is no <c>Attribute</c> mode. It used to be the default and was the slowest thing
    /// on screen; the attribute of the selected event is available - and editable - in the
    /// Properties inspector, which costs nothing per frame, so nothing was lost by dropping
    /// the overlay. Callers that switch on this enum must treat <see cref="None"/> as
    /// "draw nothing" explicitly rather than leaning on a <c>default:</c> arm.
    /// </para>
    /// </remarks>
    public enum EventDisplayMode
    {
        None = 0,
        Instrument,
        Velocity,
        Pan,
        Duration,
    }
}
