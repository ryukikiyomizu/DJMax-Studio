namespace DJMaxEditor.Files
{
    /// <summary>
    /// Platform-neutral file-handler metadata. UI settings are a separate optional capability
    /// so chart codecs can be reused by non-Windows frontends.
    /// </summary>
    internal interface IFile
    {
        string GetName();

        string GetDescription();

        string GetExtension();
    }
}
