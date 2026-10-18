// <copyright file="ProcessRunner.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    using System;
    using System.Diagnostics;
    using System.Text;

    public class ProcessRunner : IProcessRunner
    {
        public ProcessResult Run(string fileName, string arguments, TimeSpan timeout)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            StringBuilder standardError = new StringBuilder();
            using (Process process = new Process { StartInfo = startInfo })
            {
                // Both streams are drained asynchronously so the child can never block on a full pipe.
                process.OutputDataReceived += (sender, args) => { };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        lock (standardError)
                        {
                            standardError.AppendLine(args.Data);
                        }
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool timedOut = !process.WaitForExit((int)timeout.TotalMilliseconds);
                if (timedOut)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited in the meantime.
                    }
                }

                // Wait for the asynchronous readers to flush.
                process.WaitForExit();

                lock (standardError)
                {
                    return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, standardError.ToString());
                }
            }
        }
    }
}
