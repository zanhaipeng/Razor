using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.LanguageServices;
using Microsoft.VisualStudio.ProjectSystem.VS;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Threading.Tasks;

namespace Microsoft.VisualStudio.ProjectSystem.Razor
{
    [Export(ExportContractNames.Scopes.UnconfiguredProject, typeof(IProjectDynamicLoadComponent))]
    [AppliesTo("RazorDotNetCore")]
    internal class RazorProjectWatcher : OnceInitializedOnceDisposedAsync, IProjectDynamicLoadComponent
    {

        private readonly SemaphoreSlim _gate2 = new SemaphoreSlim(initialCount: 1);
        private readonly object _gate = new object();
        private readonly IUnconfiguredProjectCommonServices _commonServices;
        private readonly Lazy<IWorkspaceProjectContextFactory> _contextFactory;
        private readonly IProjectAsyncLoadDashboard _asyncLoadDashboard;
        private readonly ITaskScheduler _taskScheduler;
        private readonly List<AggregateWorkspaceProjectContext> _contexts = new List<AggregateWorkspaceProjectContext>();
        private readonly IProjectHostProvider _projectHostProvider;
        private readonly IActiveConfiguredProjectsProvider _activeConfiguredProjectsProvider;
        private readonly IUnconfiguredProjectHostObject _unconfiguredProjectHostObject;
        private readonly Dictionary<ConfiguredProject, IWorkspaceProjectContext> _configuredProjectContextsMap = new Dictionary<ConfiguredProject, IWorkspaceProjectContext>();
        private readonly Dictionary<ConfiguredProject, IConfiguredProjectHostObject> _configuredProjectHostObjectsMap = new Dictionary<ConfiguredProject, IConfiguredProjectHostObject>();

        private readonly LanguageServiceHandlerManager _languageServiceHandlerManager;

        private readonly List<IDisposable> _evaluationSubscriptionLinks;
        private readonly List<IDisposable> _designTimeBuildSubscriptionLinks;
        private readonly IProjectAsynchronousTasksService _tasksService;

        [ImportingConstructor]
        public RazorProjectWatcher(
            IUnconfiguredProjectCommonServices commonServices,
            Lazy<IWorkspaceProjectContextFactory> contextFactory,
            IProjectAsyncLoadDashboard asyncLoadDashboard,
            ITaskScheduler taskScheduler,
            [Import(ExportContractNames.Scopes.UnconfiguredProject)] IProjectAsynchronousTasksService tasksService,
            IProjectHostProvider projectHostProvider,
            IActiveConfiguredProjectsProvider activeConfiguredProjectsProvider,
            LanguageServiceHandlerManager languageServiceHandlerManager)
            : base(commonServices.ThreadingService.JoinableTaskContext)
        {
            Requires.NotNull(commonServices, nameof(commonServices));
            Requires.NotNull(contextFactory, nameof(contextFactory));
            Requires.NotNull(asyncLoadDashboard, nameof(asyncLoadDashboard));
            Requires.NotNull(taskScheduler, nameof(taskScheduler));
            Requires.NotNull(projectHostProvider, nameof(projectHostProvider));
            Requires.NotNull(activeConfiguredProjectsProvider, nameof(activeConfiguredProjectsProvider));

            _commonServices = commonServices;
            _contextFactory = contextFactory;
            _asyncLoadDashboard = asyncLoadDashboard;
            _taskScheduler = taskScheduler;
            _projectHostProvider = projectHostProvider;
            _activeConfiguredProjectsProvider = activeConfiguredProjectsProvider;
            _languageServiceHandlerManager = languageServiceHandlerManager;
            _tasksService = tasksService;

            _unconfiguredProjectHostObject = _projectHostProvider.UnconfiguredProjectHostObject;
            _evaluationSubscriptionLinks = new List<IDisposable>();
            _designTimeBuildSubscriptionLinks = new List<IDisposable>();
        }

        protected override Task DisposeCoreAsync(bool initialized)
        {
            _commonServices.Project.ProjectRenamed -= OnProjectRenamed;
            _unconfiguredProjectHostObject.Dispose();
            return Task.CompletedTask;
        }

        protected override Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            _commonServices.Project.ProjectRenamed += OnProjectRenamed;
            return Task.CompletedTask;
        }

        public Task LoadAsync()
        {
            GC.KeepAlive(_taskScheduler.RunAsync<AggregateWorkspaceProjectContext>(TaskSchedulerPriority.UIThreadBackgroundPriority, async () =>
            {
                return await CreateProjectContextAsync();
            }));

            return Task.CompletedTask;
        }

        public async Task UnloadAsync()
        {
            AggregateWorkspaceProjectContext[] contexts;
            lock (_gate)
            {
                contexts = _contexts.ToArray();
            }

            foreach (var context in contexts)
            {
                await ReleaseProjectContextAsync(context);
            }
        }

