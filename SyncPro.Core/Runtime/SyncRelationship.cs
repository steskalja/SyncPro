﻿namespace SyncPro.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading;
    using System.Threading.Tasks;

    using SyncPro.Adapters;
    using SyncPro.Certificates;
    using SyncPro.Configuration;
    using SyncPro.Data;
    using SyncPro.Tracing;

    /// <summary>
    /// Represents that state of a <see cref="SyncRelationship"/>
    /// </summary>
    public enum SyncRelationshipState
    {
        /// <summary>
        /// The state is unknown or undefined.
        /// </summary>
        Undefined,

        /// <summary>
        /// The initial state of the relationship.
        /// </summary>
        NotInitialized,

        /// <summary>
        /// The relationship is being loaded/initialized.
        /// </summary>
        Initializing,

        /// <summary>
        /// The relationship is loaded and no operations are active.
        /// </summary>
        Idle,

        /// <summary>
        /// An operation is active (sync, analyze, etc.)
        /// </summary>
        Running,

        /// <summary>
        /// A fatal error has occurred in the relationship.
        /// </summary>
        Error
    }

    /// <summary>
    /// A <see cref="SyncRelationship"/> represents a set of two or more adapters whose files and folders
    /// are synchronized.
    /// </summary>
    public class SyncRelationship : NotifyPropertyChangedSlim
    {
        private readonly SemaphoreSlim saveLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// The configuration settings for this relationship
        /// </summary>
        public RelationshipConfiguration Configuration { get; }

        public List<AdapterBase> Adapters { get; }

        public string RelationshipRootPath => 
            Path.Combine(Global.AppDataRoot, this.Configuration.RelationshipId.ToString("N"));

        public SyncDatabase GetDatabase()
        {
            SyncDatabase db = new SyncDatabase(this.Configuration.RelationshipId);
            db.Configuration.ProxyCreationEnabled = false;

            return db;
        }

        public Task<SyncDatabase> GetDatabaseAsync()
        {
            return Task.FromResult(this.GetDatabase());
        }

        /// <summary>
        /// Create a new sync relationship
        /// </summary>
        private SyncRelationship(RelationshipConfiguration configuration)
        {
            this.Adapters = new List<AdapterBase>();
            this.Configuration = configuration;

            // Load parameters from config
            this.Description = configuration.Description;
            this.Name = configuration.Name;
            this.SyncScope = configuration.Scope;
            this.SyncAttributes = configuration.SyncAttributes;
            this.SendSyncJobReports = configuration.SendSyncReports;

            this.TriggerType = configuration.TriggerConfiguration.TriggerType;
            this.TriggerScheduleInterval = configuration.TriggerConfiguration.ScheduleInterval;

            this.TriggerHourlyInterval = this.Configuration.TriggerConfiguration.HourlyIntervalValue;
            this.TriggerHourlyMinutesPastSyncTime = this.Configuration.TriggerConfiguration.HourlyMinutesPastSyncTime;

            this.TriggerDailyIntervalValue = this.Configuration.TriggerConfiguration.DailyIntervalValue;
            this.TriggerDailyStartTime = this.Configuration.TriggerConfiguration.DailyStartTime;

            this.TriggerWeeklyIntervalValue = this.Configuration.TriggerConfiguration.WeeklyIntervalValue;
            this.TriggerWeeklyStartTime = this.Configuration.TriggerConfiguration.WeeklyStartTime;
            this.TriggerWeeklyDays = this.Configuration.TriggerConfiguration.WeeklyDays;

            this.IsThrottlingEnabled = configuration.ThrottlingConfiguration.IsEnabled;
            this.ThrottlingValue = configuration.ThrottlingConfiguration.Value;
            this.ThrottlingScaleFactor = configuration.ThrottlingConfiguration.ScaleFactor;

            this.EncryptionMode = configuration.EncryptionConfiguration.Mode;
            this.EncryptionCertificateThumbprint = configuration.EncryptionConfiguration.CertificateThumbprint;

            this.State = SyncRelationshipState.NotInitialized;
        }

        public async Task SaveAsync()
        {
            if (this.Adapters.Count < 2)
            {
                throw new Exception("Cannot create relationship database with fewer than 2 adapters");
            }

            await this.saveLock.WaitAsync();

            try
            {
                if (!Directory.Exists(this.RelationshipRootPath))
                {
                    Directory.CreateDirectory(this.RelationshipRootPath);
                }

                // Set properties in the configuration based on values in the model
                this.Configuration.Description = this.Description;
                this.Configuration.Name = this.Name;
                this.Configuration.Scope = this.SyncScope;
                this.Configuration.SyncAttributes = this.SyncAttributes;
                this.Configuration.SendSyncReports = this.SendSyncJobReports;

                this.Configuration.ThrottlingConfiguration.IsEnabled = this.IsThrottlingEnabled;
                this.Configuration.ThrottlingConfiguration.Value = this.ThrottlingValue;
                this.Configuration.ThrottlingConfiguration.ScaleFactor = this.ThrottlingScaleFactor;

                this.Configuration.TriggerConfiguration.TriggerType = this.TriggerType;
                this.Configuration.TriggerConfiguration.ScheduleInterval = this.TriggerScheduleInterval;

                this.Configuration.TriggerConfiguration.HourlyIntervalValue = this.TriggerHourlyInterval;
                this.Configuration.TriggerConfiguration.HourlyMinutesPastSyncTime = this.TriggerHourlyMinutesPastSyncTime;

                this.Configuration.TriggerConfiguration.DailyIntervalValue = this.TriggerDailyIntervalValue;
                this.Configuration.TriggerConfiguration.DailyStartTime = this.TriggerDailyStartTime;

                this.Configuration.TriggerConfiguration.WeeklyIntervalValue = this.TriggerWeeklyIntervalValue;
                this.Configuration.TriggerConfiguration.WeeklyStartTime = this.TriggerWeeklyStartTime;

                // Set the encryption mode for adapters that may need it. Other encryption configuration is set below.
                this.Configuration.EncryptionConfiguration.Mode = this.EncryptionMode;

                // If the relaionship contains adapters that arent in the configuration, add them
                foreach (AdapterConfiguration adapterConfig in this.Adapters.Select(a => a.Configuration))
                {
                    if (!this.Configuration.Adapters.Contains(adapterConfig))
                    {
                        this.Configuration.Adapters.Add(adapterConfig);
                    }
                }

                // Assign IDs to the adapters. Start my determining the highest ID currently in use, then assigning
                // incremented IDs from there.
                int highestId = this.Adapters.Select(a => a.Configuration).Max(c => c.Id);
                foreach (AdapterBase adapter in this.Adapters.Where(a => !a.Configuration.IsCreated))
                {
                    // The adapter config hasn't been created, so set the Id
                    highestId++;
                    adapter.Configuration.Id = highestId;
                }

                // Now that the adapters have IDs, set the IDs in the relationship's configuration. This is only 
                // needed when the SourceAdapter and DestinationAdapter properties have been set (when the 
                // relationship is being created for the first time).
                if (this.SourceAdapter != null && this.DestinationAdapter != null)
                {
                    this.Configuration.SourceAdapterId = this.SourceAdapter.Configuration.Id;
                    this.Configuration.DestinationAdapterId = this.DestinationAdapter.Configuration.Id;
                }

                // Set the IsOriginator property according to Scope configured for the relationship. If the scope
                // is set to bidirectional, both adapters are originators (and can produce changes). Otherwise, 
                // only the source adapter is an originator.
                this.Configuration.Adapters.First(a => a.Id == this.Configuration.SourceAdapterId).IsOriginator = true;
                this.Configuration.Adapters.First(a => a.Id == this.Configuration.DestinationAdapterId).IsOriginator =
                    this.Configuration.Scope == SyncScopeType.Bidirectional;

                SyncDatabase db = await this.GetDatabaseAsync().ConfigureAwait(false);
                using (db)
                {
                    foreach (AdapterBase adapter in this.Adapters.Where(a => !a.Configuration.IsCreated))
                    {
                        // Originating adapters that have not been created need to have the root entry created
                        if (adapter.Configuration.IsOriginator)
                        {
                            // Create the root sync entry using the adapter
                            SyncEntry rootSyncEntry = await adapter.CreateRootEntry().ConfigureAwait(false);

                            // Add the sync entry and adapter entries to the db.
                            db.Entries.Add(rootSyncEntry);
                            db.AdapterEntries.AddRange(rootSyncEntry.AdapterEntries);

                            // Save the current changes to the DB. This will set the ID for the root sync entry, which
                            // we will want to persist in the config for this adapter.
                            await db.SaveChangesAsync().ConfigureAwait(false);

                            adapter.Configuration.RootIndexEntryId = rootSyncEntry.Id;
                        }

                        adapter.Configuration.IsCreated = true;
                    }
                }

                // Call each adapter to save it's own configuration. This allows different adapter types of save their 
                // own configuration as needed.
                foreach (AdapterBase adapterBase in this.Adapters)
                {
                    adapterBase.SaveConfiguration();
                }

                // Check if we are creating this relationship for the first time
                if (this.Configuration.InitiallyCreatedUtc == DateTime.MinValue)
                {
                    // Create the encryption certificate if needed
                    if (this.EncryptionMode == EncryptionMode.Encrypt && this.EncryptionCreateCertificate)
                    {
                        string subjectName = "SyncProEncryption " + this.Configuration.RelationshipId.ToString("D").ToLowerInvariant();

                        X509Certificate2 encryptionCert = CertificateHelper.CreateSelfSignedCertificate(subjectName);

                        this.EncryptionCertificateThumbprint = encryptionCert.Thumbprint;
                        this.Configuration.EncryptionConfiguration.CertificateThumbprint = encryptionCert.Thumbprint;
                    }

                    this.Configuration.InitiallyCreatedUtc = DateTime.UtcNow;
                }

                // Finally save the configuration for the relationship itself
                this.Configuration.Save(this.RelationshipRootPath);

                Logger.RelationshipSaved(
                    new Dictionary<string, object>()
                    {
                        { "RelationshipId", this.Configuration.RelationshipId },
                        { "Name", this.Configuration.Name },
                    });
            }
            finally
            {
                this.saveLock.Release();
            }
        }

        public static SyncRelationship Create()
        {
            var config = new RelationshipConfiguration
            {
                RelationshipId = Guid.NewGuid()
            };

            return new SyncRelationship(config);
        }

        public static SyncRelationship Load(Guid relationshipId)
        {
            string relationshipDir = Path.Combine(Global.AppDataRoot, relationshipId.ToString("N"));
            RelationshipConfiguration config = RelationshipConfiguration.Load(relationshipDir);

            Logger.RelationshipLoaded(
                config.RelationshipId,
                new Dictionary<string,object>()
                {
                    { "Name", config.Name },
                    { "RelationshipId", config.RelationshipId },
                    { "InitiallyCreatedUtc", config.InitiallyCreatedUtc },
                    { "Scope", config.Scope }
                });

            SyncRelationship relationship = new SyncRelationship(config);

            // Get the adapters from the configuration for this relationship
            foreach (AdapterConfiguration adapterConfig in config.Adapters)
            {
                // Get the adapter registration information for this type of adapter
                AdapterRegistration registration = 
                    AdapterRegistry.GetRegistrationByTypeId(adapterConfig.AdapterTypeId);

                if (registration == null)
                {
                    throw new Exception("No adapter registration found with TypeId " + adapterConfig.AdapterTypeId);
                }

                // Create the adapter object based on its config from the database
                AdapterBase adapter = (AdapterBase)Activator.CreateInstance(
                    registration.AdapterType,
                    relationship,
                    adapterConfig);

                relationship.Adapters.Add(adapter);

                // Load adapter-specific configuration settings
                adapter.LoadConfiguration();
            }

            return relationship;
        }

        #region Common Properties

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string name;

        /// <summary>
        /// The user-provided display name of the relationship
        /// </summary>
        public string Name
        {
            get { return this.name; }
            set { this.SetProperty(ref this.name, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string description;

        /// <summary>
        /// The user-provided description of the sync relationship
        /// </summary>
        public string Description
        {
            get { return this.description; }
            set { this.SetProperty(ref this.description, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private SyncScopeType syncScope;

        public SyncScopeType SyncScope
        {
            get { return this.syncScope; }
            set { this.SetProperty(ref this.syncScope, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool syncAttributes;

        public bool SyncAttributes
        {
            get { return this.syncAttributes; }
            set { this.SetProperty(ref this.syncAttributes, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private AdapterBase sourceAdapter;

        public AdapterBase SourceAdapter
        {
            get { return this.sourceAdapter; }
            set { this.SetProperty(ref this.sourceAdapter, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private AdapterBase destinationAdapter;

        public AdapterBase DestinationAdapter
        {
            get { return this.destinationAdapter; }
            set { this.SetProperty(ref this.destinationAdapter, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool sendSyncJobReports;

        public bool SendSyncJobReports
        {
            get { return this.sendSyncJobReports; }
            set { this.SetProperty(ref this.sendSyncJobReports, value); }
        }

        #endregion

        #region Throttling Properties

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private bool isThrottlingEnabled;

        public bool IsThrottlingEnabled
        {
            get { return this.isThrottlingEnabled; }
            set { this.SetProperty(ref this.isThrottlingEnabled, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private int throttlingValue;

        public int ThrottlingValue
        {
            get { return this.throttlingValue; }
            set { this.SetProperty(ref this.throttlingValue, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private int throttlingScaleFactor;

        public int ThrottlingScaleFactor
        {
            get { return this.throttlingScaleFactor; }
            set { this.SetProperty(ref this.throttlingScaleFactor, value); }
        }

        #endregion

        #region Triggering Properties

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private SyncTriggerType triggerType;

        public SyncTriggerType TriggerType
        {
            get { return this.triggerType; }
            set { this.SetProperty(ref this.triggerType, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private TriggerScheduleInterval triggerScheduleInterval;

        public TriggerScheduleInterval TriggerScheduleInterval
        {
            get { return this.triggerScheduleInterval; }
            set { this.SetProperty(ref this.triggerScheduleInterval, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private int triggerHourlyInterval;

        public int TriggerHourlyInterval
        {
            get { return this.triggerHourlyInterval; }
            set { this.SetProperty(ref this.triggerHourlyInterval, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private int triggerHourlyMinutesPastSyncTime;

        public int TriggerHourlyMinutesPastSyncTime
        {
            get { return this.triggerHourlyMinutesPastSyncTime; }
            set { this.SetProperty(ref this.triggerHourlyMinutesPastSyncTime, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private int triggerDailyIntervalValue;

        public int TriggerDailyIntervalValue
        {
            get { return this.triggerDailyIntervalValue; }
            set { this.SetProperty(ref this.triggerDailyIntervalValue, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private TimeSpan triggerDailyStartTime;

        public TimeSpan TriggerDailyStartTime
        {
            get { return this.triggerDailyStartTime; }
            set { this.SetProperty(ref this.triggerDailyStartTime, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private int triggerWeeklyIntervalValue;

        public int TriggerWeeklyIntervalValue
        {
            get { return this.triggerWeeklyIntervalValue; }
            set { this.SetProperty(ref this.triggerWeeklyIntervalValue, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private TimeSpan triggerWeeklyStartTime;

        public TimeSpan TriggerWeeklyStartTime
        {
            get { return this.triggerWeeklyStartTime; }
            set { this.SetProperty(ref this.triggerWeeklyStartTime, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private WeeklyDays triggerWeeklyDays;

        public WeeklyDays TriggerWeeklyDays
        {
            get { return this.triggerWeeklyDays; }
            set { this.SetProperty(ref this.triggerWeeklyDays, value); }
        }

        #endregion

        #region Encryption Properties

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private EncryptionMode encryptionMode;

        public EncryptionMode EncryptionMode
        {
            get { return this.encryptionMode; }
            set { this.SetProperty(ref this.encryptionMode, value); }
        }

        /// <summary>
        /// Indicates whether the certificate should be created when the relationship is saved
        /// for the first time.
        /// </summary>
        public bool EncryptionCreateCertificate { get; set; }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string encryptionCertificateThumbprint;

        public string EncryptionCertificateThumbprint
        {
            get { return this.encryptionCertificateThumbprint; }
            set { this.SetProperty(ref this.encryptionCertificateThumbprint, value); }
        }

        #endregion

        public async Task InitializeAsync()
        {
            await Task.Run(() => { this.Initialize(); })
                .ContinueWith(this.InitializeComplete)
                .ConfigureAwait(false);
        }

        private void Initialize()
        {
            this.State = SyncRelationshipState.Initializing;

            Logger.InitializeRelationshipStart(this.Name, this.Configuration.RelationshipId);

            List<Task> tasks = new List<Task>(this.Adapters.Count);
            foreach (AdapterBase adapter in this.Adapters)
            {
                Logger.Debug("SyncRelationship: Calling adapter.Initialize for adapter {0}", adapter.Configuration.Id);

                tasks.Add(adapter.InitializeAsync());
            }

            // This call will throw an AggregateException if any of the tasks threw an exception during initialization
            Task.WaitAll(tasks.ToArray());
        }

        private void InitializeComplete(Task initializeTask)
        {
            Logger.InitializeRelationshipStop(this.Name, this.Configuration.RelationshipId);

            if (initializeTask.IsFaulted)
            {
                Logger.Error("Relationship initialization finished in a faulted state.");

                if (initializeTask.Exception != null)
                {
                    foreach (Exception innerException in initializeTask.Exception.InnerExceptions)
                    {
                        Logger.Error(innerException.ToString());
                    }
                }

                this.State = SyncRelationshipState.Error;
                return;
            }

            if (this.Adapters.Any(a => a.IsFaulted))
            {
                Logger.Error("One or more adapters failed to initialize");
                this.State = SyncRelationshipState.Error;
                return;
            }

            // Initialization completed successfully
            this.State = SyncRelationshipState.Idle;

            this.DisableSyncScheduler();

            this.EnableSyncScheduler();
        }

        private void DisableSyncScheduler()
        {
            // Disable scheduler for continuous syncing
            foreach (AdapterBase adapterBase in this.Adapters.Where(a => a.Configuration.IsOriginator))
            {
                if (adapterBase.SupportsChangeNotification())
                {
                    IChangeNotification changeNotificationAdapter = (IChangeNotification)adapterBase;
                    changeNotificationAdapter.EnableChangeNotification(false);
                    changeNotificationAdapter.ItemChanged -= this.SyncSchedulerHandleAdapterItemChangeNotification;
                }
            }

            // Disable scheduler for scheduled syncing
            if (this.syncSchedulerTask != null && !this.syncSchedulerTask.IsCompleted)
            {
                this.syncSchedulerCancellationTokenSource.Cancel();
                this.syncSchedulerTask.Wait();
            }
        }

        private void EnableSyncScheduler()
        {
            switch (this.Configuration.TriggerConfiguration.TriggerType)
            {
                case SyncTriggerType.Continuous:
                    foreach (AdapterBase adapterBase in this.Adapters.Where(a => a.Configuration.IsOriginator))
                    {
                        if (adapterBase.SupportsChangeNotification())
                        {
                            IChangeNotification changeNotificationAdapter = (IChangeNotification) adapterBase;
                            changeNotificationAdapter.ItemChanged += this.SyncSchedulerHandleAdapterItemChangeNotification;
                            changeNotificationAdapter.EnableChangeNotification(true);
                        }
                    }
                    break;
                case SyncTriggerType.Scheduled:
                    this.syncSchedulerCancellationTokenSource = new CancellationTokenSource();
                    this.syncSchedulerTask = Task.Run(
                        this.SyncSchedulerScheduledMainThread,
                        this.syncSchedulerCancellationTokenSource.Token);
                    break;
                case SyncTriggerType.Manual:
                    // Manual sync trigger does not require any configuration.
                    break;
                case SyncTriggerType.DeviceInsertion:
                    // TODO: This may not actually be required - we may simply register a callback somewhere.
                    throw new NotImplementedException();
                default:
                    Logger.Warning(
                        "The triggering type {0} for relationship '{1}' ({2}) is invalid.",
                        this.Configuration.TriggerConfiguration.TriggerType,
                        this.Name,
                        this.Configuration.RelationshipId);
                    break;
            }
        }

        private async Task SyncSchedulerScheduledMainThread()
        {
            DateTime nextSyncTime = this.GetNextScheduledTriggerTime();

            // Briefly wait when starting the scheduler
            await Task.Delay(1000).ConfigureAwait(false);

            while (!this.syncSchedulerCancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (nextSyncTime < DateTime.Now)
                    {
                        if (this.State == SyncRelationshipState.Idle)
                        {
                            this.BeginSyncJob(
                                SyncTriggerType.Scheduled,
                                null);

                            nextSyncTime = this.GetNextScheduledTriggerTime();
                        }
                        else
                        {
                            Logger.Debug(
                                "Deferring scheduled sync job start. Relationship state is {0}",
                                this.State);
                        }
                    }

                    // 1 minute wait between checking the schedule
                    await Task.Delay(
                            SyncRelationship.SyncSchedulerDelay,
                            this.syncSchedulerCancellationTokenSource.Token)
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Suppress task cancellation exception
                }
            }
        }

        private void SyncSchedulerHandleAdapterItemChangeNotification(object sender, ItemsChangedEventArgs e)
        {
            if (this.ActiveJob != null)
            {
                return;
            }

            // TODO: Use only the files listed from the event args
            this.BeginSyncJob(SyncTriggerType.Continuous, null);
        }

        public DateTime GetNextScheduledTriggerTime()
        {
            // First get the DateTime of midnight of the current day
            DateTime nextSyncTime = DateTime.Now.Date;

            if (this.TriggerScheduleInterval == TriggerScheduleInterval.Hourly)
            {
                // Add the minute offset
                nextSyncTime = nextSyncTime.AddMinutes(this.TriggerHourlyMinutesPastSyncTime);

                while (true)
                {
                    nextSyncTime = nextSyncTime.AddHours(this.TriggerHourlyInterval);

                    if (nextSyncTime > DateTime.Now)
                    {
                        return nextSyncTime;
                    }
                }
            }

            if (this.TriggerScheduleInterval == TriggerScheduleInterval.Daily)
            {
                nextSyncTime = nextSyncTime.Add(TriggerDailyStartTime);

                while (true)
                {
                    nextSyncTime = nextSyncTime.AddDays(this.TriggerDailyIntervalValue);

                    if (nextSyncTime > DateTime.Now)
                    {
                        return nextSyncTime;
                    }
                }
            }

            if (this.TriggerScheduleInterval == TriggerScheduleInterval.Weekly)
            {
                if (this.TriggerWeeklyDays == WeeklyDays.None)
                {
                    this.TriggerWeeklyDays = WeeklyDays.All;
                }

                nextSyncTime = nextSyncTime.Add(TriggerWeeklyStartTime);

                while (true)
                {
                    nextSyncTime = nextSyncTime.AddDays(this.TriggerWeeklyIntervalValue);

                    if (!IsScheduledDayOfWeek(nextSyncTime, this.TriggerWeeklyDays))
                    {
                        continue;
                    }

                    if (nextSyncTime > DateTime.Now)
                    {
                        return nextSyncTime;
                    }
                }
            }

            throw new NotImplementedException("TriggerInterval=" + this.TriggerHourlyInterval);
        }

        private static bool IsScheduledDayOfWeek(DateTime nextSyncTime, WeeklyDays weeklyDays)
        {
            if (nextSyncTime.DayOfWeek == DayOfWeek.Sunday &&
                (weeklyDays & WeeklyDays.Sunday) != 0)
            {
                return true;
            }

            if (nextSyncTime.DayOfWeek == DayOfWeek.Monday &&
                (weeklyDays & WeeklyDays.Monday) != 0)
            {
                return true;
            }

            if (nextSyncTime.DayOfWeek == DayOfWeek.Tuesday &&
                (weeklyDays & WeeklyDays.Tuesday) != 0)
            {
                return true;
            }

            if (nextSyncTime.DayOfWeek == DayOfWeek.Wednesday &&
                (weeklyDays & WeeklyDays.Wednesday) != 0)
            {
                return true;
            }

            if (nextSyncTime.DayOfWeek == DayOfWeek.Thursday &&
                (weeklyDays & WeeklyDays.Thursday) != 0)
            {
                return true;
            }

            if (nextSyncTime.DayOfWeek == DayOfWeek.Friday &&
                (weeklyDays & WeeklyDays.Friday) != 0)
            {
                return true;
            }

            if (nextSyncTime.DayOfWeek == DayOfWeek.Saturday &&
                (weeklyDays & WeeklyDays.Saturday) != 0)
            {
                return true;
            }

            return false;
        }

        private static readonly TimeSpan SyncSchedulerDelay = TimeSpan.FromMinutes(1);
        private Task syncSchedulerTask;
        private CancellationTokenSource syncSchedulerCancellationTokenSource;

        private JobBase activeJob;

        public JobBase ActiveJob
        {
            get { return this.activeJob; }
            internal set { this.SetProperty(ref this.activeJob, value); }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private AnalyzeJob activeAnalyzeJob;

        public AnalyzeJob ActiveAnalyzeJob
        {
            get { return this.activeAnalyzeJob; }
            set { this.SetProperty(ref this.activeAnalyzeJob, value); }
        }

        public event EventHandler<JobStartedEventArgs> JobStarted;
        public event EventHandler<JobFinishedEventArgs> JobFinished;

        private SyncRelationshipState state;

        public SyncRelationshipState State
        {
            get { return this.state; }
            set { this.SetProperty(ref this.state, value); }
        }

        private string errorMessage;

        public string ErrorMessage
        {
            get { return this.errorMessage; }
            set { this.SetProperty(ref this.errorMessage, value); }
        }

        public AnalyzeJob BeginAnalyzeJob(bool startJob)
        {
            if (this.ActiveJob != null)
            {
                throw new InvalidOperationException("An ActiveJob is already present.");
            }

            AnalyzeJob newJob = new AnalyzeJob(this);

            newJob.Started += this.JobStarted;
            newJob.Finished += this.JobFinished;

            if (startJob)
            {
                newJob.Start();
            }

            return newJob;
        }

        public JobBase BeginSyncJob(
            SyncTriggerType syncTriggerType, 
            AnalyzeRelationshipResult previousResult)
        {
            if (this.ActiveJob != null)
            {
                throw new InvalidOperationException("An ActiveJob is already present.");
            }

            if (previousResult == null)
            {
                AnalyzeJob newAnalyzeJob = new AnalyzeJob(this);

                newAnalyzeJob.ContinuationJob = new SyncJob(this, newAnalyzeJob.AnalyzeResult)
                {
                    TriggerType = syncTriggerType
                };

                newAnalyzeJob.Started += this.JobStarted;
                newAnalyzeJob.Finished += this.JobFinished;

                newAnalyzeJob.ContinuationJob.Started += this.JobStarted;
                newAnalyzeJob.ContinuationJob.Finished += this.JobFinished;

                newAnalyzeJob.Start();

                return newAnalyzeJob;
            }

            SyncJob newJob = new SyncJob(this, previousResult)
            {
                TriggerType = syncTriggerType
            };

            newJob.Started += this.JobStarted;
            newJob.Finished += this.JobFinished;

            newJob.Start();

            return newJob;
        }

        public RestoreJob BeginRestoreJob(List<SyncEntry> syncEntries, string restorePath)
        {
            if (this.ActiveJob != null)
            {
                throw new InvalidOperationException("An ActiveJob is already present.");
            }

            RestoreJob restoreJob = new RestoreJob(
                this,
                syncEntries,
                restorePath);

            restoreJob.Started += this.JobStarted;
            restoreJob.Finished += this.JobFinished;

            restoreJob.Start();

            return restoreJob;
        }

        public IList<SyncJob> GetSyncJobHistory()
        {
            List<SyncJob> runs = new List<SyncJob>();
            using (var db = this.GetDatabase())
            {
                foreach (SyncHistoryData historyData in db.History)
                {
                    runs.Add(SyncJob.FromHistoryEntry(this, historyData));
                }
            }

            return runs;
        }

        public void Delete()
        {
            // https://stackoverflow.com/questions/5288996/database-in-use-error-with-entity-framework-4-code-first/5289296#5289296
            using (var db = this.GetDatabase())
            {
                db.Database.Delete();
            }

            Directory.Delete(this.RelationshipRootPath, true);
        }
    }

    public class JobStartedEventArgs : EventArgs
    {
        public JobBase Job { get; set; }

        public JobStartedEventArgs(JobBase job)
        {
            this.Job = job;
        }
    }

    public class JobFinishedEventArgs : EventArgs
    {
        public JobBase Job { get; set; }

        public JobFinishedEventArgs(JobBase job)
        {
            this.Job = job;
        }
    }

}