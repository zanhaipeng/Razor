// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.LanguageServices.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.LanguageServices;
using Microsoft.VisualStudio.ProjectSystem.LanguageServices.Handlers;
using Microsoft.VisualStudio.ProjectSystem.Logging;

namespace Microsoft.VisualStudio.ProjectSystem.Razor
{
    internal partial class ContentItemHandler : AbstractEvaluationCommandLineHandler, IEvaluationHandler, ICommandLineHandler
    {
        private Dictionary<string, string> _files;
        private string _generatedFilesRoot;

        private readonly UnconfiguredProject _project;
        private readonly IWorkspaceProjectContext _context;

        public ContentItemHandler(UnconfiguredProject project, IWorkspaceProjectContext context)
            : base(project)
        {
            Requires.NotNull(project, nameof(project));
            Requires.NotNull(context, nameof(context));

            _project = project;
            _context = context;


            while (true)
            {
                _generatedFilesRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

                try
                {
                    Directory.CreateDirectory(_generatedFilesRoot);
                    break;
                }
                catch (Exception)
                {
                }
            }

            _files = new Dictionary<string, string>();
        }

        public void Handle(IComparable version, IProjectChangeDescription projectChange, bool isActiveContext, IProjectLogger logger)
        {
            Requires.NotNull(version, nameof(version));
            Requires.NotNull(projectChange, nameof(projectChange));
            Requires.NotNull(logger, nameof(logger));

            ApplyEvaluationChanges(version, projectChange.Difference, projectChange.After.Items, isActiveContext, logger);
        }

        public void Handle(IComparable version, BuildOptions added, BuildOptions removed, bool isActiveContext, IProjectLogger logger)
        {
            Requires.NotNull(version, nameof(version));
            Requires.NotNull(added, nameof(added));
            Requires.NotNull(removed, nameof(removed));
            Requires.NotNull(logger, nameof(logger));

            IProjectChangeDiff difference = ConvertToProjectDiff(added, removed);

            ApplyDesignTimeChanges(version, difference, isActiveContext, logger);
        }

        protected override void AddToContext(string fullPath, IImmutableDictionary<string, string> metadata, bool isActiveContext, IProjectLogger logger)
        {
            if (Path.GetExtension(fullPath) != ".cshtml")
            {
                return;
            }

            string[] folderNames = GetFolderNames(fullPath, metadata);

            var output = Path.Combine(_generatedFilesRoot, string.Join("\\", folderNames), Path.GetFileNameWithoutExtension(fullPath) + ".cs");
            _files.Add(fullPath, output);

            var thingy =_project.ProjectService.Services.ExportProvider.GetExportedValue<IThingDoer>();
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            using (var stream = File.OpenWrite(output))
            {
                thingy.DoTheNeedful(Path.GetDirectoryName(_context.ProjectFilePath), fullPath, stream);
            }

            logger.WriteLine("Adding razor file '{0}'", output);
            _context.AddSourceFile(output, isInCurrentContext: isActiveContext, folderNames: folderNames);
        }

        protected override void RemoveFromContext(string fullPath, IProjectLogger logger)
        {
            if (Path.GetExtension(fullPath) != ".cshtml")
            {
                return;
            }

            logger.WriteLine("Removing razor file '{0}'", fullPath);
            var output = _files[fullPath];
            _files.Remove(fullPath);
            File.Delete(output);

            _context.RemoveSourceFile(output);
        }

        private string[] GetFolderNames(string fullPath, IImmutableDictionary<string, string> metadata)
        {
            // Roslyn wants the effective set of folders from the source up to, but not including the project 
            // root to handle the cases where linked files have a different path in the tree than what its path 
            // on disk is. It uses these folders for code actions that create files alongside others, such as 
            // extract interface.

            // First we check for a linked item, and we use its effective folder in 
            // the tree, otherwise, we just use the parent folder of the file itself
            string parentFolder = GetLinkedParentFolder(metadata);
            if (parentFolder == null)
                parentFolder = GetParentFolder(fullPath);

            // We now have a folder in the form of `Folder1\Folder2` relative to the
            // project directory  split it up into individual path components
            if (parentFolder.Length > 0)
            {
                return parentFolder.Split(FileItemServices.PathSeparatorCharacters);
            }

            return Array.Empty<string>();
        }

        private string GetLinkedParentFolder(IImmutableDictionary<string, string> metadata)
        {
            string linkFilePath = FileItemServices.GetLinkFilePath(metadata);
            if (linkFilePath != null)
                return Path.GetDirectoryName(linkFilePath);

            return null;
        }

        private string GetParentFolder(string fullPath)
        {
            return Path.GetDirectoryName(_project.MakeRelative(fullPath));
        }

        private IProjectChangeDiff ConvertToProjectDiff(BuildOptions added, BuildOptions removed)
        {
            var addedSet = ImmutableHashSet.ToImmutableHashSet(GetFilePaths(added), StringComparers.Paths);
            var removedSet = ImmutableHashSet.ToImmutableHashSet(GetFilePaths(removed), StringComparers.Paths);

            return new ProjectChangeDiff(addedSet, removedSet);
        }

        private IEnumerable<string> GetFilePaths(BuildOptions options)
        {
            return options.SourceFiles.Select(f => _project.MakeRelative(f.Path));
        }
    }
}
