// <copyright file="ConversionService.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Services
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Threading;

    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.DependencyInjection;

    using FileConverter.ConversionJobs;
    using FileConverter.Diagnostics;
    using FileConverter.Hardware;

    public class ConversionService : ObservableObject, IConversionService
    {
        private readonly List<ConversionJob> conversionJobs = new List<ConversionJob>();

        private ISettingsService settingsService;

        public ConversionService(ISettingsService settingsService)
        {
            if (settingsService == null)
            {
                throw new ArgumentNullException(nameof(settingsService));
            }

            this.settingsService = settingsService;

            this.ConversionJobs = this.conversionJobs.AsReadOnly();
        }

        public event System.EventHandler<ConversionJobsTerminatedEventArgs> ConversionJobsTerminated;

        public ReadOnlyCollection<ConversionJob> ConversionJobs
        {
            get;
            private set;
        }

        public void RegisterConversionJob(ConversionJob conversionJob)
        {
            this.conversionJobs.Add(conversionJob);
            this.OnPropertyChanged(nameof(this.ConversionJobs));
        }

        public void ConvertFilesAsync()
        {
            Thread fileConvertionThread = Helpers.InstantiateThread("ConversionQueueThread", this.ConvertFiles);
            fileConvertionThread.Start();
        }

        /// <summary>
        /// Runs the jobs: a preparation thread prepares them in order (output paths, ffmpeg arguments, probing) while this
        /// thread starts every prepared job that the limits allow (in total, per <see cref="ConversionLane"/> and per hard
        /// disk), as soon as it can. It wakes up when a job is prepared or finishes.
        /// </summary>
        private void ConvertFiles()
        {
            System.Diagnostics.Stopwatch batchTime = System.Diagnostics.Stopwatch.StartNew();
            int jobCount = this.conversionJobs.Count;
            Settings settings = this.settingsService.Settings;
            ConversionLimits limits = ConversionLimits.Compute(settings.MaximumNumberOfSimultaneousConversions, settings.MaximumNumberOfSimultaneousGpuConversions, SystemInfo.PhysicalCoreCount);
            Debug.Log($"Conversion limits: {limits} ({SystemInfo.PhysicalCoreCount} cores, {SystemInfo.LogicalProcessorCount} threads).");

            object sync = new object();
            AutoResetEvent wakeUp = new AutoResetEvent(false);
            bool[] prepared = new bool[jobCount];
            bool[] started = new bool[jobCount];
            int preparedCount = 0;
            List<ConversionJob> runningJobs = new List<ConversionJob>();
            Dictionary<ConversionJob, uint[]> hardDisksByJob = new Dictionary<ConversionJob, uint[]>();

            Thread preparationThread = Helpers.InstantiateThread("ConversionPreparationThread", () =>
            {
                for (int index = 0; index < jobCount; index++)
                {
                    this.conversionJobs[index].PrepareConversion();
                    lock (sync)
                    {
                        prepared[index] = true;
                        preparedCount++;
                    }

                    wakeUp.Set();
                }
            });
            preparationThread.Start();

            System.Collections.Specialized.StringCollection files = new System.Collections.Specialized.StringCollection();
            while (true)
            {
                lock (sync)
                {
                    // Running jobs: flags (CD drive) and slots used per lane and per hard disk.
                    ConversionFlags conversionFlags = ConversionFlags.None;
                    Dictionary<ConversionLane, int> jobsPerLane = new Dictionary<ConversionLane, int>();
                    Dictionary<uint, int> jobsPerHardDisk = new Dictionary<uint, int>();
                    foreach (ConversionJob runningJob in runningJobs)
                    {
                        conversionFlags |= runningJob.StateFlags;
                        jobsPerLane[runningJob.Lane] = jobsPerLane.TryGetValue(runningJob.Lane, out int laneCount) ? laneCount + 1 : 1;
                        foreach (uint disk in hardDisksByJob[runningJob])
                        {
                            jobsPerHardDisk[disk] = jobsPerHardDisk.TryGetValue(disk, out int diskCount) ? diskCount + 1 : 1;
                        }
                    }

                    bool jobsWaiting = false;
                    for (int index = 0; index < jobCount; index++)
                    {
                        ConversionJob job = this.conversionJobs[index];
                        if (!prepared[index] || started[index] || job.State != ConversionState.Ready)
                        {
                            continue;
                        }

                        jobsWaiting = true;
                        if (runningJobs.Count >= limits.Total)
                        {
                            break;
                        }

                        ConversionLane lane = job.Lane;
                        jobsPerLane.TryGetValue(lane, out int jobsInLane);
                        if (!job.CanStartConversion(conversionFlags) || jobsInLane >= limits.GetLaneLimit(lane))
                        {
                            continue;
                        }

                        if (!hardDisksByJob.TryGetValue(job, out uint[] hardDisks))
                        {
                            hardDisks = GetHardDisks(job);
                            hardDisksByJob[job] = hardDisks;
                        }

                        if (hardDisks.Any(disk => jobsPerHardDisk.TryGetValue(disk, out int diskCount) && diskCount >= ConversionLimits.JobsPerHardDisk))
                        {
                            continue;
                        }

                        // Share the CPU threads between the jobs expected to run alongside this one in its lane: the running
                        // ones, the prepared ones waiting, and the ones not prepared yet (they may be of this lane).
                        int laneJobs = jobsInLane + 1 + (preparedCount < jobCount ? jobCount - preparedCount : 0);
                        for (int other = index + 1; other < jobCount; other++)
                        {
                            ConversionJob otherJob = this.conversionJobs[other];
                            if (prepared[other] && !started[other] && otherJob.State == ConversionState.Ready && otherJob.Lane == lane)
                            {
                                laneJobs++;
                            }
                        }

                        int simultaneousJobs = Math.Min(laneJobs, Math.Min(limits.Total, limits.GetLaneLimit(lane)));
                        if (hardDisks.Length > 0)
                        {
                            simultaneousJobs = Math.Min(simultaneousJobs, ConversionLimits.JobsPerHardDisk);
                        }

                        job.ThreadBudget = ConversionLimits.ComputeThreadBudget(SystemInfo.LogicalProcessorCount, simultaneousJobs);

                        started[index] = true;
                        runningJobs.Add(job);
                        jobsPerLane[lane] = jobsInLane + 1;
                        foreach (uint disk in hardDisks)
                        {
                            jobsPerHardDisk[disk] = jobsPerHardDisk.TryGetValue(disk, out int diskCount) ? diskCount + 1 : 1;
                        }

                        conversionFlags |= job.StateFlags;
                        string diskUse = hardDisks.Length > 0 ? $", hard disk {string.Join("+", hardDisks)}: {hardDisks.Max(disk => jobsPerHardDisk[disk])}" : string.Empty;
                        Debug.Log($"Start job {index + 1}/{jobCount} ({lane}, {(job.ThreadBudget > 0 ? $"{job.ThreadBudget} threads" : "all threads")}), running: {runningJobs.Count} ({lane}: {jobsInLane + 1}{diskUse}): {job.InputFilePath}");

                        Thread jobThread = Helpers.InstantiateThread(job.GetType().Name, () =>
                        {
                            try
                            {
                                this.ExecuteConversionJob(job);
                            }
                            finally
                            {
                                lock (sync)
                                {
                                    runningJobs.Remove(job);
                                }

                                wakeUp.Set();
                            }
                        });
                        jobThread.Start();

                        if (!files.Contains(job.OutputFilePath))
                        {
                            files.Add(job.OutputFilePath);
                        }
                    }

                    if (preparedCount == jobCount && runningJobs.Count == 0 && !jobsWaiting)
                    {
                        break;
                    }
                }

                // Woken up by a job prepared or finished; the timeout is a safety net.
                wakeUp.WaitOne(500);
            }

            IEnumerable<string> pathCounts = this.conversionJobs.GroupBy(job => job.EncodingPath).Select(group => $"{group.Key} x{group.Count()}");
            int doneCount = this.conversionJobs.Count(job => job.State == ConversionState.Done);
            Debug.Log($"Conversions finished in {batchTime.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s: {doneCount} done, {jobCount - doneCount} failed, paths: {string.Join(", ", pathCounts)}.");

            // Copy the output files to the clipboard
            if (this.settingsService.Settings.CopyFilesInClipboardAfterConversion && files.Count > 0)
            {
                Thread clipboardThread = Helpers.InstantiateThread("CopyFilesToClipboardThread", this.CopyFilesToClipboard);
                clipboardThread.SetApartmentState(ApartmentState.STA);
                clipboardThread.Start(files);
            }

            bool allConversionsSucceed = true;
            for (int index = 0; index < this.conversionJobs.Count; index++)
            {
                allConversionsSucceed &= this.conversionJobs[index].State == ConversionState.Done;
            }

            if (this.ConversionJobsTerminated != null)
            {
                this.ConversionJobsTerminated.Invoke(this, new ConversionJobsTerminatedEventArgs(allConversionsSucceed));
            }
        }

        /// <summary>
        /// Gets the hard disk drives (not SSDs) the job reads or writes, if its input is large enough for the hard disk
        /// limit to matter (see <see cref="ConversionLimits.HardDiskLimitMinimumFileSize"/>).
        /// </summary>
        private static uint[] GetHardDisks(ConversionJob job)
        {
            try
            {
                if (new System.IO.FileInfo(job.InputFilePath).Length < ConversionLimits.HardDiskLimitMinimumFileSize)
                {
                    return new uint[0];
                }
            }
            catch (Exception)
            {
                // Unknown size: apply the limit.
            }

            IEnumerable<string> paths = new[] { job.InputFilePath }.Concat(job.OutputFilePaths ?? new string[0]);
            return paths.Where(path => !string.IsNullOrEmpty(path))
                .Select(SystemInfo.GetHardDiskNumber)
                .Where(disk => disk.HasValue)
                .Select(disk => disk.Value)
                .Distinct()
                .ToArray();
        }

        private void ExecuteConversionJob(object parameter)
        {
            ConversionJob conversionJob = parameter as ConversionJob;
            if (conversionJob == null)
            {
                throw new System.ArgumentException("The parameter must be a conversion job.", nameof(parameter));
            }

            if (conversionJob.State != ConversionState.Ready)
            {
                Debug.LogError("Fail to execute conversion job.");
                return;
            }

            try
            {
                conversionJob.StartConversion();
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failure during conversion: {exception}");
            }
        }

        private void CopyFilesToClipboard(object _filePaths)
        {
            try
            {
                System.Collections.Specialized.StringCollection FilePaths = _filePaths as System.Collections.Specialized.StringCollection;
                System.Windows.Forms.Clipboard.SetFileDropList(FilePaths);
                Debug.Log("Output files copied to the clipboard:");
                for (int index = 0; index < FilePaths.Count; index++)
                {
                    Debug.Log($"  {FilePaths[index]}");
                }
            }
            catch (Exception exception)
            {
                Debug.Log("Can't copy files to the clipboard.");
                Debug.Log($"An exception has been thrown: {exception}.");
            }
        }
    }
}
