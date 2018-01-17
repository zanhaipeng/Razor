
using System;
using Microsoft.CodeAnalysis.Host;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem3
{
    internal class ProjectSnapshotCollectionHost : IWorkspaceService
    {
        public event EventHandler Changed; // This would be pretty similar to what we already have.

        public ProjectSnapshotCollection Current { get; }

        public Workspace Workspace { get; }

    }
}
