using Colossal.Logging;
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace ConstructionAnimation
{
    internal static class ModLog
    {
        private static readonly ILog s_Log =
            LogManager.GetLogger(
                "ConstructionAnimation"
            );

        private static readonly object s_FileLock =
            new object();

        private static StreamWriter s_DiagnosticWriter;
        private static string s_DiagnosticPath;
        private static bool s_Initialized;

        public static string DiagnosticPath =>
            s_DiagnosticPath;

        public static void Initialize()
        {
            lock (s_FileLock)
            {
                if (s_Initialized)
                {
                    return;
                }

                s_Initialized = true;

                try
                {
                    string logDirectory =
                        Path.Combine(
                            Application.persistentDataPath,
                            "Logs"
                        );

                    Directory.CreateDirectory(
                        logDirectory
                    );

                    s_DiagnosticPath =
                        Path.Combine(
                            logDirectory,
                            "ConstructionAnimation-diagnostic.log"
                        );

                    FileStream stream =
                        new FileStream(
                            s_DiagnosticPath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite,
                            4096,
                            FileOptions.WriteThrough
                        );

                    s_DiagnosticWriter =
                        new StreamWriter(
                            stream,
                            new UTF8Encoding(
                                false
                            )
                        );

                    s_DiagnosticWriter.AutoFlush =
                        true;

                    s_DiagnosticWriter.WriteLine();
                    s_DiagnosticWriter.WriteLine(
                        "================ NEW SESSION ================"
                    );

                    WriteDiagnosticLineUnsafe(
                        "SESSION",
                        "ConstructionAnimation diagnostic session started."
                    );
                }
                catch (Exception ex)
                {
                    s_DiagnosticWriter =
                        null;

                    TryWrite(
                        () => s_Log.Error(
                            "Failed to initialize diagnostic file: " +
                            ex
                        )
                    );
                }
            }
        }

        public static void Shutdown()
        {
            lock (s_FileLock)
            {
                try
                {
                    if (s_DiagnosticWriter != null)
                    {
                        WriteDiagnosticLineUnsafe(
                            "SESSION",
                            "ConstructionAnimation diagnostic session ended normally."
                        );

                        s_DiagnosticWriter.Flush();
                        s_DiagnosticWriter.Dispose();
                    }
                }
                catch
                {
                }
                finally
                {
                    s_DiagnosticWriter =
                        null;

                    s_Initialized =
                        false;
                }
            }
        }

        public static void Info(
            string message
        )
        {
            TryWrite(
                () => s_Log.Info(
                    message
                )
            );
        }

        public static void Error(
            string message
        )
        {
            TryWrite(
                () => s_Log.Error(
                    message
                )
            );

            WriteDiagnosticLine(
                "ERROR",
                message
            );
        }

        public static void Diagnostic(
            string message
        )
        {
            WriteDiagnosticLine(
                "DIAG",
                message
            );
        }

        public static void Checkpoint(
            string message
        )
        {
            WriteDiagnosticLine(
                "CHECKPOINT",
                message
            );
        }

        private static void WriteDiagnosticLine(
            string category,
            string message
        )
        {
            lock (s_FileLock)
            {
                if (!s_Initialized)
                {
                    Initialize();
                }

                try
                {
                    WriteDiagnosticLineUnsafe(
                        category,
                        message
                    );
                }
                catch
                {
                }
            }
        }

        private static void WriteDiagnosticLineUnsafe(
            string category,
            string message
        )
        {
            if (s_DiagnosticWriter == null)
            {
                return;
            }

            s_DiagnosticWriter.WriteLine(
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " +
                $"[{category}] {message}"
            );
        }

        private static void TryWrite(
            Action writeAction
        )
        {
            try
            {
                writeAction?.Invoke();
            }
            catch
            {
            }
        }
    }
}
