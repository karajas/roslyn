﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Host;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem.Interop;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem.Legacy;
using Microsoft.VisualStudio.LanguageServices.ProjectSystem;
using Microsoft.VisualStudio.Shell.Interop;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem
{
    internal sealed partial class VisualStudioProjectTracker : ForegroundThreadAffinitizedObject, IDisposable, IVisualStudioHostProjectContainer
    {
        #region Readonly fields
        private static readonly ConditionalWeakTable<SolutionId, string> s_workingFolderPathMap = new ConditionalWeakTable<SolutionId, string>();

        private readonly IServiceProvider _serviceProvider;
        private readonly IVsSolution _vsSolution;
        private readonly IVsRunningDocumentTable4 _runningDocumentTable;
        private readonly object _gate = new object();
        #endregion

        #region Mutable fields accessed only from foreground thread - don't need locking for access (all accessing methods must have AssertIsForeground).
        private readonly List<WorkspaceHostState> _workspaceHosts;

        private readonly HostWorkspaceServices _workspaceServices;

        /// <summary>
        /// The list of projects loaded in this batch between <see cref="IVsSolutionLoadEvents.OnBeforeLoadProjectBatch" /> and
        /// <see cref="IVsSolutionLoadEvents.OnAfterLoadProjectBatch(bool)"/>.
        /// </summary>
        private readonly List<AbstractProject> _projectsLoadedThisBatch = new List<AbstractProject>();

        /// <summary>
        /// Set to true while the solution is in the process of closing. That is, between
        /// <see cref="IVsSolutionEvents.OnBeforeCloseSolution"/> and <see cref="IVsSolutionEvents.OnAfterCloseSolution"/>.
        /// </summary>
        private bool _solutionIsClosing = false;

        /// <summary>
        /// Set during <see cref="IVsSolutionEvents.OnBeforeCloseSolution"/>, so that <see cref="IVsSolutionEvents.OnAfterCloseSolution"/> knows
        /// whether or not to clean up deferred projects.
        /// </summary>
        private bool _deferredLoadWasEnabledForLastSolution = false;

        /// <summary>
        /// Set to true once the solution has already been completely loaded and all future changes
        /// should be pushed immediately to the workspace hosts. This may not actually result in changes
        /// being pushed to a particular host if <see cref="WorkspaceHostState.HostReadyForEvents"/> isn't true yet.
        /// </summary>
        private bool _solutionLoadComplete = false;
        #endregion

        #region Mutable fields accessed from foreground or background threads - need locking for access.
        /// <summary>
        /// This is a multi-map, only so we don't have any edge cases if people have two projects with
        /// the same output path. It makes state tracking notably easier.
        /// </summary>
        private readonly Dictionary<string, ImmutableArray<AbstractProject>> _projectsByBinPath = new Dictionary<string, ImmutableArray<AbstractProject>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<ProjectId, AbstractProject> _projectMap;
        private readonly Dictionary<string, ProjectId> _projectPathToIdMap;
        #endregion

        // Temporary for prototyping purposes
        private IVsOutputWindowPane _pane;

        /// <summary>
        /// Used to cancel our background solution parse if we get a solution close event from VS.
        /// </summary>
        private CancellationTokenSource _solutionParsingCancellationTokenSource = new CancellationTokenSource();

        /// <summary>
        /// Provided to not break CodeLens which has a dependency on this API until there is a
        /// public release which calls <see cref="ImmutableProjects"/>.  Once there is, we should
        /// change this back to returning <see cref="ImmutableArray{AbstractProject}"/>, and 
        /// Obsolete <see cref="ImmutableProjects"/> instead, and then remove that after a
        /// second public release.
        /// </summary>
        [Obsolete("Use '" + nameof(ImmutableProjects) + "' instead.", true)]
        internal IEnumerable<AbstractProject> Projects => ImmutableProjects;

        internal ImmutableArray<AbstractProject> ImmutableProjects
        {
            get
            {
                lock (_gate)
                {
                    return _projectMap.Values.ToImmutableArray();
                }
            }
        }

        IReadOnlyList<IVisualStudioHostProject> IVisualStudioHostProjectContainer.GetProjects() => this.ImmutableProjects;

        void IVisualStudioHostProjectContainer.NotifyNonDocumentOpenedForProject(IVisualStudioHostProject project)
        {
            AssertIsForeground();

            var abstractProject = (AbstractProject)project;
            StartPushingToWorkspaceAndNotifyOfOpenDocuments(SpecializedCollections.SingletonEnumerable(abstractProject));
        }

        public VisualStudioProjectTracker(IServiceProvider serviceProvider, HostWorkspaceServices workspaceServices)
            : base(assertIsForeground: true)
        {
            _projectMap = new Dictionary<ProjectId, AbstractProject>();
            _projectPathToIdMap = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);

            _serviceProvider = serviceProvider;
            _workspaceHosts = new List<WorkspaceHostState>(capacity: 1);
            _workspaceServices = workspaceServices;

            _vsSolution = (IVsSolution)serviceProvider.GetService(typeof(SVsSolution));
            _runningDocumentTable = (IVsRunningDocumentTable4)serviceProvider.GetService(typeof(SVsRunningDocumentTable));

            // It's possible that we're loading after the solution has already fully loaded, so see if we missed the event
            var shellMonitorSelection = (IVsMonitorSelection)serviceProvider.GetService(typeof(SVsShellMonitorSelection));
            if (ErrorHandler.Succeeded(shellMonitorSelection.GetCmdUIContextCookie(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_guid, out var fullyLoadedContextCookie)))
            {
                if (ErrorHandler.Succeeded(shellMonitorSelection.IsCmdUIContextActive(fullyLoadedContextCookie, out var fActive)) && fActive != 0)
                {
                    _solutionLoadComplete = true;
                }
            }
        }

        public void RegisterSolutionProperties(SolutionId solutionId)
        {
            AssertIsForeground();

            try
            {
                var solutionWorkingFolder = (IVsSolutionWorkingFolders)_vsSolution;
                solutionWorkingFolder.GetFolder(
                    (uint)__SolutionWorkingFolder.SlnWF_StatePersistence, Guid.Empty, fVersionSpecific: true, fEnsureCreated: true,
                    pfIsTemporary: out var temporary, pszBstrFullPath: out var workingFolderPath);

                if (!temporary && !string.IsNullOrWhiteSpace(workingFolderPath))
                {
                    s_workingFolderPathMap.Add(solutionId, workingFolderPath);
                }
            }
            catch
            {
                // don't crash just because solution having problem getting working folder information
            }
        }

        public void UpdateSolutionProperties(SolutionId solutionId)
        {
            AssertIsForeground();

            s_workingFolderPathMap.Remove(solutionId);

            RegisterSolutionProperties(solutionId);
        }

        public string GetWorkingFolderPath(Solution solution)
        {
            if (s_workingFolderPathMap.TryGetValue(solution.Id, out var workingFolderPath))
            {
                return workingFolderPath;
            }

            return null;
        }

        public void RegisterWorkspaceHost(IVisualStudioWorkspaceHost host)
        {
            this.AssertIsForeground();

            if (_workspaceHosts.Any(hostState => hostState.Host == host))
            {
                throw new ArgumentException("The workspace host is already registered.", nameof(host));
            }

            _workspaceHosts.Add(new WorkspaceHostState(this, host));
        }

        public void StartSendingEventsToWorkspaceHost(IVisualStudioWorkspaceHost host)
        {
            AssertIsForeground();

            var hostData = _workspaceHosts.FirstOrDefault(s => s.Host == host);
            if (hostData == null)
            {
                throw new ArgumentException("The workspace host not registered", nameof(host));
            }

            // This method is idempotent.
            if (hostData.HostReadyForEvents)
            {
                return;
            }

            hostData.HostReadyForEvents = true;

            // If any of the projects are already interactive, then we better catch up the host.
            var interactiveProjects = this.ImmutableProjects.Where(p => p.PushingChangesToWorkspaceHosts);

            if (interactiveProjects.Any())
            {
                hostData.StartPushingToWorkspaceAndNotifyOfOpenDocuments(interactiveProjects);
            }
        }

        public void InitializeProviders(DocumentProvider documentProvider, VisualStudioMetadataReferenceManager metadataReferenceProvider, VisualStudioRuleSetManager ruleSetFileProvider)
        {
            AssertIsForeground();

            Contract.ThrowIfFalse(DocumentProvider == null);
            Contract.ThrowIfFalse(MetadataReferenceProvider == null);
            Contract.ThrowIfFalse(RuleSetFileProvider == null);

            DocumentProvider = documentProvider;
            MetadataReferenceProvider = metadataReferenceProvider;
            RuleSetFileProvider = ruleSetFileProvider;
        }

        public DocumentProvider DocumentProvider { get; private set; }
        public VisualStudioMetadataReferenceManager MetadataReferenceProvider { get; private set; }
        public VisualStudioRuleSetManager RuleSetFileProvider { get; private set; }

        public void Dispose()
        {
            if (this.RuleSetFileProvider != null)
            {
                this.RuleSetFileProvider.Dispose();
            }
        }

        internal AbstractProject GetProject(ProjectId id)
        {
            lock (_gate)
            {
                _projectMap.TryGetValue(id, out var project);
                return project;
            }
        }

        internal bool ContainsProject(AbstractProject project)
        {
            lock (_gate)
            {
                return _projectMap.ContainsKey(project.Id);
            }
        }

        /// <summary>
        /// Add a project to the workspace.
        /// </summary>
        /// <remarks>This method must be called on the foreground thread.</remarks>
        internal void AddProject(AbstractProject project)
        {
            AssertIsForeground();

            lock (_gate)
            {
                _projectMap.Add(project.Id, project);
            }

            // UpdateProjectBinPath is defensively executed on the foreground thread as it calls back into referencing projects to perform metadata to P2P reference conversions.
            UpdateProjectBinPath(project, null, project.BinOutputPath);

            if (_solutionLoadComplete)
            {
                StartPushingToWorkspaceAndNotifyOfOpenDocuments(SpecializedCollections.SingletonEnumerable(project));
            }
            else
            {
                _projectsLoadedThisBatch.Add(project);
            }
        }

        /// <summary>
        /// Starts pushing events from the given projects to the workspace hosts and notifies about open documents.
        /// </summary>
        /// <remarks>This method must be called on the foreground thread.</remarks>
        internal void StartPushingToWorkspaceAndNotifyOfOpenDocuments(IEnumerable<AbstractProject> projects)
        {
            AssertIsForeground();

            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                foreach (var hostState in _workspaceHosts)
                {
                    hostState.StartPushingToWorkspaceAndNotifyOfOpenDocuments(projects);
                }
            }
        }

        /// <summary>
        /// Remove a project from the workspace.
        /// </summary>
        internal void RemoveProject(AbstractProject project)
        {
            AssertIsForeground();

            lock (_gate)
            {
                Contract.ThrowIfFalse(_projectMap.Remove(project.Id));
            }

            UpdateProjectBinPath(project, project.BinOutputPath, null);

            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                foreach (var hostState in _workspaceHosts)
                {
                    hostState.RemoveProject(project);
                }
            }
        }

        /// <summary>
        /// Updates the project tracker and referencing projects for binary output path change for the given project.
        /// </summary>
        internal void UpdateProjectBinPath(AbstractProject project, string oldBinPathOpt, string newBinPathOpt)
        {
            // UpdateProjectBinPath is defensively executed on the foreground thread as it calls back into referencing projects to perform metadata to P2P reference conversions.
            AssertIsForeground();

            if (oldBinPathOpt != null)
            {
                UpdateReferencesForBinPathChange(oldBinPathOpt, () => RemoveProjectByBinPath(oldBinPathOpt, project));
            }

            if (newBinPathOpt != null)
            {
                UpdateReferencesForBinPathChange(newBinPathOpt, () => AddProjectByBinPath(newBinPathOpt, project));
            }
        }

        private void UpdateReferencesForBinPathChange(string path, Action updateProjects)
        {
            AssertIsForeground();
            // If we already have a single project that points to this path, we'll either be:
            // 
            // (1) removing it, where it no longer exists, or
            // (2) adding another path, where it's now ambiguous
            //
            // in either case, we want to undo file-to-P2P reference conversion

            if (TryGetProjectsByBinPath(path, out var existingProjects))
            {
                if (existingProjects.Length == 1)
                {
                    foreach (var projectToUpdate in ImmutableProjects)
                    {
                        projectToUpdate.UndoProjectReferenceConversionForDisappearingOutputPath(path);
                    }
                }
            }

            updateProjects();

            if (TryGetProjectsByBinPath(path, out existingProjects))
            {
                if (existingProjects.Length == 1)
                {
                    foreach (var projectToUpdate in ImmutableProjects)
                    {
                        projectToUpdate.TryProjectConversionForIntroducedOutputPath(path, existingProjects[0]);
                    }
                }
            }
        }

        /// <summary>
        /// Gets or creates a project ID for the given project file path and display name.
        /// </summary>
        /// <remarks>This method may be called on a background thread.</remarks>
        internal ProjectId GetOrCreateProjectIdForPath(string projectPath, string projectSystemName)
        {
            lock (_gate)
            {
                string key = projectPath + projectSystemName;
                if (!_projectPathToIdMap.TryGetValue(key, out var id))
                {
                    id = ProjectId.CreateNewId(debugName: projectPath);
                    _projectPathToIdMap[key] = id;
                }

                return id;
            }
        }

        /// <summary>
        /// Notifies the workspace host about the given action.
        /// </summary>
        /// <remarks>This method must be called on the foreground thread.</remarks>
        internal void NotifyWorkspaceHosts(Action<IVisualStudioWorkspaceHost> action)
        {
            AssertIsForeground();

            // We do not want to allow message pumping/reentrancy when processing project system changes.
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                foreach (var workspaceHost in _workspaceHosts)
                {
                    if (workspaceHost.HostReadyForEvents)
                    {
                        action(workspaceHost.Host);
                    }
                }
            }
        }

        /// <summary>
        /// Attempts to get single project by given output binary filePath.
        /// </summary>
        /// <remarks>This method may be called on a background thread.</remarks>
        internal bool TryGetProjectByBinPath(string filePath, out AbstractProject project)
        {
            lock (_gate)
            {
                project = null;
                if (_projectsByBinPath.TryGetValue(filePath, out var projects))
                {
                    // If for some reason we have more than one referencing project, it's ambiguous so bail
                    if (projects.Length == 1)
                    {
                        project = projects[0];
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Attempts to get the projects by given output binary filePath.
        /// </summary>
        /// <remarks>This method may be called on a background thread.</remarks>
        internal bool TryGetProjectsByBinPath(string filePath, out ImmutableArray<AbstractProject> projects)
        {
            lock (_gate)
            {
                if (_projectsByBinPath.TryGetValue(filePath, out projects))
                {
                    return true;
                }

                projects = ImmutableArray<AbstractProject>.Empty;
                return false;
            }
        }

        internal void AddProjectByBinPath(string filePath, AbstractProject project)
        {
            lock (_gate)
            {
                if (!_projectsByBinPath.TryGetValue(filePath, out var projects))
                {
                    projects = ImmutableArray<AbstractProject>.Empty;
                }

                _projectsByBinPath[filePath] = projects.Add(project);
            }
        }

        internal void RemoveProjectByBinPath(string filePath, AbstractProject project)
        {
            lock (_gate)
            {
                if (_projectsByBinPath.TryGetValue(filePath, out var projects) && projects.Contains(project))
                {
                    if (projects.Length == 1)
                    {
                        _projectsByBinPath.Remove(filePath);
                    }
                    else
                    {
                        _projectsByBinPath[filePath] = projects.Remove(project);
                    }
                }
            }
        }

        internal void TryDisconnectExistingDeferredProject(IVsHierarchy hierarchy, string projectName)
        {
            var projectPath = AbstractLegacyProject.GetProjectFilePath(hierarchy);
            var projectId = GetOrCreateProjectIdForPath(projectPath, projectName);

            // If we created a project for this while in deferred project load mode, let's close it
            // now that we're being asked to make a "real" project for it, so that we'll prefer the
            // "real" project
            if (VisualStudioWorkspaceImpl.IsDeferredSolutionLoadEnabled(_serviceProvider))
            {
                var existingProject = GetProject(projectId);
                if (existingProject != null)
                {
                    Debug.Assert(existingProject is IWorkspaceProjectContext);
                    existingProject.Disconnect();
                }
            }
        }

        public void OnBeforeCloseSolution()
        {
            AssertIsForeground();

            _solutionIsClosing = true;

            foreach (var p in this.ImmutableProjects)
            {
                p.StopPushingToWorkspaceHosts();
            }

            _solutionLoadComplete = false;
            _deferredLoadWasEnabledForLastSolution = VisualStudioWorkspaceImpl.IsDeferredSolutionLoadEnabled(_serviceProvider);

            // Cancel any background solution parsing. NOTE: This means that work needs to
            // check the token periodically, and whenever resuming from an "await"
            _solutionParsingCancellationTokenSource.Cancel();
            _solutionParsingCancellationTokenSource = new CancellationTokenSource();
        }

        public void OnAfterCloseSolution()
        {
            AssertIsForeground();

            if (_deferredLoadWasEnabledForLastSolution)
            {
                // Copy to avoid modifying the collection while enumerating
                var loadedProjects = ImmutableProjects.ToList();
                foreach (var p in loadedProjects)
                {
                    p.Disconnect();
                }
            }

            lock (_gate)
            {
                Contract.ThrowIfFalse(_projectMap.Count == 0);
            }

            NotifyWorkspaceHosts(host => host.OnSolutionRemoved());
            NotifyWorkspaceHosts(host => host.ClearSolution());

            lock (_gate)
            {
                _projectPathToIdMap.Clear();
            }

            foreach (var workspaceHost in _workspaceHosts)
            {
                workspaceHost.SolutionClosed();
            }

            _solutionIsClosing = false;
        }

        public async Task LoadSolutionFromMSBuildAsync()
        {
            AssertIsForeground();
            InitializeOutputPane();

            // Continue on the UI thread for these operations, since we are touching the VisualStudioWorkspace, etc.
            await PopulateWorkspaceFromDeferredProjectInfoAsync(_solutionParsingCancellationTokenSource.Token).ConfigureAwait(true);
        }

        [Conditional("DEBUG")]
        private void InitializeOutputPane()
        {
            var outputWindow = (IVsOutputWindow)_serviceProvider.GetService(typeof(SVsOutputWindow));
            var paneGuid = new Guid("07aaa8e9-d776-47d6-a1be-5ce00332d74d");
            if (ErrorHandler.Succeeded(outputWindow.CreatePane(ref paneGuid, "Roslyn DPL Status", fInitVisible: 1, fClearWithSolution: 1)) &&
                ErrorHandler.Succeeded(outputWindow.GetPane(ref paneGuid, out _pane)) && _pane != null)
            {
                _pane.Activate();
            }
        }
        private async Task PopulateWorkspaceFromDeferredProjectInfoAsync(
            CancellationToken cancellationToken)
        {
            // NOTE: We need to check cancellationToken after each await, in case the user has
            // already closed the solution.
            AssertIsForeground();

            var componentModel = _serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            var workspaceProjectContextFactory = componentModel.GetService<IWorkspaceProjectContextFactory>();

            var dte = _serviceProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var solutionConfig = (EnvDTE80.SolutionConfiguration2)dte.Solution.SolutionBuild.ActiveConfiguration;

            OutputToOutputWindow($"Getting project information - start");
            var start = DateTimeOffset.UtcNow;

            var projectInfos = SpecializedCollections.EmptyReadOnlyDictionary<string, DeferredProjectInformation>();

            // Note that `solutionConfig` may be null. For example: if the solution doesn't actually
            // contain any projects.
            if (solutionConfig != null)
            {
                // Capture the context so that we come back on the UI thread, and do the actual project creation there.
                var deferredProjectWorkspaceService = _workspaceServices.GetService<IDeferredProjectWorkspaceService>();
                projectInfos = await deferredProjectWorkspaceService.GetDeferredProjectInfoForConfigurationAsync(
                    $"{solutionConfig.Name}|{solutionConfig.PlatformName}",
                    cancellationToken).ConfigureAwait(true);
            }

            AssertIsForeground();
            cancellationToken.ThrowIfCancellationRequested();
            OutputToOutputWindow($"Getting project information - done (took {DateTimeOffset.UtcNow - start})");

            OutputToOutputWindow($"Creating projects - start");
            start = DateTimeOffset.UtcNow;
            var targetPathsToProjectPaths = BuildTargetPathMap(projectInfos);
            var analyzerAssemblyLoader = _workspaceServices.GetRequiredService<IAnalyzerService>().GetLoader();
            foreach (var projectFilename in projectInfos.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GetOrCreateProjectFromArgumentsAndReferences(
                    workspaceProjectContextFactory,
                    analyzerAssemblyLoader,
                    projectFilename,
                    projectInfos,
                    targetPathsToProjectPaths);
            }
            OutputToOutputWindow($"Creating projects - done (took {DateTimeOffset.UtcNow - start})");

            OutputToOutputWindow($"Pushing to workspace - start");
            start = DateTimeOffset.UtcNow;
            FinishLoad();
            OutputToOutputWindow($"Pushing to workspace - done (took {DateTimeOffset.UtcNow - start})");
        }

        private static ImmutableDictionary<string, string> BuildTargetPathMap(IReadOnlyDictionary<string, DeferredProjectInformation> projectInfos)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in projectInfos)
            {
                var targetPath = item.Value.TargetPath;
                if (!string.IsNullOrEmpty(targetPath))
                {
                    if (!builder.ContainsKey(targetPath))
                    {
                        builder[targetPath] = item.Key;
                    }
                    else
                    {
                        Debug.Fail($"Already have a target path of '{item.Value.TargetPath}', with value '{builder[item.Value.TargetPath]}'.");
                    }
                }
            }
            return builder.ToImmutable();
        }

        [Conditional("DEBUG")]
        private void OutputToOutputWindow(string message)
        {
            _pane?.OutputString(message + Environment.NewLine);
        }

        private AbstractProject GetOrCreateProjectFromArgumentsAndReferences(
            IWorkspaceProjectContextFactory workspaceProjectContextFactory,
            IAnalyzerAssemblyLoader analyzerAssemblyLoader,
            string projectFilename,
            IReadOnlyDictionary<string, DeferredProjectInformation> allProjectInfos,
            IReadOnlyDictionary<string, string> targetPathsToProjectPaths)
        {
            var languageName = GetLanguageOfProject(projectFilename);
            if (languageName == null)
            {
                return null;
            }

            if (!allProjectInfos.TryGetValue(projectFilename, out var projectInfo))
            {
                // This could happen if we were called recursively about a dangling P2P reference
                // that isn't actually in the solution.
                return null;
            }

            var commandLineParser = _workspaceServices.GetLanguageServices(languageName).GetService<ICommandLineParserService>();
            var projectDirectory = PathUtilities.GetDirectoryName(projectFilename);
            var commandLineArguments = commandLineParser.Parse(
                projectInfo.CommandLineArguments,
                projectDirectory,
                isInteractive: false,
                sdkDirectory: RuntimeEnvironment.GetRuntimeDirectory());

            // TODO: Should come from sln file?
            var projectName = PathUtilities.GetFileName(projectFilename, includeExtension: false);

            // `AbstractProject` only sets the filename if it actually exists.  Since we want 
            // our ids to match, mimic that behavior here.
            var projectId = File.Exists(projectFilename)
                ? GetOrCreateProjectIdForPath(projectFilename, projectName)
                : GetOrCreateProjectIdForPath(projectName, projectName);
            // See if we've already created this project and we're now in a recursive call to
            // hook up a P2P ref.
            if (_projectMap.TryGetValue(projectId, out var project))
            {
                return project;
            }

            OutputToOutputWindow($"\tCreating '{projectName}':\t{commandLineArguments.SourceFiles.Length} source files,\t{commandLineArguments.MetadataReferences.Length} references.");
            var solution5 = _serviceProvider.GetService(typeof(SVsSolution)) as IVsSolution5;

            // If the index is stale, it might give us a path that doesn't exist anymore that the 
            // solution doesn't know about - be resilient to that case.
            Guid projectGuid;
            try
            {
                projectGuid = solution5.GetGuidOfProjectFile(projectFilename);
            }
            catch (ArgumentException)
            {
                var message = $"Failed to get the project guid for '{projectFilename}' from the solution, using  random guid instead.";
                Debug.Fail(message);
                OutputToOutputWindow(message);
                projectGuid = Guid.NewGuid();
            }

            // NOTE: If the indexing service fails for a project, it will give us an *empty*
            // target path, which we aren't prepared to handle.  Instead, convert it to a *null*
            // value, which we do handle.
            var outputPath = projectInfo.TargetPath;
            if (outputPath == string.Empty)
            {
                outputPath = null;
            }

            var projectContext = workspaceProjectContextFactory.CreateProjectContext(
                languageName,
                projectName,
                projectFilename,
                projectGuid: projectGuid,
                hierarchy: null,
                binOutputPath: outputPath);

            project = (AbstractProject)projectContext;
            projectContext.SetOptions(projectInfo.CommandLineArguments.Join(" "));

            foreach (var sourceFile in commandLineArguments.SourceFiles)
            {
                projectContext.AddSourceFile(sourceFile.Path);
            }

            foreach (var sourceFile in commandLineArguments.AdditionalFiles)
            {
                projectContext.AddAdditionalFile(sourceFile.Path);
            }

            var metadataReferences = commandLineArguments.ResolveMetadataReferences(project.CurrentCompilationOptions.MetadataReferenceResolver).AsImmutable();
            var addedProjectReferences = new HashSet<string>();
            foreach (var projectReferencePath in projectInfo.ReferencedProjectFilePaths)
            {
                var referencedProject = TryFindExistingProjectForProjectReference(projectReferencePath, metadataReferences);
                if (referencedProject == null)
                {
                    referencedProject = GetOrCreateProjectFromArgumentsAndReferences(
                        workspaceProjectContextFactory,
                        analyzerAssemblyLoader,
                        projectReferencePath,
                        allProjectInfos,
                        targetPathsToProjectPaths);
                }

                var referencedProjectContext = referencedProject as IWorkspaceProjectContext;
                if (referencedProjectContext != null)
                {
                    // TODO: Can we get the properties from corresponding metadata reference in
                    // commandLineArguments?
                    addedProjectReferences.Add(projectReferencePath);
                    projectContext.AddProjectReference(
                        referencedProjectContext,
                        new MetadataReferenceProperties());
                }
                else if (referencedProject != null)
                {
                    // This project was already created by the regular project system. See if we
                    // can find the matching project somehow.
                    var existingReferenceOutputPath = referencedProject?.BinOutputPath;
                    if (existingReferenceOutputPath != null)
                    {
                        addedProjectReferences.Add(projectReferencePath);
                        projectContext.AddMetadataReference(
                            existingReferenceOutputPath,
                            new MetadataReferenceProperties());
                    }
                }
                else
                {
                    // We don't know how to create this project.  Another language or something?
                    OutputToOutputWindow($"Failed to create a project for '{projectReferencePath}'.");
                }
            }

            foreach (var reference in metadataReferences)
            {
                var path = GetReferencePath(reference);
                if (targetPathsToProjectPaths.TryGetValue(path, out var possibleProjectReference) &&
                    addedProjectReferences.Contains(possibleProjectReference))
                {
                    // We already added a P2P reference for this, we don't need to add the file reference too.
                    continue;
                }

                projectContext.AddMetadataReference(path, reference.Properties);
            }

            foreach (var reference in commandLineArguments.ResolveAnalyzerReferences(analyzerAssemblyLoader))
            {
                var path = reference.FullPath;
                if (!PathUtilities.IsAbsolute(path))
                {
                    path = PathUtilities.CombineAbsoluteAndRelativePaths(
                        projectDirectory,
                        path);
                }

                projectContext.AddAnalyzerReference(path);
            }

            return (AbstractProject)projectContext;
        }

        private AbstractProject TryFindExistingProjectForProjectReference(string projectReferencePath, ImmutableArray<MetadataReference> metadataReferences)
        {
            // NOTE: ImmutableProjects might contain projects for other languages like
            // Xaml, or Typescript where the project file ends up being identical.
            AbstractProject candidate = null;
            foreach (var existingProject in ImmutableProjects)
            {
                if (existingProject.Language != LanguageNames.CSharp && existingProject.Language != LanguageNames.VisualBasic)
                {
                    continue;
                }

                if (!StringComparer.OrdinalIgnoreCase.Equals(existingProject.ProjectFilePath, projectReferencePath))
                {
                    continue;
                }

                // There might be multiple projects that have the same project file path and language in the case of
                // cross-targeted .NET Core project.  To support that, we'll try to find the one with the output path
                // that matches one of the metadata references we're trying to add.  If we don't find any, then we'll
                // settle one with a matching project file path.
                candidate = candidate ?? existingProject;
                if (metadataReferences.Contains(mr => StringComparer.OrdinalIgnoreCase.Equals(GetReferencePath(mr), existingProject.BinOutputPath)))
                {
                    return existingProject;
                }
            }

            return candidate;
        }

        private static string GetReferencePath(MetadataReference mr)
        {
            // Some references may fail to be resolved - if they are, we'll still pass them
            // through, in case they come into existence later (they may be built by other 
            // parts of the build system).
            return mr is UnresolvedMetadataReference umr
                ? umr.Reference
                : ((PortableExecutableReference)mr).FilePath;
        }

        private static string GetLanguageOfProject(string projectFilename)
        {
            switch (PathUtilities.GetExtension(projectFilename))
            {
                case ".csproj":
                    return LanguageNames.CSharp;
                case ".vbproj":
                    return LanguageNames.VisualBasic;
                default:
                    return null;
            };
        }

        private void FinishLoad()
        {
            // We are now completely done, so let's simply ensure all projects are added.
            StartPushingToWorkspaceAndNotifyOfOpenDocuments(this.ImmutableProjects);

            // Also, all remaining project adds need to immediately pushed as well, since we're now "interactive"
            _solutionLoadComplete = true;

            // Check that the set of analyzers is complete and consistent.
            GetAnalyzerDependencyCheckingService()?.CheckForConflictsAsync();
        }

        private AnalyzerDependencyCheckingService GetAnalyzerDependencyCheckingService()
        {
            var componentModel = (IComponentModel)_serviceProvider.GetService(typeof(SComponentModel));

            return componentModel.GetService<AnalyzerDependencyCheckingService>();
        }

        internal void OnBeforeLoadProjectBatch(bool fIsBackgroundIdleBatch)
        {
            AssertIsForeground();

            _projectsLoadedThisBatch.Clear();
        }

        internal void OnAfterLoadProjectBatch(bool fIsBackgroundIdleBatch)
        {
            AssertIsForeground();

            if (!fIsBackgroundIdleBatch)
            {
                // This batch was loaded eagerly. This might be because the user is force expanding the projects in the
                // Solution Explorer, or they had some files open in an .suo we need to push.
                StartPushingToWorkspaceAndNotifyOfOpenDocuments(_projectsLoadedThisBatch);
            }

            _projectsLoadedThisBatch.Clear();
        }

        internal void OnAfterBackgroundSolutionLoadComplete()
        {
            AssertIsForeground();

            // In Non-DPL scenarios, this indicates that ASL is complete, and we should push any
            // remaining information we have to the Workspace.  If DPL is enabled, this is never
            // called.
            FinishLoad();
        }
    }
}