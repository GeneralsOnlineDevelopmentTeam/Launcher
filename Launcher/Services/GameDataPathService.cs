using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace LauncherWinUI.Services
{
    /// <summary>
    /// Resolves the "Command and Conquer Generals Zero Hour Data" folder.
    /// Prefers the DataPath registry value written by the game, falls back to probing
    /// known Documents locations, and finally asks the user to locate the folder.
    /// </summary>
    public static class GameDataPathService
    {
        private const string RegistryKeyPath = @"Software\GeneralsOnline";
        private const string RegistryValueName = "DataPath";
        private const string DataFolderName = "Command and Conquer Generals Zero Hour Data";
        private const string OptionsFileName = "Options.ini";
        private const string GeneralsOnlineFolderName = "GeneralsOnlineData";

        private static string? _cachedPath;

        /// <summary>
        /// The resolved data folder. Returns null only if the user declined to locate it.
        /// </summary>
        public static string? DataPath => _cachedPath ??= Resolve();

        /// <summary>
        /// The GeneralsOnlineData subfolder inside the data folder, or null if unresolved.
        /// </summary>
        public static string? GeneralsOnlineDataPath
        {
            get
            {
                string? root = DataPath;
                return root == null ? null : Path.Combine(root, GeneralsOnlineFolderName);
            }
        }

        /// <summary>
        /// Builds a path inside the data folder, or null if the folder is unresolved.
        /// </summary>
        public static string? GetDataFilePath(params string[] relativeParts)
        {
            string? root = DataPath;
            if (root == null)
                return null;

            return Path.Combine(new[] { root }.Concat(relativeParts).ToArray());
        }

        /// <summary>
        /// Builds a path inside the GeneralsOnlineData folder, or null if unresolved.
        /// </summary>
        public static string? GetGeneralsOnlineFilePath(string fileName)
            => GetDataFilePath(GeneralsOnlineFolderName, fileName);

        private static string? Resolve()
        {
            string? fromRegistry = ReadRegistryDataPath();
            if (IsValidDataFolder(fromRegistry))
                return fromRegistry;

            /*
            foreach (string candidate in GetCandidatePaths())
            {
                if (IsValidDataFolder(candidate))
                {
                    WriteRegistryDataPath(candidate);
                    return candidate;
                }
            }
            */

            string? picked = PromptUserForDataFolder();
            if (picked != null)
                WriteRegistryDataPath(picked);

            return picked;
        }

        private static string? ReadRegistryDataPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                return key?.GetValue(RegistryValueName) as string;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteRegistryDataPath(string path)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                key?.SetValue(RegistryValueName, path, RegistryValueKind.String);
            }
            catch { }
        }

        private static IEnumerable<string> GetCandidatePaths()
        {
            var roots = new List<string>();

            void AddRoot(string? root)
            {
                if (!string.IsNullOrWhiteSpace(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                    roots.Add(root);
            }

            AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddRoot(Environment.GetEnvironmentVariable("OneDrive"));
            AddRoot(Environment.GetEnvironmentVariable("OneDriveCommercial"));
            AddRoot(Environment.GetEnvironmentVariable("OneDriveConsumer"));
            AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            foreach (string root in roots)
            {
                yield return Path.Combine(root, DataFolderName);
                yield return Path.Combine(root, "Documents", DataFolderName);
            }
        }

        private static bool IsValidDataFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                return File.Exists(Path.Combine(path, OptionsFileName));
            }
            catch
            {
                return false;
            }
        }

        private static string? PromptUserForDataFolder()
        {
            string initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            while (true)
            {
                var result = MessageBox.Show(
                    $"The launcher could not find your \"{DataFolderName}\" folder.\n\n" +
                    $"Please locate the folder that contains {OptionsFileName}. " +
                    "It is usually inside your Documents folder (or your OneDrive Documents folder).\n\nThe folder name may be localized if you have a non-English version of the game.\n\n" +
                    "Click OK to browse for it",
                    "Game Data Folder Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.OK)
                    return null;

                var dialog = new OpenFolderDialog
                {
                    Title = $"Select the folder containing {OptionsFileName}",
                    InitialDirectory = initialDirectory,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true)
                    return null;

                string selected = dialog.FolderName;

                if (IsValidDataFolder(selected))
                    return selected;

                // Allow selecting a parent folder (e.g. Documents) that contains the data folder.
                string nested = Path.Combine(selected, DataFolderName);
                if (IsValidDataFolder(nested))
                    return nested;

                initialDirectory = selected;

                MessageBox.Show(
                    $"\"{selected}\" does not contain {OptionsFileName}.\n\nPlease try again.",
                    "Invalid Folder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
