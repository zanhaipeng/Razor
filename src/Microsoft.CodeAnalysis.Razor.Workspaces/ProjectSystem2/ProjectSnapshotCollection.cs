using System.Collections.Generic;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem2
{
    internal abstract class ProjectSnapshotCollection
    {
        IReadOnlyList<ProjectSnapshot> Projects { get; }
    }
}
