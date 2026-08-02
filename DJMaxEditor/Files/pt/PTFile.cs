using System;
using System.IO;
using System.Linq;
using UnpackMe.SDK.Core;
using UnpackMe.SDK.Core.Models;

namespace DJMaxEditor.Files.pt
{
    internal abstract class PTFile
    {
        protected const uint EZTR = 0x52545A45;

        /// <summary>
        /// The dead "UnpackMe" online decrypt/encrypt service. It is OFF by default: the editor never
        /// contacts it during normal open/save. It exists only as an explicit, user-opt-in code path
        /// for anyone who stands up a compatible service. See <see cref="OnlineUrl"/>.
        /// </summary>
        public static bool OnlineEnabled = false;

        /// <summary>Counts online attempts actually made — used by tests to prove no silent network I/O.</summary>
        public static int OnlineAttemptCount = 0;

        /// <summary>Hard ceiling on how long we will wait for the online task before giving up.</summary>
        private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(30);

        public static string OnlineUrl => m_unpackMeUrl;

        protected bool TryDoStuffDataOnline(byte[] data, string mode, out byte[] result)
        {
            result = null;

            if (!OnlineEnabled)
            {
                // Explicitly disabled: make NO network request. This is the default.
                DJMaxEditor.Diagnostics.DiagnosticLog.Write("net.blocked",
                    $"Online {mode} requested but the online service is disabled; no request was made.");
                return false;
            }

            try
            {
                OnlineAttemptCount++;
                DJMaxEditor.Diagnostics.DiagnosticLog.Write("net.optin",
                    $"Online {mode} explicitly enabled; contacting {m_unpackMeUrl}.");
                result = DoStuffDataOnline(data, mode);
            }
            catch (Exception e)
            {
                DJMaxEditor.Diagnostics.DiagnosticLog.Write("net.error", $"Online {mode} failed: {e.Message}");
                return false;
            }

            return result != null;
        }

        protected byte[] DoStuffDataOnline(byte[] data, string mode)
        {
            using (UnpackMeClient unpackMeClient = new UnpackMeClient(m_unpackMeUrl))
            {
                unpackMeClient.Authenticate(m_unpackMeClientLogin, m_unpackMeClientPassword);
                var commands = unpackMeClient.GetAvailableCommands();

                var commandName = mode == "decrypt" ? "DJMax *.pt decrypt" : "DJMax *.pt encrypt";

                var decryptCommand = commands.SingleOrDefault(x => x.CommandTitle == commandName);

                using (var stream = new MemoryStream(data))
                {
                    var taskId = unpackMeClient.CreateTaskFromCommandId(decryptCommand.CommandId, stream);

                    TaskModel task;
                    string taskStatus;
                    var deadline = DateTime.UtcNow + OnlineTimeout;
                    do
                    {
                        if (DateTime.UtcNow > deadline)
                        {
                            throw new TimeoutException(
                                $"Online {mode} did not complete within {OnlineTimeout.TotalSeconds:F0}s.");
                        }

                        task = unpackMeClient.GetTaskById(taskId);
                        taskStatus = task.TaskStatus;

                        System.Threading.Thread.Sleep(500);

                    } while (taskStatus != "completed");

                    return unpackMeClient.DownloadToByteArray(taskId);
                }
            }
        }

        private const string m_unpackMeUrl = "http://api.unpackme.shadosoft-tm.com/";

        private const string m_unpackMeClientLogin = "djmaxeditor";

        private const string m_unpackMeClientPassword = "djmaxeditor";
    }
}
