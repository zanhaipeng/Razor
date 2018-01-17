
namespace Microsoft.CodeAnalysis.Razor.ProjectSystem3
{
    internal static class WorkspaceExtensions
    {
        public static ProjectSnapshotCollectionHost GetRazorProjectHost(this Workspace workspace)
        {
            return workspace.Services.GetRequiredService<ProjectSnapshotCollectionHost>();
        }
    }
}
