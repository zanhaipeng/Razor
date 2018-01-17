using Microsoft.CodeAnalysis.Host;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem2
{
    internal abstract class RazorWorkspace
    {
        public HostLanguageServices LanguageServices { get; }

        public ProjectSnapshotCollection Current { get; }

        public Workspace Workspace { get; }
    }
}
