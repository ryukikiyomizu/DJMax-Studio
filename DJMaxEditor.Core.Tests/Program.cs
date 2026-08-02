using System;
using System.Linq;
using DJMaxEditor.DJMax;
using DJMaxEditor.Editor;

namespace DJMaxEditor.Core.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Run("CoreHasNoDesktopOrNativeReferences", CoreHasNoDesktopOrNativeReferences);
            Run("CoreDoesNotContainProtectedImplementations", CoreDoesNotContainProtectedImplementations);
            Run("DocumentContextPreservesAuthoritativeModelIdentity", DocumentContextPreservesAuthoritativeModelIdentity);

            Console.WriteLine(
                "== CORE RESULT: " + _passed + " passed, " + _failed + " failed ==");
            return _failed;
        }

        private static void CoreHasNoDesktopOrNativeReferences()
        {
            string[] forbidden =
            {
                "System.Windows.Forms",
                "PresentationCore",
                "PresentationFramework",
                "WindowsBase",
                "WindowsFormsIntegration",
                "WeifenLuo.WinFormsUI.Docking",
                "fmod",
                "fmodex"
            };

            string[] references = typeof(PlayerData).Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .ToArray();

            foreach (string name in forbidden)
            {
                Assert(!references.Any(reference =>
                    string.Equals(reference, name, StringComparison.OrdinalIgnoreCase)),
                    "Core references forbidden assembly: " + name);
            }
        }

        private static void CoreDoesNotContainProtectedImplementations()
        {
            string[] forbiddenTypeNames =
            {
                "ChartFormatDetector",
                "BmsChartSerializer",
                "BmsonChartSerializer",
                "TrailerChartReader",
                "PtCodec",
                "AudioPlayerFmodEx",
                "PTOpenFile",
                "PTSaveFile",
                "BmsOpenFile",
                "BmsSaveFile",
                "BmsonSaveFile"
            };

            string[] typeNames = typeof(PlayerData).Assembly
                .GetTypes()
                .Select(type => type.Name)
                .ToArray();

            foreach (string name in forbiddenTypeNames)
            {
                Assert(!typeNames.Contains(name),
                    "Core contains protected implementation: " + name);
            }
        }

        private static void DocumentContextPreservesAuthoritativeModelIdentity()
        {
            var model = new PlayerData();
            model.Tracks.AddTrack(new TrackData(0));
            var context = new EditorDocumentContext(model, "identity.pt");
            var created = context.Edits.CreateEvent(
                new EventData { EventType = EventType.Note },
                0,
                192);

            Assert(ReferenceEquals(model, context.Model),
                "EditorDocumentContext copied or replaced PlayerData");
            Assert(created != null, "Shared editing service did not create the event");
            Assert(ReferenceEquals(
                    created,
                    model.Tracks.GetTrackAtIndex(0).Events.Single()),
                "Editing service mutated a different model");
            Assert(context.Selection.Items.Any(item => ReferenceEquals(item, created)),
                "Selection does not share the created event identity");
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception exception)
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name + ": " + exception.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
