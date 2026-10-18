// <copyright file="IProcessRunner.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    using System;

    /// <summary>
    /// Runs a short-lived process to completion. Abstracted so the hardware detection can be tested without ffmpeg.
    /// </summary>
    public interface IProcessRunner
    {
        ProcessResult Run(string fileName, string arguments, TimeSpan timeout);
    }

    public sealed class ProcessResult
    {
        public ProcessResult(int exitCode, bool timedOut, string standardError)
        {
            this.ExitCode = exitCode;
            this.TimedOut = timedOut;
            this.StandardError = standardError ?? string.Empty;
        }

        public int ExitCode { get; }

        public bool TimedOut { get; }

        public string StandardError { get; }

        public bool Succeeded => !this.TimedOut && this.ExitCode == 0;
    }
}
