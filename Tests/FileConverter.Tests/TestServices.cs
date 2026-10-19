// <copyright file="TestServices.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

// Tests share the services below (Ioc.Default can only be configured once) and some change the settings: run them one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace FileConverter.Tests
{
    using CommunityToolkit.Mvvm.DependencyInjection;

    using FileConverter.Services;

    using Microsoft.Extensions.DependencyInjection;

    /// <summary>
    /// Configures <see cref="Ioc.Default"/> with in-memory settings, for the classes that resolve services themselves
    /// (conversion jobs). Nothing is read from or written to the registry or the user's settings file.
    /// </summary>
    public static class TestServices
    {
        private static readonly object InitializationLock = new object();
        private static TestSettingsService settingsService;

        public static Settings Settings
        {
            get
            {
                Initialize();
                return settingsService.Settings;
            }
        }

        public static void Initialize()
        {
            lock (InitializationLock)
            {
                if (settingsService != null)
                {
                    return;
                }

                settingsService = new TestSettingsService();
                Ioc.Default.ConfigureServices(new ServiceCollection()
                    .AddSingleton<ISettingsService>(settingsService)
                    .BuildServiceProvider());
            }
        }

        private class TestSettingsService : ISettingsService
        {
            public Settings Settings { get; } = new Settings();

            public bool PostInstallationInitialization() => true;

            public void SaveSettings()
            {
            }

            public void RevertSettings()
            {
            }
        }
    }
}
