using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace DJMaxEditor.Preview
{
    /// <summary>
    /// Optional local artwork for the vertical RESPECT preview. Owner-extracted
    /// Shared Assets can override the small packaged fallback at runtime. Images
    /// are cloned into memory, so the extraction remains read-only and unlocked.
    /// </summary>
    public sealed class RespectVerticalSkin : IDisposable
    {
        private static readonly IDictionary<string, string> SharedAssetFiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "gear_default_bottom", @"Base Game Skins\Gears\Sprite\gear_default_bottom @33186.png" },
                { "note_blue_shine_4_00000", @"Base Game Skins\Notes\Sprite\note_blue_shine_4_00000 @24869.png" },
                { "note_blue_shine_5_00000", @"Base Game Skins\Notes\Sprite\note_blue_shine_5_00000 @51841.png" },
                { "note_blue_shine_6_00000", @"Base Game Skins\Notes\Sprite\note_blue_shine_6_00000 @27126.png" },
                { "note_white_shine_4_00000", @"Base Game Skins\Notes\Sprite\note_white_shine_4_00000 @52860.png" },
                { "note_white_shine_5_00000", @"Base Game Skins\Notes\Sprite\note_white_shine_5_00000 @50771.png" },
                { "note_white_shine_6_00000", @"Base Game Skins\Notes\Sprite\note_white_shine_6_00000 @26373.png" },
                { "note_analog_shine_01", @"Base Game Skins\Notes\Sprite\note_analog_shine_01 @39360.png" },
                { "note_L1", @"Base Game Skins\Notes\Sprite\note_L1 @26261.png" },
                { "note_L2", @"Base Game Skins\Notes\Sprite\note_L2 @51698.png" },
                { "note_R1", @"Base Game Skins\Notes\Sprite\note_R1 @25489.png" },
                { "note_R2", @"Base Game Skins\Notes\Sprite\note_R2 @51732.png" }
            };

        private readonly Dictionary<string, Image> _images =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        private RespectVerticalSkin()
        {
        }

        public string Id { get; private set; }

        public string DisplayName { get; private set; }

        public string RootPath { get; private set; }

        public string GearVideoPath { get; private set; }

        public bool CanSelectGear { get; private set; }

        public bool CanSelectNotes { get; private set; }

        public bool UsesExtractedAssets { get; private set; }

        public string SourceLabel
        {
            get { return UsesExtractedAssets ? "SHARED ASSETS" : "PACKAGED SKIN"; }
        }

        public bool IsLoaded
        {
            get
            {
                return _images.ContainsKey("gear_default_bottom") &&
                    _images.ContainsKey("judge_line_blue") &&
                    _images.ContainsKey("note_blue_shine_4_00000") &&
                    _images.ContainsKey("note_white_shine_6_00000") &&
                    _images.ContainsKey("note_analog_shine_01") &&
                    _images.ContainsKey("note_L1") &&
                    _images.ContainsKey("note_L2") &&
                    _images.ContainsKey("note_R1") &&
                    _images.ContainsKey("note_R2");
            }
        }

        public static RespectVerticalSkin TryLoad()
        {
            string assemblyDirectory = Path.GetDirectoryName(
                typeof(RespectVerticalSkin).Assembly.Location);
            string fallback = Path.Combine(
                string.IsNullOrWhiteSpace(assemblyDirectory)
                    ? AppDomain.CurrentDomain.BaseDirectory
                    : assemblyDirectory,
                "LocalAssets", "RespectVertical", "Default");

            foreach (string path in ExtractedAssetCandidatePaths())
            {
                RespectVerticalSkin skin = TryLoadFromPaths(path, fallback);
                if (skin != null && skin.UsesExtractedAssets)
                {
                    return skin;
                }
                if (skin != null) skin.Dispose();
            }

            string legacy = Environment.GetEnvironmentVariable(
                "DJMAX_EDITOR_RESPECT_SKIN");
            RespectVerticalSkin legacySkin = TryLoadFlat(legacy);
            if (legacySkin != null) return legacySkin;

            return TryLoadFlat(fallback);
        }

        /// <summary>
        /// Discovers complete gameplay-skin folders. Gear and note selection are
        /// deliberately kept independent, matching the game's EquipmentValues.
        /// Custom packs live beside Default under RespectVertical\Skins or in the
        /// DJMAX_EDITOR_RESPECT_SKINS directory. Incomplete packs are ignored.
        /// </summary>
        public static IList<RespectVerticalSkin> LoadAvailable()
        {
            string assemblyDirectory = Path.GetDirectoryName(
                typeof(RespectVerticalSkin).Assembly.Location);
            string assetsRoot = Path.Combine(
                string.IsNullOrWhiteSpace(assemblyDirectory)
                    ? AppDomain.CurrentDomain.BaseDirectory
                    : assemblyDirectory,
                "LocalAssets", "RespectVertical");
            string fallback = Path.Combine(assetsRoot, "Default");
            string custom = Environment.GetEnvironmentVariable(
                "DJMAX_EDITOR_RESPECT_SKINS");
            if (string.IsNullOrWhiteSpace(custom))
            {
                custom = Path.Combine(assetsRoot, "Skins");
            }

            return LoadAvailableFromPaths(
                fallback,
                ExtractedAssetCandidatePaths().FirstOrDefault(IsSharedAssetsRoot),
                custom);
        }

        public static IList<RespectVerticalSkin> LoadAvailableFromPaths(
            string fallbackRoot,
            string extractedRoot,
            string customRoot)
        {
            var skins = new List<RespectVerticalSkin>();
            AddSkin(skins, TryLoadFlat(fallbackRoot),
                "default", "Default (PS4)");

            if (IsSharedAssetsRoot(extractedRoot))
            {
                AddSkin(skins, TryLoadFromPaths(extractedRoot, fallbackRoot),
                    "respect-v-default", "Default (Respect V)");
            }

            if (!string.IsNullOrWhiteSpace(customRoot) &&
                Directory.Exists(customRoot))
            {
                try
                {
                    foreach (string directory in Directory.GetDirectories(customRoot)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        string id = Path.GetFileName(directory);
                        AddSkin(skins, TryLoadCustom(directory, fallbackRoot),
                            id, FriendlyName(id));
                    }
                }
                catch (IOException)
                {
                    // Optional custom packs must never block the preview.
                }
                catch (UnauthorizedAccessException)
                {
                    // Optional custom packs must never block the preview.
                }
            }

            return skins;
        }

        /// <summary>
        /// Builds one in-memory skin from an extracted Shared Assets root laid
        /// over a flat packaged fallback. Neither directory is changed.
        /// </summary>
        public static RespectVerticalSkin TryLoadFromPaths(
            string extractedRoot,
            string fallbackRoot)
        {
            var skin = new RespectVerticalSkin();
            skin.LoadFlatImages(fallbackRoot);

            if (IsSharedAssetsRoot(extractedRoot))
            {
                skin.LoadSharedAssets(extractedRoot);
                skin.UsesExtractedAssets = true;
                skin.CanSelectGear = true;
                skin.CanSelectNotes = true;
                skin.RootPath = Path.GetFullPath(extractedRoot);
            }
            else
            {
                skin.RootPath = ExistingFullPath(fallbackRoot);
            }

            skin.GearVideoPath = ExistingFile(
                Path.Combine(fallbackRoot ?? string.Empty, "gear_loop.mp4"));

            if (skin.IsLoaded) return skin;
            skin.Dispose();
            return null;
        }

        public static int NormalizeLaneMode(int laneCount)
        {
            return RespectGameplayLayout.NormalizeLaneMode(laneCount);
        }

        public Image Get(string name)
        {
            Image image;
            return name != null && _images.TryGetValue(name, out image)
                ? image
                : null;
        }

        public Image GetNote(int laneCount, int lane)
        {
            int mode = NormalizeLaneMode(laneCount);
            if (mode == 0) return null;

            int noteMode = mode == 8 ? 6 : mode;
            bool blue;
            switch (mode)
            {
                case 4:
                    blue = lane == 1 || lane == 2;
                    break;
                case 5:
                    blue = lane == 1 || lane == 3;
                    break;
                case 6:
                case 8:
                    blue = lane == 1 || lane == 4;
                    break;
                default:
                    blue = false;
                    break;
            }

            return Get("note_" + (blue ? "blue" : "white") +
                "_shine_" + noteMode + "_00000");
        }

        public void Dispose()
        {
            foreach (Image image in _images.Values)
            {
                image.Dispose();
            }
            _images.Clear();
        }

        private static RespectVerticalSkin TryLoadFlat(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return null;
            }

            var skin = new RespectVerticalSkin();
            skin.LoadFlatImages(rootPath);
            skin.RootPath = Path.GetFullPath(rootPath);
            skin.CanSelectGear = true;
            skin.CanSelectNotes = true;
            skin.GearVideoPath = ExistingFile(
                Path.Combine(rootPath, "gear_loop.mp4"));
            if (skin.IsLoaded) return skin;
            skin.Dispose();
            return null;
        }

        private static RespectVerticalSkin TryLoadCustom(
            string rootPath,
            string fallbackRoot)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return null;
            }

            var skin = new RespectVerticalSkin();
            skin.LoadFlatImages(fallbackRoot);
            skin.LoadFlatImages(rootPath);
            skin.RootPath = Path.GetFullPath(rootPath);
            skin.CanSelectGear = HasCustomGear(rootPath);
            skin.CanSelectNotes = HasCustomNotes(rootPath);
            skin.GearVideoPath = ExistingFile(
                Path.Combine(rootPath, "gear_loop.mp4")) ?? ExistingFile(
                Path.Combine(fallbackRoot ?? string.Empty, "gear_loop.mp4"));
            if (skin.IsLoaded && (skin.CanSelectGear || skin.CanSelectNotes))
                return skin;
            skin.Dispose();
            return null;
        }

        private void LoadFlatImages(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return;
            }

            try
            {
                foreach (string file in Directory.GetFiles(rootPath, "*.png"))
                {
                    LoadImage(Path.GetFileNameWithoutExtension(file), file);
                }
            }
            catch (IOException)
            {
                // Optional assets never prevent the vector fallback.
            }
            catch (UnauthorizedAccessException)
            {
                // Optional assets never prevent the vector fallback.
            }
        }

        private void LoadSharedAssets(string rootPath)
        {
            foreach (KeyValuePair<string, string> asset in SharedAssetFiles)
            {
                string file = Path.Combine(rootPath, asset.Value);
                if (File.Exists(file))
                {
                    LoadImage(asset.Key, file);
                }
            }
        }

        private void LoadImage(string name, string file)
        {
            try
            {
                using (Image source = Image.FromFile(file))
                {
                    Image previous;
                    if (_images.TryGetValue(name, out previous))
                    {
                        previous.Dispose();
                    }
                    _images[name] = new Bitmap(source);
                }
            }
            catch (ArgumentException)
            {
                // A malformed optional image must never prevent chart preview.
            }
            catch (IOException)
            {
                // The fallback remains available when a source file is locked.
            }
            catch (UnauthorizedAccessException)
            {
                // The fallback remains available when a source file is inaccessible.
            }
        }

        private static bool IsSharedAssetsRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return false;
            }

            string config = Path.Combine(rootPath, "Config Tables");
            return HasCsvHeader(Path.Combine(config, "gear_skin")) &&
                HasCsvHeader(Path.Combine(config, "note_skin")) &&
                Directory.Exists(Path.Combine(rootPath,
                    "Base Game Skins", "Gears", "Sprite")) &&
                Directory.Exists(Path.Combine(rootPath,
                    "Base Game Skins", "Notes", "Sprite"));
        }

        private static bool HasCsvHeader(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var reader = new StreamReader(path))
                {
                    string header = reader.ReadLine();
                    return header != null && header.StartsWith(
                        "id,", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string ExistingFullPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
                ? Path.GetFullPath(path)
                : null;
        }

        private static string ExistingFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? Path.GetFullPath(path)
                : null;
        }

        private static void AddSkin(
            ICollection<RespectVerticalSkin> skins,
            RespectVerticalSkin skin,
            string id,
            string name)
        {
            if (skin == null) return;
            skin.Id = id;
            skin.DisplayName = name;
            skins.Add(skin);
        }

        private static bool HasCustomGear(string rootPath)
        {
            return HasMatchingFile(rootPath, "gear_*.png") ||
                HasMatchingFile(rootPath, "Gear_*.png") ||
                HasMatchingFile(rootPath, "judge_line*.png") ||
                HasMatchingFile(rootPath, "btn_*.png") ||
                File.Exists(Path.Combine(rootPath, "gear_loop.mp4"));
        }

        private static bool HasCustomNotes(string rootPath)
        {
            return HasMatchingFile(rootPath, "note_*.png");
        }

        private static bool HasMatchingFile(string rootPath, string pattern)
        {
            try
            {
                return Directory.GetFiles(rootPath, pattern).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string FriendlyName(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "Custom skin";
            return string.Join(" ", id.Replace('_', ' ').Replace('-', ' ')
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));
        }

        private static IEnumerable<string> ExtractedAssetCandidatePaths()
        {
            string configured = Environment.GetEnvironmentVariable(
                "DJMAX_EDITOR_RESPECT_ASSETS");
            if (!string.IsNullOrWhiteSpace(configured)) yield return configured;

            string profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                yield return Path.Combine(profile, "Downloads", "Shared Assets");
            }
        }
    }
}
