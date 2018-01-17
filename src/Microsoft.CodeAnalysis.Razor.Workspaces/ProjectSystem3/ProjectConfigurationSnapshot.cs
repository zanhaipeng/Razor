using System.Collections.Generic;
using Microsoft.AspNetCore.Razor.Language;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem3
{
    internal abstract class ProjectConfigurationSnapshot
    {
        public RazorParserOptions ParserOptions { get; }

        public RazorCodeGenerationOptions CodeGenerationOptions { get; }

        public IReadOnlyList<ProjectPlugin> Plugins { get; }
    }
}
