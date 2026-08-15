using Colossal.Logging;
using System;

namespace ConstructionAnimation
{
    internal static class ModLog
    {
        private static readonly ILog Logger = LogManager.GetLogger("ConstructionAnimation");

        public static void Info(string message)
        {
            Logger.Info(message);
            Console.WriteLine($"[ConstructionAnimation] [INFO] {message}");
        }

        public static void Warn(string message)
        {
            Logger.Warn(message);
        }

        public static void Error(string message)
        {
            Logger.Error(message);
        }
    }
}
