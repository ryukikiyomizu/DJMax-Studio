using System;

namespace DJMaxEditor.Files.FormatDetection
{
    /// <summary>
    /// Authoritative set of chart container formats the editor can distinguish.
    /// Detection is *positive*: a value other than <see cref="Unknown"/> / <see cref="Malformed"/>
    /// is only returned when the format's signature and structure are actually present.
    /// </summary>
    public enum ChartFormat
    {
        /// <summary>Decrypted Technika/Trilogy PTFF (magic "PTFF" + EZTR track blocks). Opened by PTOpenFile.</summary>
        PtffDecrypted,

        /// <summary>Encrypted Technika/Trilogy PTFF: plaintext "PTFF"/header, encrypted body from 0x18, no EZTR.</summary>
        PtffEncryptedTechnika,

        /// <summary>DJMax Respect V / "Technika Q" trailer-format chart (leading zero sign + 26-byte EOF trailer).</summary>
        TrailerRespectV,

        /// <summary>Cyclon XML chart.</summary>
        CyclonXml,

        /// <summary>Classic text BMS family (.bms/.bme/.bml/.pms).</summary>
        BmsClassic,

        /// <summary>
        /// bmson (Be-Music JSON): a JSON chart whose sound channels are identified by filename,
        /// which lifts the classic family's 1296-slot object-id ceiling.
        /// </summary>
        Bmson,

        /// <summary>
        /// TECHMANIA native track file (track.tech, format version "3"): JSON container of
        /// patterns with pipe-packed note strings, imported onto the TECHNIKA layout.
        /// </summary>
        TechmaniaTrack,

        /// <summary>A file that matches no known chart signature.</summary>
        Unknown,

        /// <summary>A file that matches a known signature but whose structure is invalid/truncated.</summary>
        Malformed
    }

    /// <summary>How strongly the evidence supports the detected format.</summary>
    public enum DetectionConfidence
    {
        /// <summary>No usable evidence (e.g. empty input).</summary>
        None,

        /// <summary>A weak signal (e.g. an extension hint) but no confirmed structure.</summary>
        Low,

        /// <summary>A signature matched but full structure was not (or could not be) validated.</summary>
        Probable,

        /// <summary>Signature and structure both validated.</summary>
        High
    }
}
