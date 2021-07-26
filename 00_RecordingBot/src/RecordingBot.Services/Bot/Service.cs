namespace RecordingBot.Services.Bot{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography.X509Certificates;
    using System.Timers;
    using Microsoft.Graph.Communications.Common.Telemetry;
    using Microsoft.Owin.Hosting;
    using RecordingBot.Services.Http;
    using RecordingBot.Services.Bot;

    /// <summary>
    /// Service is the main entry point independent of Azure.  Anyone instantiating Service needs to first
    /// initialize the DependencyResolver.  Calling Start() on the Service starts the HTTP server that will
    /// listen for incoming Conversation requests from the Skype Platform.
    /// </summary>
    public class Service
    {
        public const long OneDayMiliseconds = 86400000;
        /// <summary>
        /// The singleton instance.
        /// </summary>
        public static readonly Service Instance = new Service();

        /// <summary>
        /// The sync lock.
        /// </summary>
        private readonly object syncLock = new object();

        /// <summary>
        /// The call http server.
        /// </summary>
        private IDisposable callHttpServer;

        /// <summary>
        /// Is the service started.
        /// </summary>
        private bool started;

        /// <summary>
        /// Graph logger instance.
        /// </summary>
        private IGraphLogger logger;

        /// <summary>
        /// Gets the configuration.
        /// </summary>
        public IConfiguration Configuration { get; private set; }

        private Timer BatchUploaderTimer;
        private Timer BatchUploaderKillTimer;

        private System.Diagnostics.Process BlobUploadProcess;

        /// <summary>
        /// Instantiate a custom server (e.g. for testing).
        /// </summary>
        /// <param name="config">The configuration to initialize.</param>
        /// <param name="logger">Logger instance.</param>
        public void Initialize(IConfiguration config, IGraphLogger logger)
        {
            this.Configuration = config;
            this.logger = logger;
        }

        /// <summary>
        /// Start the service.
        /// </summary>
        public void Start()
        {
            lock (this.syncLock)
            {
                if (this.started)
                {
                    throw new InvalidOperationException("The service is already started.");
                }
                RecordingBot.Services.Bot.BotService.Instance.Initialize(this, this.logger);
                //Bot.Bot.Instance.Initialize(this, this.logger);
                System.IO.Directory.CreateDirectory($"C:\\Test");

                // Start HTTP server for calls
                var callStartOptions = new StartOptions();
                foreach (var url in this.Configuration.CallControlListeningUrls)
                {
                    System.IO.File.AppendAllText("C:\\Test\\urls.txt", url.ToString());
                    callStartOptions.Urls.Add(url.ToString());
                }
                this.callHttpServer = WebApp.Start(
                    callStartOptions,
                    (appBuilder) =>
                    {
                        var startup = new HttpConfigurationInitializer();
                        startup.ConfigureSettings(appBuilder, this.logger);
                    });
                try
                {
                    RemoveTemporaryFiles("C:\\Test");
                }
                catch (Exception)
                { }

                System.IO.Directory.CreateDirectory($"C:\\Test");
                this.KillUploaderProcess();

                var today = DateTime.Today;
                var tomorrow = today.AddDays(1);

                // - Setting up blob uploader schedule
                TimeSpan nextRun = default;
                if (today.DayOfWeek == DayOfWeek.Friday || today.DayOfWeek == DayOfWeek.Saturday)
                {
                    nextRun = new TimeSpan(0, 0, 1);
                }
                else if (today.DayOfWeek == DayOfWeek.Thursday)
                {
                    nextRun = DateTime.UtcNow < today.AddHours(12).AddMinutes(30) ?
                        new TimeSpan(new DateTimeOffset(today.Year, today.Month, today.Day, 13, 0, 0, 0, new TimeSpan(0, 0, 0)).Ticks - DateTimeOffset.UtcNow.Ticks) :
                        new TimeSpan(DateTimeOffset.UtcNow.AddMinutes(60).Ticks - DateTimeOffset.UtcNow.Ticks);
                }
                else
                {
                    nextRun = DateTime.UtcNow < today.AddHours(12).AddMinutes(30) ?
                        new TimeSpan(new DateTimeOffset(today.Year, today.Month, today.Day, 13, 0, 0, 0, new TimeSpan(0, 0, 0)).Ticks - DateTimeOffset.UtcNow.Ticks) :
                        new TimeSpan(new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 13, 0, 0, 0, new TimeSpan(0, 0, 0)).Ticks - DateTimeOffset.UtcNow.Ticks);
                }

                if (nextRun.TotalMilliseconds < 0)
                {
                    nextRun = new TimeSpan(0, 0, 1);
                }

                System.IO.File.WriteAllText($"C:\\Test\\nextRun-{DateTime.UtcNow.ToString("dd-MM-yy-HH-mm")}.txt", $"{nextRun.TotalMilliseconds}");

                this.BatchUploaderTimer = new System.Timers.Timer(nextRun.TotalMilliseconds);
                this.BatchUploaderTimer.Elapsed += RunBatchUploader;
                this.BatchUploaderTimer.Enabled = true;
                this.BatchUploaderTimer.AutoReset = false;

                // - Setting up uploader kill schedule
                this.BatchUploaderKillTimer = new System.Timers.Timer();
                this.BatchUploaderKillTimer.Enabled = false;
                this.BatchUploaderKillTimer.AutoReset = false;
                this.BatchUploaderKillTimer.Elapsed += KillBatchUploader;

                this.started = true;
            }
        }

        private static void RemoveTemporaryFiles(string root)
        {
            var temporaryFiles = new List<string>(System.IO.Directory.GetFiles(root, "*.mp4", SearchOption.AllDirectories));
            temporaryFiles = temporaryFiles.Where(x => !x.Contains("final.mp4")).ToList();
            temporaryFiles.AddRange(System.IO.Directory.GetFiles(root, "*.h264", SearchOption.AllDirectories));
            foreach (var file in temporaryFiles)
            {
                System.IO.File.Delete(file);
            }
        }

        private void RunBatchUploader(Object source, ElapsedEventArgs e)
        {

            this.KillUploaderProcess();

            this.BlobUploadProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "blobUploader\\RecorderBlobUploader.exe",
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });

            var tomorrow = DateTime.Today.AddDays(1);

            if (tomorrow.DayOfWeek == DayOfWeek.Friday || tomorrow.DayOfWeek == DayOfWeek.Saturday)
            {
                // System.IO.File.WriteAllText($"C:\\Test\\nextRun-{DateTime.UtcNow.ToString("dd-MM-yy-HH-mm")}.txt", $"{new TimeSpan(new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 0, 5, 0, 0, new TimeSpan(0, 0, 0)).Ticks - DateTimeOffset.UtcNow.Ticks).TotalMilliseconds}");

                this.BatchUploaderTimer.Interval = new TimeSpan(new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 0, 5, 0, 0, new TimeSpan(0, 0, 0)).Ticks - DateTimeOffset.UtcNow.Ticks).TotalMilliseconds;//OneDayMiliseconds;
                this.BatchUploaderTimer.Enabled = true;
            }
            else
            {
                // System.IO.File.WriteAllText($"C:\\Test\\nextRun-{DateTime.UtcNow.ToString("dd-MM-yy-HH-mm")}.txt", $"{new TimeSpan(new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 13, 0, 0, 0, new TimeSpan(0, 0, 0)).Ticks - DateTimeOffset.UtcNow.Ticks).TotalMilliseconds}");

                this.BatchUploaderKillTimer.Interval = new TimeSpan(new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 4, 0, 0, 0, new TimeSpan(0, 0, 0)).Ticks - DateTimeOffset.UtcNow.Ticks).TotalMilliseconds;
                this.BatchUploaderKillTimer.Enabled = true;

                this.BatchUploaderTimer.Interval = new TimeSpan(new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 13, 0, 0, 0, new TimeSpan(0, 0, 0)).Ticks - DateTimeOffset.UtcNow.Ticks).TotalMilliseconds;//OneDayMiliseconds;
                this.BatchUploaderTimer.Enabled = true;
            }
        }

        private void KillBatchUploader(Object source, ElapsedEventArgs e)
        {
            this.KillUploaderProcess();
        }

        private void KillUploaderProcess()
        {
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("RecorderBlobUploader"))
                {
                    process.Kill();
                }
            }
            catch
            {
            }

            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("ffmpeg"))
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Stop the service.
        /// </summary>
        public void Stop()
        {
            lock (this.syncLock)
            {
                if (!this.started)
                {
                    throw new InvalidOperationException("The service is already stopped.");
                }

                this.started = false;

                this.callHttpServer.Dispose();
                BotService.Instance.Dispose();
                this.BatchUploaderTimer.Stop();
                this.BatchUploaderTimer.Dispose();
            }
        }
    }
}