        public async Task<AggregateWorkspaceProjectContext> CreateProjectContextAsync()
        {
            await InitializeAsync();

            var context = await CreateProjectContextAsyncCore().ConfigureAwait(false);
            if (context == null)
            {
                return null;
            }

            lock (_gate)
            {
                // There's a race here, by the time we've created the project context,
                // the project could have been renamed, handle this.
                var projectData = GetProjectData();

                context.SetProjectFilePathAndDisplayName(projectData.FullPath, projectData.DisplayName);
                _contexts.Add(context);

                var watchedEvaluationRules = _languageServiceHandlerManager.GetWatchedRules(RuleHandlerType.Evaluation);
                var watchedDesignTimeBuildRules = _languageServiceHandlerManager.GetWatchedRules(RuleHandlerType.DesignTimeBuild);

                foreach (var configuredProject in context.InnerConfiguredProjects)
                {
                    _designTimeBuildSubscriptionLinks.Add(configuredProject.Services.ProjectSubscription.JointRuleSource.SourceBlock.LinkTo(
                        new ActionBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>>(e => OnProjectChangedCoreAsync(e, RuleHandlerType.DesignTimeBuild)),
                        ruleNames: watchedDesignTimeBuildRules, suppressVersionOnlyUpdates: true));

                    _evaluationSubscriptionLinks.Add(configuredProject.Services.ProjectSubscription.ProjectRuleSource.SourceBlock.LinkTo(
                        new ActionBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>>(e => OnProjectChangedCoreAsync(e, RuleHandlerType.Evaluation)),
                        ruleNames: watchedEvaluationRules, suppressVersionOnlyUpdates: true));
                }
            }

            return context;
        }

        public async Task ReleaseProjectContextAsync(AggregateWorkspaceProjectContext context)
        {
            Requires.NotNull(context, nameof(context));

            ImmutableHashSet<IWorkspaceProjectContext> usedProjectContexts;
            lock (_gate)
            {
                if (!_contexts.Remove(context))
                {
                    throw new ArgumentException("Specified context was not created by this instance, or has already been unregistered.");
                }

                // Update the maps storing configured project host objects and project contexts which are shared across created contexts.
                // We can remove the ones which are only used by the current context being released.
                RemoveUnusedConfiguredProjectsState_NoLock();

                usedProjectContexts = _configuredProjectContextsMap.Values.ToImmutableHashSet();
            }

            // TODO: https://github.com/dotnet/roslyn-project-system/issues/353
            await _commonServices.ThreadingService.SwitchToUIThread();

            // We don't want to dispose the inner workspace contexts that are still being used by other active aggregate contexts.
            Func<IWorkspaceProjectContext, bool> shouldDisposeInnerContext = c => !usedProjectContexts.Contains(c);

            context.Dispose(shouldDisposeInnerContext);
        }

        // Clears saved host objects and project contexts for unused configured projects.
        private void RemoveUnusedConfiguredProjectsState_NoLock()
        {
            if (_contexts.Count == 0)
            {
                // No active project contexts, clear all state.
                _configuredProjectContextsMap.Clear();
                _configuredProjectHostObjectsMap.Clear();
                return;
            }

            var unusedConfiguredProjects = new HashSet<ConfiguredProject>(_configuredProjectContextsMap.Keys);
            foreach (var context in _contexts)
            {
                foreach (var configuredProject in context.InnerConfiguredProjects)
                {
                    unusedConfiguredProjects.Remove(configuredProject);
                }
            }

            foreach (var configuredProject in unusedConfiguredProjects)
            {
                _configuredProjectContextsMap.Remove(configuredProject);
                _configuredProjectHostObjectsMap.Remove(configuredProject);
            }
        }

        private Task OnProjectRenamed(object sender, ProjectRenamedEventArgs args)
        {
            lock (_gate)
            {
                var projectData = GetProjectData();

                foreach (var context in _contexts)
                {
                    context.SetProjectFilePathAndDisplayName(projectData.FullPath, projectData.DisplayName);
                }
            }

            return Task.CompletedTask;
        }

        // Returns the name that is the handshake between Roslyn and the csproj/vbproj
        private async Task<string> GetLanguageServiceName()
        {
            ConfigurationGeneral properties = await _commonServices.ActiveConfiguredProjectProperties.GetConfigurationGeneralPropertiesAsync()
                                                                                                .ConfigureAwait(false);

            return (string)await properties.LanguageServiceName.GetValueAsync()
                                                               .ConfigureAwait(false);
        }

        private async Task<Guid> GetProjectGuidAsync()
        {
            ConfigurationGeneral properties = await _commonServices.ActiveConfiguredProjectProperties.GetConfigurationGeneralPropertiesAsync()
                                                                                                     .ConfigureAwait(false);
            Guid.TryParse((string)await properties.ProjectGuid.GetValueAsync().ConfigureAwait(false), out Guid guid);

            return guid;
        }

