
using System.Collections.Generic;
using Microsoft.AspNetCore.Razor.Language;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem3
{
    internal abstract class ProjectSnapshot
    {
        public abstract ProjectConfigurationSnapshot Configuration { get; }

        public abstract Project WorkspaceProject { get; }

        public abstract IReadOnlyList<TagHelperDescriptor> TagHelpers { get; }
    }
}
