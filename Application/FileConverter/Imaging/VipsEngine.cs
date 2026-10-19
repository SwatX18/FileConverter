// <copyright file="VipsEngine.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Imaging
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    using FileConverter.Diagnostics;

    /// <summary>
    /// Loads the bundled libvips (see <c>Middleware/libvips/README.md</c>) once, and tells which conversions it handles
    /// (see <see cref="ConversionJobs.ConversionJob_Vips"/>). Everything it doesn't handle stays with ImageMagick.
    /// </summary>
    public static class VipsEngine
    {
        private const uint LoadWithAlteredSearchPath = 0x00000008;

        // Inputs that libvips may handle, with the loader they need. Deliberately absent:
        // - RAW files (cr2, dng, nef, arw...): they are TIFF based, libvips' TIFF loader would only read their preview;
        // - GIF: ImageMagick keeps the palette (a GIF to PNG is 2x smaller), and animated GIFs need it anyway.
        private static readonly Dictionary<string, string> LoaderByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".jpg", "jpegload" },
            { ".jpeg", "jpegload" },
            { ".jfif", "jpegload" },
            { ".png", "pngload" },
            { ".webp", "webpload" },
            { ".tif", "tiffload" },
            { ".tiff", "tiffload" },
            { ".avif", "heifload" },
            { ".heic", "heifload" },
            { ".heif", "heifload" },
        };

        private static readonly Dictionary<OutputType, string> SaverByOutputType = new Dictionary<OutputType, string>
        {
            { OutputType.Jpg, "jpegsave" },
            { OutputType.Png, "pngsave" },
            { OutputType.Webp, "webpsave" },
            { OutputType.Avif, "heifsave" },
        };

        private static readonly object InitializationLock = new object();

        private static bool initialized;
        private static string unavailableReason;
        private static string version;
        private static HashSet<string> availableOperations = new HashSet<string>();

        /// <summary>
        /// Gets whether libvips could be loaded (loads it on the first call).
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                Initialize();
                return unavailableReason == null;
            }
        }

        /// <summary>
        /// Gets the loaded libvips version (e.g. "8.18.7"), or null if libvips is unavailable (loads it on the first call).
        /// </summary>
        public static string Version
        {
            get
            {
                Initialize();
                return version;
            }
        }

        /// <summary>
        /// Gets why libvips is unavailable, or null if it is loaded (loads it on the first call).
        /// </summary>
        public static string UnavailableReason
        {
            get
            {
                Initialize();
                return unavailableReason;
            }
        }

        /// <summary>
        /// Gets why ImageMagick must convert this file instead of libvips, without opening it (null: libvips can try).
        /// </summary>
        public static string GetBlocker(OutputType outputType, string inputFilePath)
        {
            if (!SaverByOutputType.TryGetValue(outputType, out string saver))
            {
                return $"{outputType} outputs are written by ImageMagick";
            }

            string extension = Path.GetExtension(inputFilePath ?? string.Empty);
            if (!LoaderByExtension.TryGetValue(extension, out string loader))
            {
                return $"{extension.ToLowerInvariant()} files are read by ImageMagick";
            }

            if (!IsAvailable)
            {
                return unavailableReason;
            }

            if (!availableOperations.Contains(loader) || !availableOperations.Contains(saver))
            {
                return $"this libvips build has no {(availableOperations.Contains(loader) ? saver : loader)}";
            }

            return null;
        }

        /// <summary>
        /// Sets the number of threads libvips uses per image (a process-wide setting: the jobs of a batch share the same
        /// budget), so that parallel jobs don't oversubscribe the CPU.
        /// </summary>
        /// <param name="threadBudget">The job's <see cref="ConversionJobs.ConversionJob.ThreadBudget"/>; 0 means all threads.</param>
        public static void SetConcurrency(int threadBudget)
        {
            NetVips.NetVips.Concurrency = threadBudget > 0 ? threadBudget : Environment.ProcessorCount;
        }

        private static void Initialize()
        {
            lock (InitializationLock)
            {
                if (initialized)
                {
                    return;
                }

                initialized = true;
                try
                {
                    unavailableReason = Load();
                    if (unavailableReason == null)
                    {
                        version = GetVersion();
                    }
                }
                catch (Exception exception)
                {
                    unavailableReason = $"libvips could not be loaded ({exception.GetType().Name}: {exception.Message})";
                }

                Debug.Log(unavailableReason == null ?
                    $"libvips {version} loaded, operations: {string.Join(", ", availableOperations.OrderBy(name => name))}." :
                    $"Image engine: {unavailableReason}, ImageMagick converts every image.");
            }
        }

        /// <returns>Null on success, why libvips is unavailable otherwise.</returns>
        /// <remarks>
        /// Must not reference NetVips types: NetVips initializes libvips in a module initializer, which runs as soon as
        /// a method using its types is compiled. That must happen after the DLL is loaded (see <see cref="InitializeNetVips"/>).
        /// </remarks>
        private static string Load()
        {
            // NetVips (on .NET Framework) imports libvips-42.dll, libglib-2.0-0.dll and libgobject-2.0-0.dll by name.
            // Loading libvips by full path first resolves its dependencies from the libvips folder, and the later
            // imports by name then reuse the loaded modules instead of any GLib found on PATH (GTK apps install one).
            string applicationDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string libvipsPath = Path.Combine(applicationDirectory, "libvips", "libvips-42.dll");
            if (!File.Exists(libvipsPath))
            {
                return $"libvips not found ({libvipsPath})";
            }

            if (LoadLibraryEx(libvipsPath, IntPtr.Zero, LoadWithAlteredSearchPath) == IntPtr.Zero)
            {
                return $"libvips could not be loaded (error {Marshal.GetLastWin32Error()})";
            }

            return InitializeNetVips();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string InitializeNetVips()
        {
            if (!NetVips.ModuleInitializer.VipsInitialized)
            {
                Exception exception = NetVips.ModuleInitializer.Exception;
                return $"libvips could not be initialized ({exception?.GetType().Name}: {exception?.Message})";
            }

            // Batches: no operation cache (each file is used once), and no loaders libvips flags as untrusted.
            NetVips.Cache.Max = 0;
            NetVips.NetVips.BlockUntrusted = true;

            LogHandler.Install();

            foreach (string operation in LoaderByExtension.Values.Concat(SaverByOutputType.Values).Distinct())
            {
                if (NetVips.NetVips.TypeFind("VipsOperation", operation) != IntPtr.Zero)
                {
                    availableOperations.Add(operation);
                }
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string GetVersion()
        {
            return $"{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}";
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        /// <summary>
        /// Sends libvips warnings to the diagnostics log. A separate class so that <see cref="VipsEngine"/> has no
        /// NetVips-typed field.
        /// </summary>
        private static class LogHandler
        {
            // Keeps the callback alive: libvips calls it from native code.
            private static NetVips.Log.LogDelegate handler;

            public static void Install()
            {
                handler = (domain, level, message) => Debug.Log($"libvips {level}: {message}");
                NetVips.Log.SetLogHandler("VIPS", NetVips.Enums.LogLevelFlags.Warning | NetVips.Enums.LogLevelFlags.Critical | NetVips.Enums.LogLevelFlags.Error, handler);
            }
        }
    }
}