        private async Task<string> GetTargetPathAsync()
        {
            ConfigurationGeneral properties = await _commonServices.ActiveConfiguredProjectProperties.GetConfigurationGeneralPropertiesAsync()
                                                                                                     .ConfigureAwait(false);
            return (string)await properties.TargetPath.GetValueAsync()
                                                      .ConfigureAwait(false);
        }

        private ProjectData GetProjectData()
        {
            string filePath = _commonServices.Project.FullPath;

            return new ProjectData()
            {
                FullPath = filePath,
                DisplayName = Path.GetFileNameWithoutExtension(filePath)
            };
        }

        private bool TryGetConfiguredProjectState(ConfiguredProject configuredProject, out IWorkspaceProjectContext workspaceProjectContext, out IConfiguredProjectHostObject configuredProjectHostObject)
        {
            lock (_gate)
            {
                if (_configuredProjectContextsMap.TryGetValue(configuredProject, out workspaceProjectContext))
                {
                    configuredProjectHostObject = _configuredProjectHostObjectsMap[configuredProject];
                    return true;
                }
                else
                {
                    workspaceProjectContext = null;
                    configuredProjectHostObject = null;
                    return false;
                }
            }
        }

        private void AddConfiguredProjectState(ConfiguredProject configuredProject, IWorkspaceProjectContext workspaceProjectContext, IConfiguredProjectHostObject configuredProjectHostObject)
        {
            lock (_gate)
            {
                _configuredProjectContextsMap.Add(configuredProject, workspaceProjectContext);
                _configuredProjectHostObjectsMap.Add(configuredProject, configuredProjectHostObject);
            }
        }

        private async Task<AggregateWorkspaceProjectContext> CreateProjectContextAsyncCore()
        {
            string languageName = await GetLanguageServiceName().ConfigureAwait(false);
            if (string.IsNullOrEmpty(languageName))
                return null;

            Guid projectGuid = await GetProjectGuidAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(await GetTargetPathAsync().ConfigureAwait(false)))
            {
                return null;
            }

            // Don't initialize until the project has been loaded into the IDE and available in Solution Explorer
            await _asyncLoadDashboard.ProjectLoadedInHostWithCancellation(_commonServices.Project).ConfigureAwait(false);

