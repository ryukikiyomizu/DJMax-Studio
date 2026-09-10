using DJMaxEditor.Preview;

namespace DJMaxEditor.Studio.Preview
{
    /// <summary>
    /// What both gameplay playfield panels implement, so the shell can drive either one without
    /// knowing which game it belongs to.
    ///
    /// <para>
    /// The contract is exactly the trio <c>MainWindow</c> has always used on the TECHNIKA view:
    /// adopt a projection at document-open time, drop it on close, and move the drawing to the
    /// playhead once per virtual-tick change from the render pump. <see cref="HasPlayfield"/>
    /// is the cheap guard <c>Sync</c> needs before building a frame.
    /// </para>
    /// </summary>
    internal interface IGameplayPlayfieldView
    {
        /// <summary>True while a projection this view can actually draw is bound.</summary>
        bool HasPlayfield { get; }

        void Bind(GameplayPreviewProjection projection);

        void Unbind();

        /// <summary>Moves the drawing to a playhead position, in the editor's virtual tick space.</summary>
        void Sync(int playheadVirtualTick);
    }
}
