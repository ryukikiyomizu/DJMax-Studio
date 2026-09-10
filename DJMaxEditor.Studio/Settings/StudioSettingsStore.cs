using System;
using System.IO;
using System.Text;
using System.Text.Json;
using DJMaxEditor.Diagnostics;

namespace DJMaxEditor.Studio.Settings
{
    /// <summary>
    /// Reads and writes <see cref="StudioSettings"/> as JSON under
    /// <c>%LocalAppData%\DJMaxEditor\studio-settings.json</c>.
    ///
    /// <para>
    /// Same folder the BGA transcode cache already uses, for the same reason: it is per-user, it is
    /// not roamed to another machine that may not have the same audio device, and it survives the
    /// editor being moved or rebuilt. Nothing is written beside the exe - a portable copy on a
    /// read-only share still opens.
    /// </para>
    /// <para>
    /// JSON and not the .NET settings machinery: the file is meant to be readable and hand-editable,
    /// and <c>Properties.Settings</c> would put it in a versioned per-build folder that a rebuilt
    /// exe silently stops finding. Which means the file is also a text file a user can break, so
    /// every read path here is total - a missing, truncated, half-written or hostile file produces
    /// defaults and a log line, never an exception and never a dialog on startup.
    /// </para>
    /// </summary>
    public sealed class StudioSettingsStore
    {
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            // Both of these are about surviving a hand-edited file: a trailing comma after the last
            // property and a `// why I changed this` note are exactly what someone editing JSON by
            // hand leaves behind, and neither is worth losing every setting over.
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        private readonly string _path;

        /// <summary>The real store, at the per-user path.</summary>
        public StudioSettingsStore()
            : this(DefaultPath)
        {
        }

        /// <param name="path">
        /// Where the file lives. The tests pass a temporary path; nothing else should.
        /// </param>
        public StudioSettingsStore(string path)
        {
            _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        }

        /// <summary><c>%LocalAppData%\DJMaxEditor\studio-settings.json</c>.</summary>
        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DJMaxEditor",
                    "studio-settings.json");
            }
        }

        /// <summary>The file this store reads and writes.</summary>
        public string Path_ { get { return _path; } }

        /// <summary>
        /// Whether the last <see cref="Load"/> found a usable file. False both for "first launch"
        /// and for "the file was unreadable"; the preferences window shows it so a settings file
        /// that is being ignored is visible rather than mysterious.
        /// </summary>
        public bool LoadedFromDisk { get; private set; }

        /// <summary>
        /// The settings on disk, or defaults. Always returns a normalised object and never throws.
        /// </summary>
        public StudioSettings Load()
        {
            LoadedFromDisk = false;
            StudioSettings settings = null;
            try
            {
                if (File.Exists(_path))
                {
                    string json = File.ReadAllText(_path, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        settings = JsonSerializer.Deserialize<StudioSettings>(json, ReadOptions);
                        LoadedFromDisk = settings != null;
                    }
                }
            }
            catch (Exception ex)
            {
                // Anything at all: no permission, a directory where the file should be, a truncated
                // write from a machine that lost power, `null` as the whole document. All of it is
                // "start with defaults", because there is no user to ask yet.
                DiagnosticLog.Exception("settings.load", ex);
                settings = null;
            }

            if (settings == null)
            {
                settings = new StudioSettings();
            }
            settings.Normalise();
            return settings;
        }

        /// <summary>
        /// Writes <paramref name="settings"/> out. Returns false and logs on failure - the caller is
        /// usually a window closing, which is the worst possible moment for a modal error.
        /// </summary>
        public bool Save(StudioSettings settings)
        {
            if (settings == null)
            {
                return false;
            }

            try
            {
                settings.Normalise();

                string directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Write beside the target and move over it. A settings file is small enough that a
                // torn write is unlikely and cheap enough that ruling it out costs nothing - and the
                // alternative is that a crash mid-save loses every preference rather than none.
                string temporary = _path + ".tmp";
                File.WriteAllText(
                    temporary, JsonSerializer.Serialize(settings, WriteOptions), Encoding.UTF8);
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                File.Move(temporary, _path);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Exception("settings.save", ex);
                return false;
            }
        }
    }
}