            // TODO: https://github.com/dotnet/roslyn-project-system/issues/353
            return await _taskScheduler.RunAsync(TaskSchedulerPriority.UIThreadBackgroundPriority, async () =>
            {
                await _commonServices.ThreadingService.SwitchToUIThread();

                var projectData = GetProjectData();

                // Get the set of active configured projects ignoring target framework.
#pragma warning disable CS0618 // Type or member is obsolete
                var configuredProjectsMap = await _activeConfiguredProjectsProvider.GetActiveConfiguredProjectsMapAsync().ConfigureAwait(true);
#pragma warning restore CS0618 // Type or member is obsolete

                // Get the unconfigured project host object (shared host object).
                var configuredProjectsToRemove = new HashSet<ConfiguredProject>(_configuredProjectHostObjectsMap.Keys);
                var activeProjectConfiguration = _commonServices.ActiveConfiguredProject.ProjectConfiguration;

                var innerProjectContextsBuilder = ImmutableDictionary.CreateBuilder<string, IWorkspaceProjectContext>();
                string activeTargetFramework = string.Empty;
                IConfiguredProjectHostObject activeIntellisenseProjectHostObject = null;

                foreach (var kvp in configuredProjectsMap)
                {
                    var targetFramework = kvp.Key;
                    var configuredProject = kvp.Value;
                    if (!TryGetConfiguredProjectState(configuredProject, out IWorkspaceProjectContext workspaceProjectContext, out IConfiguredProjectHostObject configuredProjectHostObject))
                    {
                        // Get the target path for the configured project.
                        var projectProperties = configuredProject.Services.ExportProvider.GetExportedValue<ProjectProperties>();

                        var configurationGeneralProperties = await projectProperties.GetConfigurationGeneralPropertiesAsync().ConfigureAwait(true);
                        var originalTargetPath = (string)await configurationGeneralProperties.TargetPath.GetValueAsync().ConfigureAwait(true);

                        var targetPath = Path.Combine(Path.GetDirectoryName(originalTargetPath), Path.GetFileNameWithoutExtension(originalTargetPath) + ".Views" + Path.GetExtension(originalTargetPath));

                        var targetFrameworkMoniker = (string)await configurationGeneralProperties.TargetFrameworkMoniker.GetValueAsync().ConfigureAwait(true);
                        var displayName = GetDisplayName(configuredProject, projectData, targetFramework);

                        configuredProjectHostObject = _projectHostProvider.GetConfiguredProjectHostObject(_unconfiguredProjectHostObject, displayName, targetFrameworkMoniker);

                        // TODO: https://github.com/dotnet/roslyn-project-system/issues/353
                        await _commonServices.ThreadingService.SwitchToUIThread();
                        workspaceProjectContext = _contextFactory.Value.CreateProjectContext(languageName, displayName, projectData.FullPath, projectGuid, configuredProjectHostObject, targetPath);
                        workspaceProjectContext.AddMetadataReference(originalTargetPath, new MetadataReferenceProperties());

                        // By default, set "LastDesignTimeBuildSucceeded = false" to turn off diagnostics until first design time build succeeds for this project.
                        workspaceProjectContext.LastDesignTimeBuildSucceeded = false;

                        AddConfiguredProjectState(configuredProject, workspaceProjectContext, configuredProjectHostObject);
                    }

                    innerProjectContextsBuilder.Add(targetFramework, workspaceProjectContext);

                    if (activeIntellisenseProjectHostObject == null && configuredProject.ProjectConfiguration.Equals(activeProjectConfiguration))
                    {
                        activeIntellisenseProjectHostObject = configuredProjectHostObject;
                        activeTargetFramework = targetFramework;
                    }
                }

                _unconfiguredProjectHostObject.ActiveIntellisenseProjectHostObject = activeIntellisenseProjectHostObject;
                var newProjectContext = new AggregateWorkspaceProjectContext(innerProjectContextsBuilder.ToImmutable(), configuredProjectsMap, activeTargetFramework, _unconfiguredProjectHostObject);

                return newProjectContext;
            });
        }

        private async Task OnProjectChangedCoreAsync(IProjectVersionedValue<IProjectSubscriptionUpdate> e, RuleHandlerType handlerType)
        {
            if (IsDisposing || IsDisposed)
                return;

            await _tasksService.LoadedProjectAsync(async () =>
            {
                await HandleAsync(e, handlerType).ConfigureAwait(false);
            });

            // If "TargetFrameworks" property has changed, we need to refresh the project context and subscriptions.
            if (HasTargetFrameworksChanged(e))
            {
                //throw new InvalidOperationException("We'll do this for real some other time.");
            }
        }

        private async Task HandleAsync(IProjectVersionedValue<IProjectSubscriptionUpdate> update, RuleHandlerType handlerType)
        {
            // We need to process the update within a lock to ensure that we do not release this context during processing.
            // TODO: Enable concurrent execution of updates themeselves, i.e. two separate invocations of HandleAsync
            //       should be able to run concurrently.
            await ExecuteWithinLockAsync(async () =>
            {
                // TODO: https://github.com/dotnet/roslyn-project-system/issues/353
                await _commonServices.ThreadingService.SwitchToUIThread();

                // Get the inner workspace project context to update for this change.
                var projectContextToUpdate = _contexts[0].GetInnerProjectContext(update.Value.ProjectConfiguration, out bool isActiveContext);
                if (projectContextToUpdate == null)
                {
                    return;
                }

                _languageServiceHandlerManager.Handle(update, handlerType, projectContextToUpdate, isActiveContext);
            }).ConfigureAwait(false);
        }

        private Task<T> ExecuteWithinLockAsync<T>(Func<Task<T>> task)
        {
            return _gate2.ExecuteWithinLockAsync(JoinableCollection, JoinableFactory, task);
        }

        private Task ExecuteWithinLockAsync(Func<Task> task)
        {
            return _gate2.ExecuteWithinLockAsync(JoinableCollection, JoinableFactory, task);
        }

        private bool HasTargetFrameworksChanged(IProjectVersionedValue<IProjectSubscriptionUpdate> e)
        {
            return e.Value.ProjectChanges.TryGetValue(ConfigurationGeneral.SchemaName, out IProjectChangeDescription projectChange) &&
                 projectChange.Difference.ChangedProperties.Contains(ConfigurationGeneral.TargetFrameworksProperty);
        }

        private static string GetDisplayName(ConfiguredProject configuredProject, ProjectData projectData, string targetFramework)
        {
            // For cross targeting projects, we need to ensure that the display name is unique per every target framework.
            // This is needed for couple of reasons:
            //   (a) The display name is used in the editor project context combo box when opening source files that used by more than one inner projects.
            //   (b) Language service requires each active workspace project context in the current workspace to have a unique value for {ProjectFilePath, DisplayName}.
            var baseName = configuredProject.ProjectConfiguration.IsCrossTargeting() ?
                $"{projectData.DisplayName} ({targetFramework})" :
                projectData.DisplayName;

            return baseName + " - Razor";
        }

        private struct ProjectData
        {
            public string FullPath;
            public string DisplayName;
        }
    }
}
